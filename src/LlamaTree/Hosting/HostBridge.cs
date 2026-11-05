using LlamaTree.Hosting.Link;

namespace LlamaTree.Hosting;

/// <summary>
/// LlamaTree started with --attach, on a server: joins its standard input and output to this user's session host
/// there, starting the host if none is running. A window on another computer runs it over SSH
/// (<c>ssh server llamatree --attach</c>) and talks to the host through it as it would through the local pipe.
/// <para>
/// The bridge is only the connection: when SSH drops it ends, and the host, like a host whose window was closed, keeps
/// the sessions running for the next window.
/// </para>
/// </summary>
internal static class HostBridge
{
    public static int Run()
    {
        try
        {
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LlamaTree: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        var host = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(300));
        if (host == null && HostEndpoint.Problem is { } problem)
        {
            Console.Error.WriteLine("LlamaTree: " + problem);
            return 1;
        }
        // Started again every few seconds while none answers: one still ending (quit to restart on a new version, say)
        // holds its lock a moment, and the one started meanwhile sees it and leaves.
        for (var i = 0; i < 120 && host == null; i++)
        {
            if (i % 12 == 0 && !SessionHost.StartDetached()) break;
            host = await HostEndpoint.ConnectAsync(TimeSpan.FromMilliseconds(250));
        }
        if (host == null)
        {
            Console.Error.WriteLine("LlamaTree: the session host didn't start on " + Environment.MachineName + ".");
            return 1;
        }
        await using (host)
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            // Either side ending (SSH dropped, or the host ended) ends the bridge.
            await Task.WhenAny(Pump(input, host), Pump(host, output));
        }
        return 0;
    }

    private static async Task Pump(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                await to.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
