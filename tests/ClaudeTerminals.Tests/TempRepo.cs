using System.Diagnostics;

namespace ClaudeTerminals.Tests;

/// <summary>A throwaway git repository under %TEMP%, deleted on dispose.</summary>
internal sealed class TempRepo : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ct-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempRepo()
    {
        Directory.CreateDirectory(Root);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "core.autocrlf", "false");
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
        return stdout.Result;
    }

    public void Write(string path, string content)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    public bool Exists(string path) => File.Exists(Path.Combine(Root, path));

    public void Commit(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files) Write(path, content);
        Git("add", "-A");
        Git("commit", "-q", "-m", "commit");
    }

    /// <summary>The index and working tree as git sees them, for "nothing changed" assertions.</summary>
    public string Snapshot() => Git("status", "--porcelain=v2", "--untracked-files=all") + Git("ls-files", "-s");

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);   // git objects are read-only
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
