using System.Text;
using System.Threading.Channels;

namespace LlamaTree.Hosting.Link;

/// <summary>
/// One connection between the window and the session host. Each message is a line: a letter saying what it is, a
/// space, and a JSON object (see <see cref="LinkKind"/>). Lines are written in order on a writer of their own, so a
/// sender never waits for the other side to read.
/// <para>
/// The other side may be another computer's LlamaTree, so neither direction holds more than it has to: a line past
/// <see cref="MaxLine"/> ends the connection, as do more than <see cref="MaxOutbox"/> characters waiting to go, and
/// reading pauses while more than <see cref="MaxHeld"/> characters received are still being handled (see <see cref="Hold"/>).
/// </para>
/// </summary>
internal sealed class LinkChannel : IDisposable
{
    /// <summary>
    /// The longest line either side may send, in characters. The biggest messages are a Git diff (its two sides, 4 MB
    /// each, see GitService), a session's kept output replayed (512K characters, more once escaped as JSON) and a whole
    /// conversation; files go in parts of 256K characters (see <see cref="ServerFiles.PartSize"/>). Twice the diff, with
    /// its escapes: a line longer than this ends the connection rather than being held.
    /// </summary>
    internal const int MaxLine = 16 << 20;

    /// <summary>
    /// The most characters waiting to be sent: past this the other side isn't reading, and the connection is ended
    /// rather than holding more. Files go through <see cref="SendParts"/>, read only as they're written, so never count.
    /// </summary>
    internal const int MaxOutbox = 2 * MaxLine;

    /// <summary>
    /// The most characters received and still being handled (see <see cref="Hold"/>): past this, reading pauses until
    /// they have been, so the other side waits rather than this one filling up.
    /// </summary>
    internal const int MaxHeld = 8 << 20;

