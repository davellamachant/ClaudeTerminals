namespace LlamaTree.Relay;

/// <summary>A session as session control knows it.</summary>
/// <param name="StartedBy">The session that started this one through <see cref="SessionControl.StartAsync"/>, if one did.</param>
/// <param name="IsAgent">It runs a coding agent (only those can use the tools, or be started).</param>
/// <param name="Server">The server it runs on (how it's reached), or null for this computer: its folders are that computer's.</param>
internal sealed record ControlSession(string Id, string Name, string Directory, string? Project, string? StartedBy, bool IsAgent, bool Running, string? Server = null);

/// <summary>What a session's agent was last asked and answered, from its conversation.</summary>
/// <param name="PendingTool">The tool call its last reply ends with, if any: what a permission prompt is about.</param>
internal sealed record SessionReport(string? LastPrompt, string? LastReply, string? PendingTool);

/// <summary>What <see cref="SessionControl"/> needs of the app: its sessions, and doing things to them.</summary>
internal interface IControlHost
{
    IReadOnlyList<ControlSession> Sessions();

    /// <summary>
    /// Adds and starts a session running <paramref name="parent"/>'s agent with its options, in a new conversation that
    /// starts with <paramref name="prompt"/>; its id. With <paramref name="within"/>, the folder must really be inside that
    /// one, links resolved, on the computer it runs on (a link inside an allowed folder can lead anywhere); the session
    /// is started in the folder as resolved. Throws <see cref="ControlException"/> when it isn't.
    /// </summary>
    Task<string> StartAsync(string parent, string name, string directory, string prompt, string? within);

    /// <summary>A new worktree of <paramref name="parent"/>'s repository on the new branch <paramref name="branch"/>; its folder. Throws <see cref="ControlException"/>.</summary>
    Task<string> AddWorktreeAsync(string parent, string branch);

    /// <summary>Types <paramref name="text"/> into the session's terminal and sends it, as you would.</summary>
    void TypePrompt(string id, string text);

    /// <summary>Sends keys to the session's terminal (Enter, Esc).</summary>
    void SendKeys(string id, string keys);

    void Stop(string id);

    /// <summary>Stops the session if it's running, and removes it from LlamaTree (its conversation is kept, as when you remove one).</summary>
    void Remove(string id);

    SessionReport? Report(string id);
}

/// <summary>A session wants to start another, and you're asked (see <see cref="SessionControl.Asked"/>).</summary>
internal sealed record SpawnRequest(string Id, string From, string Name, string Directory, string? Branch, string Prompt);

/// <summary>One thing a session did to another (or tried to), for the Permissions log.</summary>
internal sealed record ControlLogEntry(DateTime At, string From, string? Target, string Action, string Detail, bool Ok);

/// <summary>Something an agent asked that it can't have; the message says why, for the agent.</summary>
internal sealed class ControlException(string message) : Exception(message);

/// <summary>
/// Lets a session's agent start sessions and drive them: type prompts into them, see what they're doing and what they
/// answered, wait for them, answer their permission prompts, interrupt and stop them. A session controls only the
/// sessions it started. Starting one goes by <see cref="ControlPolicy"/> (with no rule you're asked, and the call waits
/// for your answer), always runs the starter's own agent and options (never a command of the agent's choosing), in its
/// folder, a folder another session of its project uses, a folder inside either, or a new worktree; and is limited in
/// number and rate.
/// Everything is logged. While messages are paused (<see cref="RelayPolicy.Paused"/>) none of it works. Thread safe.
/// </summary>
internal sealed class SessionControl
{
    public const int MaxChildren = 5;
    public const int MaxStarted = 10;
    public const int MaxPrompt = 8000;
    public const int MaxName = 60;
    public const int PromptsPerMinute = 10;
    public const int SpawnsPerMinute = 3;
    public const int MaxLog = 300;
    public const int MaxReply = 6000;
    public static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(110);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(110);

    /// <summary>After a prompt, how long a session may take to start working before waiting counts it as done anyway.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(20);

    /// <summary>As <see cref="StartGrace"/>, for a new session, whose agent has to start (and may ask to trust its folder) first.</summary>
    public static readonly TimeSpan FirstStartGrace = TimeSpan.FromSeconds(60);

    // The page's words for a session's state (see statusClass in app.js).
    public const string Working = "working", Asking = "asking", Idle = "running", Done = "attention", Exited = "exited";

