namespace ClaudeTerminals.Relay;

/// <summary>A session as the relay knows it: what another session is told about it, and what rules are judged by.</summary>
/// <param name="Project">Its project's id (sessions in the same project share it), or null.</param>
/// <param name="ProjectName">That project's name, as other sessions are told it.</param>
internal sealed record RelayPeer(string Id, string Name, string? Project, string? ProjectName, string? Agent, bool Running);

/// <param name="ReplyTo">The message this answers, if any (which lets it through: see <see cref="RelayHub.Send"/>).</param>
internal sealed record RelayMessage(string Id, string From, string To, string Text, DateTime Sent, string? ReplyTo);

internal enum SendOutcome { Delivered, Waiting, Denied }

internal sealed record SendResult(SendOutcome Outcome, RelayMessage? Message);

/// <summary>Messages from one session to another waiting for you to say whether it may.</summary>
internal sealed record RelayAsk(string From, string To, int Count);

/// <summary>Something an agent asked of the relay that it can't have; the message says why, for the agent.</summary>
internal sealed class RelayException(string message) : Exception(message);

/// <summary>
/// Carries messages between sessions. The sender is the session the app knows made the call (its panel token), never
/// a name it gives. A message goes into the other session's inbox only as <see cref="RelayPolicy"/> allows; with no
/// rule it waits while you're asked (<see cref="Asked"/>), and is delivered or dropped by your answer. The receiving
/// agent reads its inbox through a tool, so a message reaches it as data from another agent, not as your prompt.
/// Thread safe; events are raised on the caller's thread, outside the lock.
/// </summary>
internal sealed class RelayHub
{
    public const int MaxText = 8000;
    public const int MaxInbox = 100;
    public const int MaxWaiting = 20;
    public const int PerMinute = 20;
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromHours(1);

    private readonly RelayPolicy policy;
    private readonly Func<DateTime> now;
    private readonly Lock gate = new();
    private Dictionary<string, RelayPeer> peers = [];
    private readonly Dictionary<string, List<RelayMessage>> inboxes = [];
    private readonly Dictionary<(string From, string To), List<RelayMessage>> waiting = [];
    private readonly Dictionary<string, Queue<DateTime>> sentBy = [];
    private readonly List<RelayMessage> delivered = [];   // the latest, for replies

    public RelayHub(RelayPolicy policy, Func<DateTime>? now = null)
    {
        this.policy = policy;
        this.now = now ?? (() => DateTime.UtcNow);
        policy.Changed += Recheck;
    }

    public RelayPolicy Policy => policy;

    /// <summary>A pair of sessions has a first message waiting for your answer.</summary>
    public event Action<RelayAsk>? Asked;

    /// <summary>A message went into a session's inbox.</summary>
    public event Action<RelayMessage>? Delivered;

    /// <summary>What's waiting changed (asked, answered, or a session gone).</summary>
    public event Action? Changed;

    /// <summary>The sessions there are now; inboxes and waiting messages of sessions gone are dropped.</summary>
    public void SetPeers(IEnumerable<RelayPeer> list)
    {
        var dropped = 0;
        lock (gate)
        {
            peers = list.ToDictionary(p => p.Id);
            foreach (var key in waiting.Keys.Where(k => !peers.ContainsKey(k.From) || !peers.ContainsKey(k.To)).ToList())
                dropped += waiting.Remove(key) ? 1 : 0;
            foreach (var id in inboxes.Keys.Where(id => !peers.ContainsKey(id)).ToList()) inboxes.Remove(id);
        }
        if (dropped > 0) Changed?.Invoke();
    }

    public RelayPeer? Peer(string id)
    {
        lock (gate) return peers.GetValueOrDefault(id);
    }