    private readonly Stream stream;
    private readonly Channel<object> outbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });   // lines, or parts (IEnumerable<string>) made as they go
    private readonly TaskCompletionSource writerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object holdLock = new();
    private long queued;   // characters of lines waiting to be written
    private long held;     // characters received and not yet handled (see Hold)
    private TaskCompletionSource? room;   // the reader, waiting for held to drop
    private int closed;

    /// <summary>A message, on the reading thread.</summary>
    public event Action<char, string>? Received;

    /// <summary>The connection ended, from either side; raised once.</summary>
    public event Action? Closed;

    public LinkChannel(Stream stream) => this.stream = stream;

    public bool IsOpen => Volatile.Read(ref closed) == 0;

    /// <summary>Why this side ended the connection, when it was for something the other side did (a line too long, not reading); else null.</summary>
    public string? CloseReason { get; private set; }

    /// <summary>Who's at the other end, for the log: a server (user@server), "window" or "session host".</summary>
    public string Name { get; init; } = "connection";

    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Queues a message. JSON never needs a line break (one inside a string is written \n), so any that slipped in
    /// are only whitespace between tokens and are made spaces. Past <see cref="MaxOutbox"/> waiting, the connection ends.
    /// </summary>
    public void Send(char kind, string json)
    {
        var line = Line(kind, json);
        if (Interlocked.Add(ref queued, line.Length) > MaxOutbox)
        {
            Close($"The other side stopped reading: more than {MaxOutbox >> 20}M characters were waiting to be sent to it.");
            return;
        }
        outbox.Writer.TryWrite(line);
    }

    /// <summary>
    /// Queues messages made only as they're written (a file's parts, read a part at a time), in order with the rest.
    /// One that can't be made (the file has gone) ends them; the connection carries on.
    /// </summary>
    public void SendParts(char kind, IEnumerable<string> jsons) => outbox.Writer.TryWrite(jsons.Select(json => Line(kind, json)));

    private static string Line(char kind, string json)
    {
        if (json.Contains('\n') || json.Contains('\r')) json = json.Replace('\n', ' ').Replace('\r', ' ');
        return kind + " " + json;
    }

    /// <summary>
    /// For a <see cref="Received"/> handler that handles the message later (on the app thread): counts its
    /// <paramref name="size"/> as held until the action returned is called, and reading pauses while more than
    /// <see cref="MaxHeld"/> is.
    /// </summary>
    public Action Hold(int size)
    {
        lock (holdLock) held += size;
        var released = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            TaskCompletionSource? wake = null;
            lock (holdLock)
            {
                held -= size;
                if (held <= MaxHeld) (wake, room) = (room, null);
            }
            wake?.TrySetResult();
        };
    }

    /// <summary>Done once what's held is no more than <see cref="MaxHeld"/> (or the connection has ended).</summary>
    private Task RoomAsync()
    {
        lock (holdLock)
        {
            if (held <= MaxHeld || !IsOpen) return Task.CompletedTask;
            return (room ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    /// <summary>Waits (at most <paramref name="timeout"/>) until everything queued so far has been written, then closes.</summary>
    public async Task FlushAndCloseAsync(TimeSpan timeout)
    {
        outbox.Writer.TryComplete();
        await Task.WhenAny(writerDone.Task, Task.Delay(timeout));
        Close();
    }

    private async Task WriteLoopAsync()
    {
        var utf8 = new UTF8Encoding(false);
        try
        {
            var reader = outbox.Reader;
            while (await reader.WaitToReadAsync())
            {
                // Everything waiting goes out together, then one flush.
                while (reader.TryRead(out var item))
                {
                    if (item is string line)
                    {
                        await stream.WriteAsync(utf8.GetBytes(line + "\n"));
                        Interlocked.Add(ref queued, -line.Length);
                    }
                    else await WritePartsAsync((IEnumerable<string>)item, utf8);
                }
                await stream.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            writerDone.TrySetResult();
        }
        if (outbox.Reader.Completion.IsCompleted) return;   // closing on purpose: FlushAndCloseAsync closes
        Close();
    }

    /// <summary>Writes messages as they're made (see <see cref="SendParts"/>): one that can't be (its file has gone) ends them.</summary>
    private async Task WritePartsAsync(IEnumerable<string> lines, Encoding utf8)
    {
        using var parts = lines.GetEnumerator();
        while (true)
        {
            string line;
            try
            {
                if (!parts.MoveNext()) return;
                line = parts.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            await stream.WriteAsync(utf8.GetBytes(line + "\n"));
        }
    }

    private async Task ReadLoopAsync()
    {
        string? reason = null;
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            var line = new StringBuilder();
            var buffer = new char[64 * 1024];
            while (reason == null && IsOpen)
            {
                await RoomAsync();
                var n = await reader.ReadAsync(buffer);
                if (n == 0) break;
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != '\n') continue;
                    if (line.Length + i - start > MaxLine) break;
                    line.Append(buffer, start, i - start);
                    Deliver(line.ToString());
                    line.Clear();
                    start = i + 1;
                }
                // A line that goes on past the most there may be: the connection ends, rather than holding it while more comes.
                if (line.Length + n - start > MaxLine) reason = $"The other side sent a message longer than {MaxLine >> 20}M characters.";
                else line.Append(buffer, start, n - start);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        Close(reason);
    }

    private void Deliver(string line)
    {
        if (line.Length < 2 || line[1] != ' ') return;
        var json = line[2..];
        try { Received?.Invoke(line[0], json); }
        catch (Exception ex)
        {
            // One bad message mustn't end the connection. What it said isn't logged: only what kind it was.
            AppLog.Warn($"{Name}: a '{line[0]}' {AppLog.MessageType(json)} message ({json.Length} characters) failed and was dropped: {AppLog.Describe(ex)}");
        }
    }

    public void Close() => Close(null);

    /// <summary>Ends the connection; <paramref name="reason"/> says why, when it was for something the other side did.</summary>
    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        CloseReason = reason;
        if (reason != null) AppLog.Warn($"{Name}: the connection was ended: {reason}");
        outbox.Writer.TryComplete();
        TaskCompletionSource? wake;
        lock (holdLock) (wake, room) = (room, null);
        wake?.TrySetResult();
        try { stream.Dispose(); } catch (Exception) { }
        Closed?.Invoke();
    }

    public void Dispose() => Close();
}

/// <summary>What a line on a <see cref="LinkChannel"/> is.</summary>
internal static class LinkKind
{
    /// <summary>Window → host, first: { pid, plugins: { origin, key, namespaced }, remote } (see <see cref="PluginAddressing"/>).</summary>
    public const char Hello = 'H';

    /// <summary>Either way: a message for or from the app page, as the page sends and reads them.</summary>
    public const char Page = 'P';

    /// <summary>Host → window: something only the window can do: { op: "close" | "serve" | "unserve", id, folder }.</summary>
    public const char Window = 'W';
}
