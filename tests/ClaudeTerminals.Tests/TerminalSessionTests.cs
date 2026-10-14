using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>Stop versus natural exit (review finding F6), with a real pseudo console.</summary>
public sealed class TerminalSessionTests
{
    private static TerminalSession Session(string command) =>
        new(new SessionInfo { Name = "test", Directory = Path.GetTempPath(), Command = command });

    [Fact]
    public async Task Stop_kills_the_process_once_and_reports_it_without_an_exit_event()
    {
        using var s = Session("cmd /c ping -n 30 127.0.0.1 >nul");
        var exits = 0;
        s.Exited += (_, _, _) => Interlocked.Increment(ref exits);
        s.Start(80, 25);
        Assert.True(s.IsRunning);

        Assert.True(s.Stop());
        Assert.False(s.IsRunning);
        Assert.False(s.Stop());   // nothing left to stop: the host must not report a second stop

        await Task.Delay(1000);   // a late exit callback from the killed process must not surface
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task Natural_exit_is_reported_once_with_its_code_and_generation()
    {
        using var s = Session("cmd /c exit 3");
        var exited = new TaskCompletionSource<(int Code, int Generation)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exits = 0;
        s.Exited += (_, code, generation) => { Interlocked.Increment(ref exits); exited.TrySetResult((code, generation)); };
        s.Start(80, 25);

        var (code, generation) = await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(3, code);
        Assert.Equal(s.Generation, generation);   // nothing restarted since: the host reports it
        await Task.Delay(500);
        Assert.Equal(1, exits);
        Assert.False(s.IsRunning);
        Assert.False(s.Stop());
    }

    [Fact]
    public async Task Restart_after_stop_runs_a_new_process_with_a_new_generation()
    {
        using var s = Session("cmd /c ping -n 30 127.0.0.1 >nul");
        var exits = 0;
        s.Exited += (_, _, _) => Interlocked.Increment(ref exits);
        s.Start(80, 25);
        var first = s.Generation;
        s.Stop();

        s.Start(80, 25);
        Assert.NotEqual(first, s.Generation);   // an exit stamped with the first generation is recognisably stale
        Assert.True(s.IsRunning);
        await Task.Delay(1000);
        Assert.True(s.IsRunning);   // the first process's death did not mark the second as exited
        Assert.Equal(0, exits);
        Assert.True(s.Stop());
    }
}
