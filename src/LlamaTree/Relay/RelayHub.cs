using System.Text.Json;

namespace LlamaTree.Relay;

/// <summary>A session as the relay knows it: what another session is told about it, and what rules are judged by.</summary>
/// <param name="Project">Its project's id (sessions in the same project share it), or null.</param>
/// <param name="ProjectName">That project's name, as other sessions are told it.</param>
/// <param name="Inbox">Whether others may message it first (a session always; a plugin only with "inbox": true).</param>
internal sealed record RelayPeer(string Id, string Name, string? Project, string? ProjectName, string? Agent, bool Running, bool Inbox = true);

/// <param name="ReplyTo">The message this answers, if any (which lets it through: see <see cref="RelayHub.Send"/>).</param>
/// <param name="Thread">The first message of the exchange it's part of (its own id when it starts one).</param>
/// <param name="Turn">Which message of that exchange it is, from 1 (see <see cref="RelayHub.MaxTurns"/>).</param>
internal sealed record RelayMessage(string Id, string From, string To, string Text, DateTime Sent, string? ReplyTo, string Thread, int Turn);

internal enum SendOutcome { Delivered, Waiting, Denied }

internal sealed record SendResult(SendOutcome Outcome, RelayMessage? Message);

/// <summary>Messages from one session to another waiting for you to say whether it may.</summary>
internal sealed record RelayAsk(string From, string To, int Count);

/// <summary>What became of a message: delivered, read, waiting for your answer, refused, or dropped (Not now, or a session gone).</summary>
internal enum RelayStatus { Waiting, Delivered, Read, Refused, Dropped }

/// <summary>A message in the log, with what became of it and when.</summary>
internal sealed record RelayLogEntry(RelayMessage Message, RelayStatus Status, DateTime At);

/// <summary>Something an agent asked of the relay that it can't have; the message says why, for the agent.</summary>
internal sealed class RelayException(string message) : Exception(message);

/// <summary>
/// Carries messages between sessions. The sender is the session the app knows made the call (its panel token), never
/// a name it gives. A message goes into the other session's inbox only as <see cref="RelayPolicy"/> allows; with no
/// rule it waits while you're asked (<see cref="Asked"/>), and is delivered or dropped by your answer. The receiving
/// agent reads its inbox through a tool, so a message reaches it as data from another agent, not as your prompt.
/// Every message is logged (<see cref="Log"/>). Unread and waiting messages and the log are kept between runs (a file
/// written shortly after each change, and by <see cref="Flush"/>). Thread safe; events are raised on the caller's thread,
/// outside the lock.
/// </summary>
internal sealed class RelayHub
{
    public const int MaxText = 8000;
    public const int MaxInbox = 100;
    public const int MaxWaiting = 20;
    public const int PerMinute = 20;
    public const int MaxLog = 500;

    /// <summary>
    /// How many messages one exchange may run to (a message and the replies to it, and to them): two agents allowed to
    /// answer each other could otherwise go on for ever. Past it a reply is refused; a new message starts afresh, which an
    /// agent is told to do only when the person asks it to.
    /// </summary>
    public const int MaxTurns = 12;

    public static readonly TimeSpan ReplyWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(110);

    private readonly RelayPolicy policy;
    private readonly Func<DateTime> now;
    private readonly Lock gate = new();
    private Dictionary<string, RelayPeer> peers = [];
    private readonly Dictionary<string, List<RelayMessage>> inboxes = [];
    private readonly Dictionary<(string From, string To), List<RelayMessage>> waiting = [];
    private readonly Dictionary<string, Queue<DateTime>> sentBy = [];
    private readonly List<RelayLogEntry> log = [];
    private readonly Dictionary<string, TaskCompletionSource> arrivals = [];   // session → done when something reaches its inbox
    private int logVersion;

    private readonly string? path;
    private System.Threading.Timer? saveTimer;