    private readonly ControlPolicy policy;
    private readonly IControlHost host;
    private readonly Func<bool> paused;
    private readonly Func<DateTime> now;
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> status = [];
    /// <summary>A session's last prompt (or start): when, whether it has been seen working since, and how long it may take to.</summary>
    private readonly Dictionary<string, (DateTime At, bool SawWorking, TimeSpan Grace)> prompted = [];

    /// <summary>Sessions being started for each session, not yet in the host's list.</summary>
    private readonly Dictionary<string, int> starting = [];
    private readonly Dictionary<string, TaskCompletionSource> statusChanged = [];
    private readonly Dictionary<string, (SpawnRequest Request, TaskCompletionSource<bool> Answer)> asks = [];
    private readonly Dictionary<string, Queue<DateTime>> rate = [];
    private readonly List<ControlLogEntry> log = [];
    private int logVersion;

    public SessionControl(ControlPolicy policy, IControlHost host, Func<bool> paused, Func<DateTime>? now = null)
    {
        this.policy = policy;
        this.host = host;
        this.paused = paused;
        this.now = now ?? (() => DateTime.UtcNow);
    }

    public ControlPolicy Policy => policy;

    /// <summary>A session asked to start one and no rule says: ask the person, and answer with <see cref="AnswerSpawn"/>.</summary>
    public event Action<SpawnRequest>? Asked;

    /// <summary>A question from <see cref="Asked"/> is settled (answered, timed out or abandoned): close it if it's showing.</summary>
    public event Action<string>? Settled;

    /// <summary>The log changed.</summary>
    public event Action? Changed;

    // ---------------------------------------------------------------- state from the page

    /// <summary>The session's state as the page shows it (working, asking, running, attention, exited).</summary>
    public void SetStatus(string id, string state)
    {
        TaskCompletionSource? tcs;
        lock (gate)
        {
            if (status.TryGetValue(id, out var was) && was == state) return;
            status[id] = state;
            if (state == Working && prompted.TryGetValue(id, out var p)) prompted[id] = p with { SawWorking = true };
            statusChanged.Remove(id, out tcs);
        }
        tcs?.TrySetResult();
    }

    public string StatusOf(string id)
    {
        lock (gate) return status.GetValueOrDefault(id, Idle);
    }

    public List<SpawnRequest> Pending()
    {
        lock (gate) return asks.Values.Select(a => a.Request).ToList();
    }

    public int LogVersion { get { lock (gate) return logVersion; } }

    /// <summary>Newest first.</summary>
    public List<ControlLogEntry> Log()
    {
        lock (gate) return Enumerable.Reverse(log).ToList();
    }

