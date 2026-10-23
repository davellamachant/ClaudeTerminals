using System.Diagnostics;
using System.Text;
using ClaudeTerminals.Terminal;

namespace ClaudeTerminals.Git;

/// <summary>
/// Suggests a commit message for a repository's changes, like Visual Studio's "Generate commit message": Claude Code
/// runs headless (claude -p) with no tools, MCP servers or saved session, and the user's own Claude sign-in.
/// It describes what Commit would take: the staged changes, or everything when nothing is staged.
/// </summary>
internal static partial class CommitMessageService
{
    private const int MaxDiffChars = 60_000;
    private const int MaxNewFileChars = 2_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Headless, tool-less and not saved (so it doesn't appear among the conversations to resume).</summary>
    private const string Command = "claude -p --model haiku --tools \"\" --strict-mcp-config --no-session-persistence --output-format text";

    /// <param name="detail">"short" (one line), "standard" (a line, and brief points only when needed) or "detailed".</param>
    public static async Task<string> SuggestAsync(string dir, string? detail = null, CancellationToken ct = default)
    {
        detail = NormalizeDetail(detail);
        var prompt = await BuildPromptAsync(dir, detail) ?? throw new InvalidOperationException("There are no changes to describe.");
        var message = Finish(await RunClaudeAsync(dir, prompt, ct), detail);
        return message.Length > 0 ? message : throw new InvalidOperationException("Claude didn't suggest a message. Try again.");
    }

    internal static string NormalizeDetail(string? detail) => detail is "short" or "detailed" ? detail : "standard";

    /// <summary>The answer tidied; a short message is its summary line, whatever else came back.</summary>
    internal static string Finish(string answer, string detail)
    {
        var message = Clean(answer);
        return detail == "short" ? message.Split('\n')[0].Trim() : message;
    }

    /// <summary>The request for Claude, or null when there is nothing to commit.</summary>
    internal static async Task<string?> BuildPromptAsync(string dir, string detail = "standard")
    {
        var status = await GitService.GetStatusAsync(dir);
        if (!status.IsRepo) throw new InvalidOperationException(status.Error ?? "Not a Git repository.");
        var staged = status.Staged.Count > 0;
        var files = staged ? status.Staged : status.Unstaged;
        if (files.Count == 0) return null;

        // With nothing staged the index matches HEAD, so the working tree diff is everything Commit All would take
        // (and works before the first commit, unlike diff HEAD). New files aren't in it; a few lines of each are added.
        var diffArgs = staged
            ? new[] { "diff", "--cached", "--no-ext-diff", "--no-color", "-U3" }
            : ["diff", "--no-ext-diff", "--no-color", "-U3"];
        var diff = (await GitService.RunAsync(dir, diffArgs, maxOutputBytes: MaxDiffChars * 4)).Text;
        var sb = new StringBuilder(Math.Min(diff.Length, MaxDiffChars));
        Append(sb, diff);
        if (!staged)
        {
            foreach (var f in files.Where(f => f.Untracked))
            {
                if (sb.Length >= MaxDiffChars) break;
                sb.Append("\n--- new file: ").Append(f.Path).Append('\n');
                Append(sb, ReadStart(Path.Combine(dir, f.Path)));
            }
        }
        var truncated = sb.Length >= MaxDiffChars;

        var log = await GitService.RunAsync(dir, ["log", "-10", "--format=%s"]);
        var recent = log.Ok ? log.Text.Trim() : "";

        var p = new StringBuilder();
        p.AppendLine("Write a git commit message for the changes below.");
        p.AppendLine();
        p.AppendLine("Rules:");
        p.AppendLine("- Reply with the commit message only: no preamble, no quotes, no code fences.");
        p.AppendLine("- First line: a summary of at most 72 characters, in the imperative mood.");
        p.AppendLine(NormalizeDetail(detail) switch
        {
            "short" => "- Reply with that one line only: no body.",
            "detailed" => "- Then a blank line and a body wrapped at 72 characters: what changed and why, as far as the diff shows. Don't invent reasons.",
            _ => "- Usually that line is enough. Only if the change covers several separate things, add a blank line and at most three short \"- \" points, one line each. No paragraphs.",
        });
        p.AppendLine("- Follow the style of the recent commits (for example a \"feat:\" or \"fix:\" prefix) if they have one.");
        if (recent.Length > 0)
        {
            p.AppendLine();
            p.AppendLine("Recent commit subjects:");
            p.AppendLine(recent);
        }
        p.AppendLine();
        p.AppendLine($"Files ({files.Count}):");
        foreach (var f in files.Take(200)) p.AppendLine($"{(f.Untracked ? "A" : f.Status)} {f.Path}");
        if (files.Count > 200) p.AppendLine($"… and {files.Count - 200} more");
        p.AppendLine();
        p.AppendLine(truncated ? "Diff (cut short; the file list above is complete):" : "Diff:");
        p.Append(sb);
        return p.ToString();
    }

    private static void Append(StringBuilder sb, string text)
    {
        var room = MaxDiffChars - sb.Length;
        if (room <= 0) return;
        sb.Append(text.Length <= room ? text : text[..room]);
    }

    private static string ReadStart(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxNewFileChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);
            if (text.Contains('\0')) return "(binary file)\n";
            return read == buffer.Length ? text + "\n…\n" : text + "\n";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";   // a folder (submodule), or gone already
        }
    }

    private static async Task<string> RunClaudeAsync(string dir, string prompt, CancellationToken ct)
    {
        var env = SessionEnvironment.Build();
        string line;
        try
        {
            line = CommandLineResolver.Resolve(Command, env);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException("Claude Code (claude) wasn't found on PATH, so a message can't be suggested.");
        }
        var (program, arguments) = SplitProgram(line);
        var psi = new ProcessStartInfo(program, arguments)
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // The same clean environment sessions get, not this app's (which may have been started from a Claude session).
        psi.Environment.Clear();
        foreach (var (name, value) in env) psi.Environment[name] = value;

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(Timeout);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude Code.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timer.Token);
            var stderr = process.StandardError.ReadToEndAsync(timer.Token);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), timer.Token);
            process.StandardInput.Close();
            var output = await stdout;
            var error = await stderr;
            await process.WaitForExitAsync(timer.Token);
            if (process.ExitCode != 0)
            {
                var why = (error.Trim().Length > 0 ? error : output).Trim();
                throw new InvalidOperationException("Claude couldn't suggest a message: " + (why.Length > 400 ? why[..400] + "…" : why));
            }
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Claude didn't answer within {Timeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>CommandLineResolver gives a whole command line; the program is its first (possibly quoted) token.</summary>
    internal static (string Program, string Arguments) SplitProgram(string line)
    {
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return (line[1..end], line[(end + 1)..].TrimStart());
        }
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }

    /// <summary>Drops code fences or quotes the model sometimes wraps the message in.</summary>
    internal static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Trim().Split('\n').ToList();
        if (lines.Count >= 2 && lines[0].StartsWith("```") && lines[^1].Trim() == "```")
            lines = lines[1..^1];
        var message = string.Join("\n", lines).Trim();
        if (message.Length >= 2 && message[0] == '"' && message[^1] == '"' && !message[1..^1].Contains('"'))
            message = message[1..^1].Trim();
        return WrapBody(message);
    }

    /// <summary>Wraps body lines at 72 characters, as git convention asks; models often write a paragraph as one line.</summary>
    internal static string WrapBody(string message)
    {
        var lines = message.Split('\n');
        var result = new List<string> { lines[0] };   // the summary line is left as it is
        foreach (var line in lines.Skip(1))
        {
            if (line.Length <= 72)
            {
                result.Add(line);
                continue;
            }
            // A list item's continuation lines line up under its text.
            var lead = line[..(line.Length - line.TrimStart().Length)];
            var bullet = ListItem().Match(line);
            var hanging = new string(' ', bullet.Success ? bullet.Length : lead.Length);
            var current = new StringBuilder(lead);
            var empty = true;
            foreach (var word in line.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!empty && current.Length + 1 + word.Length > 72)
                {
                    result.Add(current.ToString());
                    current.Clear().Append(hanging);
                    empty = true;
                }
                if (!empty) current.Append(' ');
                current.Append(word);
                empty = false;
            }
            result.Add(current.ToString());
        }
        return string.Join("\n", result);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?:[-*]|\d+[.)])\s+")]
    private static partial System.Text.RegularExpressions.Regex ListItem();
}