    /// <param name="path">Where unread and waiting messages and the log are kept between runs; null for this run only.</param>
    public RelayHub(RelayPolicy policy, Func<DateTime>? now = null, string? path = null)
    {
        this.policy = policy;
        this.now = now ?? (() => DateTime.UtcNow);
        this.path = path;
        if (path != null) Load(path);
        policy.Changed += Recheck;
        Changed += ScheduleSave;   // everything that changes what's kept raises Changed
    }

    // ---- kept between runs

    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    private sealed record Waits(string From, string To, List<RelayMessage> Messages);
    private sealed record Kept(Dictionary<string, List<RelayMessage>> Inboxes, List<Waits> Waiting, List<RelayLogEntry> Log);

    private void Load(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonSerializer.Deserialize<Kept>(File.ReadAllText(file), FileOptions) is not { } kept) return;
            foreach (var (id, list) in kept.Inboxes ?? []) if (list?.Count > 0) inboxes[id] = list;
            foreach (var w in kept.Waiting ?? []) if (w.Messages?.Count > 0) waiting[(w.From, w.To)] = w.Messages;
            log.AddRange((kept.Log ?? []).TakeLast(MaxLog));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file loses the messages kept, not the app.
        }
    }

    /// <summary>Writes what's kept a moment after the last change, so a burst of messages is written once.</summary>
    private void ScheduleSave()
    {
        if (path == null) return;
        lock (gate)
        {
            saveTimer ??= new System.Threading.Timer(_ => Flush());
            saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes what's kept now (as the app closes).</summary>
    public void Flush()
    {
        if (path == null) return;
        string json;
        lock (gate)
        {
            json = JsonSerializer.Serialize(new Kept(
                inboxes.ToDictionary(x => x.Key, x => x.Value.ToList()),
                waiting.Select(w => new Waits(w.Key.From, w.Key.To, w.Value.ToList())).ToList(),
                [.. log]), FileOptions);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Written next time something changes.
        }
    }

    public RelayPolicy Policy => policy;

    /// <summary>
    /// Decides for messages the rules don't cover (to or from a plugin): Allow or Deny, or null to leave it to the rules. Given
    /// the sender, the receiver and whether it's a reply. It can refuse even a reply.
    /// </summary>
    public Func<RelayPeer, RelayPeer, bool, RelayDecision?>? Gate { get; set; }

    /// <summary>A pair of sessions has a first message waiting for your answer.</summary>
    public event Action<RelayAsk>? Asked;

    /// <summary>A message went into a session's inbox.</summary>
    public event Action<RelayMessage>? Delivered;

    /// <summary>What's waiting, unread or logged changed.</summary>
    public event Action? Changed;

    /// <summary>The sessions there are now; inboxes and waiting messages of sessions gone are dropped.</summary>
    public void SetPeers(IEnumerable<RelayPeer> list)
    {
        var dropped = 0;
        lock (gate)
        {
            peers = list.ToDictionary(p => p.Id);
            static bool Plugin(string id) => id.StartsWith("plugin:", StringComparison.Ordinal);
            bool Gone(string id) => !Plugin(id) && !peers.ContainsKey(id);
            foreach (var key in waiting.Keys.Where(k => Gone(k.From) || Gone(k.To)).ToList())
            {
                foreach (var m in waiting[key]) SetStatus(m, RelayStatus.Dropped);
                dropped += waiting.Remove(key) ? 1 : 0;
            }
            foreach (var id in inboxes.Keys.Where(Gone).ToList())
            {
                foreach (var m in inboxes[id]) SetStatus(m, RelayStatus.Dropped);
                inboxes.Remove(id);
            }
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
                .Select(p => (p, policy.Paused ? RelayDecision.Deny
                    : (me != null ? Gate?.Invoke(me, p, false) : null) ?? policy.Decide(from, p.Id, SameProject(me, p)).Decision)).ToList();
        }
    }

    /// <summary>
    /// Sends <paramref name="text"/> from <paramref name="from"/> to the session <paramref name="to"/> names. A reply to
    /// a message that session sent this one within <see cref="ReplyWindow"/> always goes through, so asking a question
    /// lets it be answered, up to <see cref="MaxTurns"/> messages in one exchange; anything else follows the rules.
    /// </summary>
    public SendResult Send(string from, string to, string text, string? replyTo = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new RelayException("There's nothing to send.");
        if (text.Length > MaxText) throw new RelayException($"A message can be up to {MaxText} characters; this one is {text.Length}.");
        if (policy.Paused) throw new RelayException("Messages between sessions are paused in LlamaTree; nothing was sent.");
        var target = Find(to);
        RelayAsk? ask = null;
        SendResult result;
        lock (gate)
        {
            var me = peers.GetValueOrDefault(from) ?? throw new RelayException("This session isn't known to LlamaTree.");
            if (target.Id == from) throw new RelayException("That's this session.");
            var t = now();
            var sent = sentBy.TryGetValue(from, out var q) ? q : sentBy[from] = new Queue<DateTime>();
            while (sent.Count > 0 && t - sent.Peek() > TimeSpan.FromMinutes(1)) sent.Dequeue();
            if (sent.Count >= PerMinute) throw new RelayException($"This session has sent {PerMinute} messages in the last minute; wait a little.");

            // Only a message the other session sent this one makes it a reply; naming any other id starts a new exchange.
            var parent = replyTo == null ? null : log.Select(e => e.Message).LastOrDefault(m => m.Id == replyTo && m.From == target.Id && m.To == from);
            if (parent != null && parent.Turn >= MaxTurns)
                throw new RelayException($"This exchange with \"{target.Name}\" has reached {MaxTurns} messages, so the reply wasn't sent. " +
                    "Tell the person where it stands; send a new message (without reply_to) only if they ask you to carry on.");
            var id = Guid.NewGuid().ToString("N")[..12];
            var message = new RelayMessage(id, from, target.Id, text, t, parent?.Id, parent?.Thread ?? id, (parent?.Turn ?? 0) + 1);
            var isReply = parent != null && t - parent.Sent < ReplyWindow;
            var gated = Gate?.Invoke(me, target, isReply);
            var decision = gated == RelayDecision.Deny ? RelayDecision.Deny : isReply ? RelayDecision.Allow : gated ?? policy.Decide(from, target.Id, SameProject(me, target)).Decision;
            switch (decision)
            {
                case RelayDecision.Deny:
                    AddLog(message, RelayStatus.Refused);
                    result = new SendResult(SendOutcome.Denied, null);
                    break;
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
                    AddLog(message, RelayStatus.Waiting);
                    if (list.Count == 1) ask = new RelayAsk(from, target.Id, 1);
                    result = new SendResult(SendOutcome.Waiting, message);
                    break;
            }
        }
        if (result.Outcome == SendOutcome.Delivered) Delivered?.Invoke(result.Message!);
        if (ask != null) Asked?.Invoke(ask);
        Changed?.Invoke();
        return result;
    }

    /// <summary>Takes the session's unread messages, oldest first.</summary>
    public List<RelayMessage> Read(string session)
    {
        List<RelayMessage>? list;
        lock (gate)
        {
            inboxes.Remove(session, out list);
            foreach (var m in list ?? []) SetStatus(m, RelayStatus.Read);
        }
        if (list == null) return [];
        Changed?.Invoke();   // its unread count is back to none
        return list;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> (at most <see cref="MaxWait"/>) for the reply to <paramref name="messageId"/>,
    /// a message <paramref name="session"/> sent, and takes it from its inbox; null if none came in time. Other messages
    /// arriving meanwhile stay unread.
    /// </summary>
    public async Task<RelayMessage?> WaitForReplyAsync(string session, string messageId, TimeSpan timeout, CancellationToken cancel = default)
    {
        lock (gate)
        {
            if (!log.Any(e => e.Message.Id == messageId && e.Message.From == session))
                throw new RelayException($"This session sent no message {messageId}. send_message gives the id of each message it sends.");
        }
        // Real time, whatever clock messages are stamped with: this is how long the agent's tool call is held open.
        var until = DateTime.UtcNow + (timeout > MaxWait ? MaxWait : timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        while (true)
        {
            Task arrived;
            RelayMessage? reply = null;
            lock (gate)
            {
                if (inboxes.TryGetValue(session, out var inbox) && inbox.FindIndex(m => m.ReplyTo == messageId) is var i and >= 0)
                {
                    reply = inbox[i];
                    inbox.RemoveAt(i);
                    if (inbox.Count == 0) inboxes.Remove(session);
                    SetStatus(reply, RelayStatus.Read);
                }
                arrived = (arrivals.TryGetValue(session, out var tcs) ? tcs : arrivals[session] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            if (reply != null) { Changed?.Invoke(); return reply; }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            await Task.WhenAny(arrived, Task.Delay(left, cancel));
            cancel.ThrowIfCancellationRequested();
        }
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

    /// <summary>Goes up whenever the log changes, so a page showing it knows to read it again.</summary>
    public int LogVersion { get { lock (gate) return logVersion; } }

    /// <summary>The latest messages and what became of each, newest first.</summary>
    public List<RelayLogEntry> Log()
    {
        lock (gate) return Enumerable.Reverse(log).ToList();
    }

    /// <summary>Empties the log (messages still unread or waiting stay where they are).</summary>
    public void ClearLog()
    {
        lock (gate) { log.RemoveAll(e => e.Status is not (RelayStatus.Waiting or RelayStatus.Delivered)); logVersion++; }
        Changed?.Invoke();
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
                foreach (var m in list)
                {
                    if (d == RelayDecision.Deny) { SetStatus(m, RelayStatus.Refused); continue; }
                    var sent = m with { Sent = now() };
                    Deliver(sent);
                    out_.Add(sent);
                }
            }
        }
        foreach (var m in out_) Delivered?.Invoke(m);
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Your answer to what waits from one session to another without a rule: delivered this once (<paramref name="deliver"/>),
    /// or dropped ("Not now"). The next message asks again.
    /// </summary>
    public void AnswerOnce(string from, string to, bool deliver)
    {
        List<RelayMessage>? list;
        var out_ = new List<RelayMessage>();
        lock (gate)
        {
            if (!waiting.Remove((from, to), out list)) return;
            foreach (var m in list)
            {
                if (!deliver) { SetStatus(m, RelayStatus.Dropped); continue; }
                var sent = m with { Sent = now() };
                Deliver(sent);
                out_.Add(sent);
            }
        }
        foreach (var m in out_) Delivered?.Invoke(m);
        Changed?.Invoke();
    }

    private void Deliver(RelayMessage m)
    {
        var inbox = inboxes.TryGetValue(m.To, out var l) ? l : inboxes[m.To] = [];
        inbox.Add(m);
        if (inbox.Count > MaxInbox) { SetStatus(inbox[0], RelayStatus.Dropped); inbox.RemoveAt(0); }
        // Delivered after waiting: its entry is the same message, sent anew.
        var i = log.FindIndex(e => e.Message.Id == m.Id);
        if (i >= 0) { log[i] = new RelayLogEntry(m, RelayStatus.Delivered, now()); logVersion++; }
        else AddLog(m, RelayStatus.Delivered);
        if (arrivals.Remove(m.To, out var tcs)) tcs.TrySetResult();
    }

    private void AddLog(RelayMessage m, RelayStatus status)
    {
        logVersion++;
        log.Add(new RelayLogEntry(m, status, now()));
        if (log.Count > MaxLog) log.RemoveRange(0, log.Count - MaxLog);
    }

    private void SetStatus(RelayMessage m, RelayStatus status)
    {
        var i = log.FindIndex(e => e.Message.Id == m.Id);
        if (i >= 0) { log[i] = log[i] with { Status = status, At = now() }; logVersion++; }
    }

    private static bool SameProject(RelayPeer? a, RelayPeer? b) => a?.Project != null && a.Project == b?.Project;
}
