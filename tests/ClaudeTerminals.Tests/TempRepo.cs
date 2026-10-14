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

    /// <summary>
    /// HEAD, the index, git's view of the working tree, and the exact name and bytes of every working file, for
    /// "nothing changed" assertions.
    /// </summary>
    public string Snapshot()
    {
        string head;
        try { head = Git("rev-parse", "-q", "--verify", "HEAD"); }
        catch (InvalidOperationException) { head = "(none)"; }
        var files = new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(Root, f.FullName).Split(Path.DirectorySeparatorChar)[0] != ".git")   // the metadata folder only, not .gitignore etc.
            .Select(f => Path.GetRelativePath(Root, f.FullName) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f.FullName))))
            .Order(StringComparer.Ordinal);
        return head + Git("status", "--porcelain=v2", "--untracked-files=all") + Git("ls-files", "-s") + string.Join("\n", files);
    }

    public void Dispose()
    {
        try
        {
            FileSystemFixtures.DeleteTree(Root);   // also clears the read-only flag on git objects
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