    /// <summary>The session <paramref name="key"/> names: its id, or its name when only one session has it.</summary>
    public RelayPeer Find(string key)
    {
        lock (gate)
        {
            if (peers.TryGetValue(key, out var p)) return p;
            var named = peers.Values.Where(x => string.Equals(x.Name, key.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            return named.Count switch
            {
                1 => named[0],
                0 => throw new RelayException($"There's no session \"{key}\". list_sessions shows the sessions there are."),
                _ => throw new RelayException($"{named.Count} sessions are called \"{key}\"; give its id instead (list_sessions shows them)."),
            };
        }
    }

    /// <summary>The other sessions, with what a message from <paramref name="from"/> to each would get.</summary>
    public List<(RelayPeer Peer, RelayDecision Decision)> Others(string from)
    {
        lock (gate)
        {
            var me = peers.GetValueOrDefault(from);
            return peers.Values.Where(p => p.Id != from)
                .Select(p => (p, policy.Paused ? RelayDecision.Deny : policy.Decide(from, p.Id, SameProject(me, p)).Decision)).ToList();
        }
    }

    /// <summary>
    /// Sends <paramref name="text"/> from <paramref name="from"/> to the session <paramref name="to"/> names. A reply to
    /// a message that session sent this one within <see cref="ReplyWindow"/> always goes through, so asking a question
    /// lets it be answered; anything else follows the rules.
    /// </summary>
    public SendResult Send(string from, string to, string text, string? replyTo = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new RelayException("There's nothing to send.");
        if (text.Length > MaxText) throw new RelayException($"A message can be up to {MaxText} characters; this one is {text.Length}.");
        if (policy.Paused) throw new RelayException("Messages between sessions are paused in Claude Terminals; nothing was sent.");
        var target = Find(to);
        RelayAsk? ask = null;
        SendResult result;
        lock (gate)
        {
            var me = peers.GetValueOrDefault(from) ?? throw new RelayException("This session isn't known to Claude Terminals.");
            if (target.Id == from) throw new RelayException("That's this session.");
            var t = now();
            var sent = sentBy.TryGetValue(from, out var q) ? q : sentBy[from] = new Queue<DateTime>();
            while (sent.Count > 0 && t - sent.Peek() > TimeSpan.FromMinutes(1)) sent.Dequeue();
            if (sent.Count >= PerMinute) throw new RelayException($"This session has sent {PerMinute} messages in the last minute; wait a little.");

            var message = new RelayMessage(Guid.NewGuid().ToString("N")[..12], from, target.Id, text, t, replyTo);
            var isReply = replyTo != null && delivered.Any(m => m.Id == replyTo && m.From == target.Id && m.To == from && t - m.Sent < ReplyWindow);
            var decision = isReply ? RelayDecision.Allow : policy.Decide(from, target.Id, SameProject(me, target)).Decision;
            switch (decision)
            {
                case RelayDecision.Deny:
                    return new SendResult(SendOutcome.Denied, null);
                case RelayDecision.Allow:
                    sent.Enqueue(t);
                    Deliver(message);
                    result = new SendResult(SendOutcome.Delivered, message);
                    break;
                default:
                    var key = (from, target.Id);
                    var list = waiting.TryGetValue(key, out var l) ? l : waiting[key] = [];
                    if (list.Count >= MaxWaiting) throw new RelayException($"{MaxWaiting} messages to \"{target.Name}\" are already waiting for the person's answer.");
                    sent.Enqueue(t);
                    list.Add(message);
                    if (list.Count == 1) ask = new RelayAsk(from, target.Id, 1);
                    result = new SendResult(SendOutcome.Waiting, message);
                    break;
            }
        }
        if (result.Outcome == SendOutcome.Delivered) Delivered?.Invoke(result.Message!);
        if (ask != null) { Asked?.Invoke(ask); Changed?.Invoke(); }
        return result;
    }

    /// <summary>Takes the session's unread messages, oldest first.</summary>
    public List<RelayMessage> Read(string session)
    {
        List<RelayMessage>? list;
        lock (gate) inboxes.Remove(session, out list);
        if (list == null) return [];
        Changed?.Invoke();   // its unread count is back to none
        return list;
    }

    public int Unread(string session)
    {
        lock (gate) return inboxes.TryGetValue(session, out var l) ? l.Count : 0;
    }

    /// <summary>What's waiting for your answer, by pair.</summary>
    public List<RelayAsk> Waiting()
    {
        lock (gate) return waiting.Select(w => new RelayAsk(w.Key.From, w.Key.To, w.Value.Count)).ToList();
    }

    /// <summary>The rules changed: waiting messages now allowed are delivered, and refused ones dropped.</summary>
    private void Recheck()
    {
        var out_ = new List<RelayMessage>();
        var changed = false;
        lock (gate)
        {
            foreach (var (key, list) in waiting.ToList())
            {
                if (!peers.TryGetValue(key.From, out var a) || !peers.TryGetValue(key.To, out var b)) continue;
                var d = policy.Paused ? RelayDecision.Ask : policy.Decide(key.From, key.To, SameProject(a, b)).Decision;
                if (d == RelayDecision.Ask) continue;
                waiting.Remove(key);
                changed = true;
                if (d == RelayDecision.Allow)
                    foreach (var m in list) { var sent = m with { Sent = now() }; Deliver(sent); out_.Add(sent); }
            }
        }
        foreach (var m in out_) Delivered?.Invoke(m);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Drops what's waiting from one session to another without making a rule ("not this time").</summary>
    public void Dismiss(string from, string to)
    {
        bool removed;
        lock (gate) removed = waiting.Remove((from, to));
        if (removed) Changed?.Invoke();
    }

    private void Deliver(RelayMessage m)
    {
        var inbox = inboxes.TryGetValue(m.To, out var l) ? l : inboxes[m.To] = [];
        inbox.Add(m);
        if (inbox.Count > MaxInbox) inbox.RemoveAt(0);
        delivered.Add(m);
        if (delivered.Count > 500) delivered.RemoveRange(0, delivered.Count - 500);
    }

    private static bool SameProject(RelayPeer? a, RelayPeer? b) => a?.Project != null && a.Project == b?.Project;
}
