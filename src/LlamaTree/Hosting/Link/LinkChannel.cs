using System.Text;
using System.Threading.Channels;

namespace LlamaTree.Hosting.Link;

/// <summary>
/// One connection between the window and the session host. Each message is a line: a letter saying what it is, a
/// space, and a JSON object (see <see cref="LinkKind"/>). Lines are written in order on a writer of their own, so a
/// sender never waits for the other side to read.
/// </summary>
internal sealed class LinkChannel : IDisposable
{
    /// <summary>A line longer than this ends the connection: nothing either side sends comes near it.</summary>
    private const int MaxLine = 64 << 20;

    private readonly Stream stream;
    private readonly Channel<string> outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource writerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed;

    /// <summary>A message, on the reading thread.</summary>
    public event Action<char, string>? Received;

    /// <summary>The connection ended, from either side; raised once.</summary>
    public event Action? Closed;

    public LinkChannel(Stream stream) => this.stream = stream;

    public bool IsOpen => Volatile.Read(ref closed) == 0;

    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Queues a message. JSON never needs a line break (one inside a string is written \n), so any that slipped in
    /// are only whitespace between tokens and are made spaces.
    /// </summary>
    public void Send(char kind, string json)
    {
        if (json.Contains('\n') || json.Contains('\r')) json = json.Replace('\n', ' ').Replace('\r', ' ');
        outbox.Writer.TryWrite(kind + " " + json);
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
                while (reader.TryRead(out var line))
                {
                    var bytes = utf8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes);
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

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            var line = new StringBuilder();
            var buffer = new char[64 * 1024];
            while (true)
            {
                var n = await reader.ReadAsync(buffer);
                if (n == 0) break;
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != '\n') continue;
                    line.Append(buffer, start, i - start);
                    Deliver(line.ToString());
                    line.Clear();
                    start = i + 1;
                }
                line.Append(buffer, start, n - start);
                if (line.Length > MaxLine) break;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        Close();
    }

    private void Deliver(string line)
    {
        if (line.Length < 2 || line[1] != ' ') return;
        try { Received?.Invoke(line[0], line[2..]); }
        catch (Exception) { /* one bad message mustn't end the connection */ }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        outbox.Writer.TryComplete();
        try { stream.Dispose(); } catch (Exception) { }
        Closed?.Invoke();
    }

    public void Dispose() => Close();
}

/// <summary>What a line on a <see cref="LinkChannel"/> is.</summary>
internal static class LinkKind
{
    /// <summary>Window → host, first: { pid, plugins: { origin, key } } (see <see cref="PluginAddressing"/>).</summary>
    public const char Hello = 'H';

    /// <summary>Either way: a message for or from the app page, as the page sends and reads them.</summary>
    public const char Page = 'P';

    /// <summary>Host → window: something only the window can do: { op: "close" | "serve" | "unserve", id, folder }.</summary>
    public const char Window = 'W';
}