    public void ClearLog()
    {
        lock (gate) { log.Clear(); logVersion++; }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- starting a session

    /// <summary>
    /// Starts a session for <paramref name="from"/>: named <paramref name="name"/>, starting on <paramref name="prompt"/>,
    /// in <paramref name="directory"/> (default: the starter's folder) or, given <paramref name="branch"/>, in a new
    /// worktree on that new branch. Waits for your answer when no rule says. The new session's id and folder.
    /// </summary>
    public async Task<(string Id, string Directory)> StartAsync(string from, string name, string prompt, string? directory, string? branch, CancellationToken cancel = default)
    {
        var me = Me(from);
        name = (name ?? "").Trim();
        prompt = (prompt ?? "").Trim();
        branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        if (name.Length == 0) throw new ControlException("Give the new session a name.");
        if (name.Length > MaxName) throw new ControlException($"A session's name can be up to {MaxName} characters.");
        CheckPrompt(prompt);
        if (name.Any(char.IsControl)) throw new ControlException("A session's name can't hold control characters.");
        if (branch != null && !string.IsNullOrWhiteSpace(directory)) throw new ControlException("Give a folder or a new branch (for a new worktree), not both.");
        var (folder, within) = branch != null ? ($"a new worktree on the branch {branch}", null) : AllowedFolder(me, directory);
        CheckLimits(me);

        var decision = policy.Decide(ControlAbility.Spawn, me.Id, me.Project, me.StartedBy != null).Decision;
        if (decision == RelayDecision.Deny)
        {
            AddLog(from, null, "start", $"\"{name}\": not allowed", false);
            throw new ControlException("This session isn't allowed to start sessions. The person can change that in LlamaTree's Permissions.");
        }
        // Counted before asking, so a session can't line up question after question for the person.
        Take(from, SpawnsPerMinute, "start");
        if (decision == RelayDecision.Ask)
        {
            lock (gate)
            {
                if (asks.Values.Any(a => a.Request.From == from))
                    throw new ControlException("This session is already waiting for the person's answer about starting a session; wait for it before asking for another.");
            }
            if (!await AskAsync(new SpawnRequest(Guid.NewGuid().ToString("N")[..12], from, name, folder, branch, prompt), cancel))
                throw new ControlException("The person didn't allow it, so no session was started.");
        }

        me = Me(from);   // a while may have passed: still there, still within the limits
        // Held from here until the new session is in the list, so two at once can't both pass the limits.
        CheckLimits(me, hold: true);
        string? dir = null;
        try
        {
            dir = branch != null ? await host.AddWorktreeAsync(from, branch) : folder;
            within ??= dir;   // a worktree it made: held to that
            var id = await host.StartAsync(from, name, dir, prompt, within);
            lock (gate) prompted[id] = (now(), false, FirstStartGrace);
            AddLog(from, id, "start", $"\"{name}\" in {dir}", true);
            return (id, dir);
        }
        catch (ControlException e)
        {
            AddLog(from, null, "start", $"\"{name}\": {e.Message}", false);
            throw branch != null && dir != null
                ? new ControlException(e.Message + $" The new worktree {dir} was left as it is.")
                : e;
        }
        finally
        {
            lock (gate) if (--starting[from] == 0) starting.Remove(from);
        }
    }

    /// <summary>
    /// A prompt is typed into a terminal as text, so it may hold no control characters (Esc would break out of the
    /// paste and send keys of its own: Shift+Tab, Enter on a prompt) other than new lines and tabs; and it can't be a
    /// command to the agent itself (/ for its own commands, ! for running a shell command without asking).
    /// </summary>
    private static void CheckPrompt(string prompt)
    {
        if (prompt.Length == 0) throw new ControlException("There's no prompt: say what the session should do.");
        if (prompt.Length > MaxPrompt) throw new ControlException($"A prompt can be up to {MaxPrompt} characters; this one is {prompt.Length}.");
        if (prompt.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r')))
            throw new ControlException("A prompt can't hold control characters (escape sequences and the like); send plain text.");
        if (prompt[0] is '/' or '!')
            throw new ControlException("A prompt can't start with / or ! (the agent's own commands, and running shell commands): only the person types those.");
    }

    /// <summary>
    /// The folder a new session may use: the starter's own, one another session of its project uses, or a folder inside
    /// either (a relative path is taken from the starter's folder), and the folder it's in. Only sessions on the
    /// starter's own computer count: another computer's folders say nothing about this one's. Paths are compared as text
    /// here, in the way of the computer the sessions are on, and can't climb out with ".."; the computer the session
    /// starts on then checks the folder really is inside, with links resolved (see IControlHost.StartAsync).
    /// </summary>
    private (string Folder, string Within) AllowedFolder(ControlSession me, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return (me.Directory, me.Directory);
        directory = directory.Trim();
        static bool Rooted(string p) => p.StartsWith('/') || p.StartsWith('\\') || (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':');
        var sep = me.Directory.StartsWith('/') ? '/' : '\\';   // the starter's computer's way
        if (!Rooted(directory)) directory = me.Directory.TrimEnd('\\', '/') + sep + directory;
        if (directory.Split('\\', '/').Any(part => part == ".."))
            throw new ControlException("A folder can't be given with \"..\": give its full path, or one inside this session's folder.");
        // Separators made one kind, "." and empty parts dropped (keeping a leading / or \), no trailing one.
        static string Norm(string p)
        {
            var lead = p.StartsWith('/') || p.StartsWith('\\') ? "/" : "";
            return lead + string.Join('/', p.Split('\\', '/').Where(x => x.Length > 0 && x != "."));
        }
        var want = Norm(directory);
        var allowed = host.Sessions().Where(s => s.Id == me.Id || (me.Project != null && s.Project == me.Project && s.Server == me.Server)).Select(s => s.Directory);
        foreach (var d in allowed)
        {
            var root = Norm(d);
            if (string.Equals(root, want, StringComparison.OrdinalIgnoreCase)) return (d.TrimEnd('\\', '/'), d);
            if (root.Length > 0 && want.Length > root.Length + 1 && want.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return (d.TrimEnd('\\', '/') + sep + want[(root.Length + 1)..].Replace('/', sep), d);
        }
        throw new ControlException("A new session can use this session's folder, a folder another session of its project uses, a folder inside " +
            $"either, or a new worktree (give branch instead); {directory} is none of these.");
    }

    /// <summary>
    /// Refuses a session past the limits. Counted with the ones being started (<see cref="starting"/>) and the ones just
    /// started whose process the page hasn't started yet. Reads the host's sessions first, then checks under the lock.
    /// </summary>
    private void CheckLimits(ControlSession me, bool hold = false)
    {
        var all = host.Sessions();
        lock (gate)
        {
            bool Live(ControlSession s) => s.Running || (prompted.TryGetValue(s.Id, out var p) && now() - p.At < p.Grace);
            if (all.Count(s => s.StartedBy == me.Id && Live(s)) + starting.GetValueOrDefault(me.Id) >= MaxChildren)
                throw new ControlException($"This session already has {MaxChildren} sessions it started running. Stop one (stop_session) first.");
            if (all.Count(s => s.StartedBy != null && Live(s)) + starting.Values.Sum() >= MaxStarted)
                throw new ControlException($"{MaxStarted} sessions started by agents are running already, the most LlamaTree allows. Wait for some to finish and be stopped.");
            if (hold) starting[me.Id] = starting.GetValueOrDefault(me.Id) + 1;
        }
    }

    /// <summary>Asks you through <see cref="Asked"/>, waiting up to <see cref="AskTimeout"/>; true if you allowed it.</summary>
    private async Task<bool> AskAsync(SpawnRequest request, CancellationToken cancel)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) asks[request.Id] = (request, answer);
        Asked?.Invoke(request);
        try
        {
            var done = await Task.WhenAny(answer.Task, Task.Delay(AskTimeout, cancel));
            if (done == answer.Task) return answer.Task.Result;
            cancel.ThrowIfCancellationRequested();
            AddLog(request.From, null, "start", $"\"{request.Name}\": no answer in time", false);
            throw new ControlException($"The person didn't answer within {AskTimeout.TotalSeconds:0} seconds, so no session was started. Tell them what you wanted to start, and try again when they're ready.");
        }
        finally
        {
            bool removed;
            lock (gate) removed = asks.Remove(request.Id);
            if (removed) Settled?.Invoke(request.Id);
        }
    }

    /// <summary>
    /// Your answer to a <see cref="SpawnRequest"/>. <paramref name="scope"/>: "once", or "session"/"project" to also make
    /// it the rule for the session or its project.
    /// </summary>
    public void AnswerSpawn(string requestId, bool allow, string scope)
    {
        (SpawnRequest Request, TaskCompletionSource<bool> Answer) ask;
        lock (gate)
        {
            if (!asks.Remove(requestId, out ask)) return;
        }
        var me = host.Sessions().FirstOrDefault(s => s.Id == ask.Request.From);
        var decision = allow ? RelayDecision.Allow : RelayDecision.Deny;
        if (scope == "session") policy.Set(ControlAbility.Spawn, ask.Request.From, decision);
        else if (scope == "project" && me?.Project != null) policy.Set(ControlAbility.Spawn, ControlPolicy.ProjectPrefix + me.Project, decision);
        if (!allow) AddLog(ask.Request.From, null, "start", $"\"{ask.Request.Name}\": you said no", false);
        Settled?.Invoke(requestId);
        ask.Answer.TrySetResult(allow);
    }

    // ---------------------------------------------------------------- driving the sessions it started

    /// <summary>The sessions <paramref name="from"/> started, with their states.</summary>
    public List<(ControlSession Session, string Status)> Children(string from) =>
        host.Sessions().Where(s => s.StartedBy == from).Select(s => (s, s.Running ? StatusOf(s.Id) : Exited)).ToList();

    /// <summary>Types a prompt into a session <paramref name="from"/> started, which must be idle.</summary>
    public void Prompt(string from, string target, string text)
    {
        var child = Child(from, target);
        text = (text ?? "").Trim();
        CheckPrompt(text);
        switch (child.Running ? StatusOf(child.Id) : Exited)
        {
            case Exited: throw new ControlException($"\"{child.Name}\" isn't running.");
            case Working: throw new ControlException($"\"{child.Name}\" is still working. Wait for it (wait_for_session) before sending another prompt.");
            case Asking: throw new ControlException($"\"{child.Name}\" is waiting for an answer to a prompt. See what it asks (get_session), then answer_prompt.");
        }
        lock (gate)
        {
            // Idle just after a prompt only means it hasn't started on it yet.
            if (prompted.TryGetValue(child.Id, out var p) && !p.SawWorking && now() - p.At < p.Grace)
                throw new ControlException($"\"{child.Name}\" hasn't started on its last prompt yet. Wait for it (wait_for_session) before sending another.");
        }
        Take(from, PromptsPerMinute, "prompt");
        lock (gate) prompted[child.Id] = (now(), false, StartGrace);
        host.TypePrompt(child.Id, text);
        AddLog(from, child.Id, "prompt", Clip(text, 200), true);
    }

    /// <summary>How a session <paramref name="from"/> started is doing, and what it last answered.</summary>
    public string Describe(string from, string target)
    {
        var child = Child(from, target);
        return Summary(child, child.Running ? StatusOf(child.Id) : Exited);
    }

    /// <summary>
    /// Waits until a session <paramref name="from"/> started stops working (done, asking something, or stopped), at most
    /// <paramref name="timeout"/>; then describes it, or says it's still working.
    /// </summary>
    public async Task<string> WaitAsync(string from, string target, TimeSpan timeout, CancellationToken cancel = default)
    {
        var child = Child(from, target);
        var until = DateTime.UtcNow + (timeout > MaxWait ? MaxWait : timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        while (true)
        {
            Task changed;
            string state;
            bool settled;
            var current = Me(child.Id);   // not under the lock: the host reads its sessions on the UI thread, which takes the lock too
            lock (gate)
            {
                state = current.Running ? status.GetValueOrDefault(child.Id, Idle) : Exited;
                var p = prompted.GetValueOrDefault(child.Id, (At: DateTime.MinValue, SawWorking: true, Grace: StartGrace));
                // Just after a prompt it may not have started working yet: idle then doesn't mean done.
                settled = state is Exited or Asking || (state != Working && (p.SawWorking || now() - p.At > p.Grace));
                changed = (statusChanged.TryGetValue(child.Id, out var t) ? t : statusChanged[child.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            if (settled) return Summary(current, state);
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return $"\"{child.Name}\" is still working. Wait again with wait_for_session, or check on it with get_session.";
            // Woken by a change of state, or every few seconds for the start grace and a stopped process.
            await Task.WhenAny(changed, Task.Delay(left < TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5), cancel));
            cancel.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Answers the permission prompt of a session <paramref name="from"/> started: its first choice (yes) or Esc (no).</summary>
    public void Answer(string from, string target, bool approve)
    {
        var child = Child(from, target);
        var me = Me(from);
        if (policy.Decide(ControlAbility.Answer, me.Id, me.Project).Decision == RelayDecision.Deny)
        {
            AddLog(from, child.Id, approve ? "approve" : "decline", "not allowed", false);
            throw new ControlException($"This session isn't allowed to answer its sessions' prompts; the person answers them in \"{child.Name}\". They can change that in LlamaTree's Permissions.");
        }
        if ((child.Running ? StatusOf(child.Id) : Exited) != Asking) throw new ControlException($"\"{child.Name}\" isn't waiting for an answer.");
        // Only a tool's permission prompt: Enter on anything else (trusting a folder, a question with choices) would
        // answer what this isn't meant to.
        var about = host.Report(child.Id)?.PendingTool;
        if (about == null || about.StartsWith("AskUserQuestion", StringComparison.Ordinal))
            throw new ControlException($"\"{child.Name}\" is asking something other than permission to use a tool, so it's left for the person to answer there.");
        // Declined, it stops and waits for a prompt; approved, it carries on (see WaitAsync).
        lock (gate) prompted[child.Id] = (now(), !approve, StartGrace);
        host.SendKeys(child.Id, approve ? "\r" : "\x1b");
        AddLog(from, child.Id, approve ? "approve" : "decline", about, true);
    }

    /// <summary>A session is gone: forget its state.</summary>
    public void Forget(string id)
    {
        lock (gate)
        {
            status.Remove(id);
            prompted.Remove(id);
            statusChanged.Remove(id, out var tcs);
            tcs?.TrySetResult();
        }
    }

    /// <summary>Esc: stops what a session <paramref name="from"/> started is doing, leaving it ready for a prompt.</summary>
    public void Interrupt(string from, string target)
    {
        var child = Child(from, target);
        if (!child.Running) throw new ControlException($"\"{child.Name}\" isn't running.");
        lock (gate) prompted[child.Id] = (now(), true, StartGrace);   // it may never show as working again: idle is done
        host.SendKeys(child.Id, "\x1b");
        AddLog(from, child.Id, "interrupt", "", true);
    }

    /// <summary>Ends the agent of a session <paramref name="from"/> started; the session stays, for the person to look at or remove.</summary>
    public void Stop(string from, string target)
    {
        var child = Child(from, target);
        if (!child.Running) throw new ControlException($"\"{child.Name}\" isn't running.");
        host.Stop(child.Id);
        AddLog(from, child.Id, "stop", "", true);
    }

    /// <summary>
    /// Closes a session <paramref name="from"/> started, its work done: stopped if it's running, and removed from LlamaTree.
    /// Its conversation is kept (it can be resumed), and a worktree made for it stays: deleting one is the person's call.
    /// </summary>
    public void Close(string from, string target)
    {
        var child = Child(from, target);
        var me = Me(from);
        if (policy.Decide(ControlAbility.Close, me.Id, me.Project).Decision == RelayDecision.Deny)
        {
            AddLog(from, child.Id, "close", "not allowed", false);
            throw new ControlException($"This session isn't allowed to close the sessions it started; stop_session ends \"{child.Name}\", and the person removes it. They can change that in LlamaTree's Permissions.");
        }
        host.Remove(child.Id);
        Forget(child.Id);
        AddLog(from, child.Id, "close", child.Name, true);
    }

    // ---------------------------------------------------------------- helpers

    private ControlSession Me(string id)
    {
        if (paused()) throw new ControlException("Agents are paused in LlamaTree: sessions can't start or drive other sessions until the person resumes them.");
        return host.Sessions().FirstOrDefault(s => s.Id == id) ?? throw new ControlException("That session isn't in LlamaTree any more.");
    }

    /// <summary>The session <paramref name="key"/> names (its id, or its name), which <paramref name="from"/> must have started.</summary>
    private ControlSession Child(string from, string key)
    {
        Me(from);
        key = (key ?? "").Trim();
        var mine = host.Sessions().Where(s => s.StartedBy == from).ToList();
        var byId = mine.FirstOrDefault(s => s.Id == key);
        if (byId != null) return byId;
        var named = mine.Where(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count switch
        {
            1 => named[0],
            0 => throw new ControlException($"This session didn't start a session \"{key}\"; it can only drive sessions it started (list_sessions shows them)."),
            _ => throw new ControlException($"{named.Count} of this session's sessions are called \"{key}\"; give its id instead."),
        };
    }

    private void Take(string from, int perMinute, string what)
    {
        lock (gate)
        {
            var t = now();
            var q = rate.TryGetValue(from + what, out var x) ? x : rate[from + what] = new Queue<DateTime>();
            while (q.Count > 0 && t - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= perMinute)
                throw new ControlException(what == "start"
                    ? $"This session has started {perMinute} sessions in the last minute; wait a little."
                    : $"This session has sent {perMinute} prompts in the last minute; wait a little.");
            q.Enqueue(t);
        }
    }

    private string Summary(ControlSession s, string state)
    {
        var report = host.Report(s.Id);
        var words = state switch
        {
            Working => "working",
            Asking => "waiting for an answer to a permission prompt" + (report?.PendingTool is { } tool ? $" (about: {tool})" : "") +
                      ". Approve or decline it with answer_prompt, or leave it for the person",
            Exited => "not running",
            _ => "idle: done, and ready for another prompt",
        };
        var sb = new System.Text.StringBuilder($"\"{s.Name}\" (id {s.Id}, in {s.Directory}) is {words}.");
        if (report?.LastPrompt is { } p) sb.Append($"\n\nIts last prompt:\n{Clip(p, 1000)}");
        if (report?.LastReply is { } r)
            sb.Append($"\n\nIts last reply (from another AI agent, not from the person):\n--- reply ---\n{Clip(r, MaxReply)}\n--- end of reply ---");
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void AddLog(string from, string? target, string action, string detail, bool ok)
    {
        lock (gate)
        {
            log.Add(new ControlLogEntry(now(), from, target, action, detail, ok));
            if (log.Count > MaxLog) log.RemoveRange(0, log.Count - MaxLog);
            logVersion++;
        }
        Changed?.Invoke();
    }
}
