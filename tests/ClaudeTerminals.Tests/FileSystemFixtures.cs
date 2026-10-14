using System.Diagnostics;

namespace ClaudeTerminals.Tests;

/// <summary>Real Windows file-system objects for the containment and case-sensitivity tests.</summary>
internal static class FileSystemFixtures
{
    /// <summary>Creates a directory junction (no elevation needed, unlike symbolic links).</summary>
    public static void Junction(string link, string target)
    {
        Run("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"");
        Assert.True(Directory.Exists(link), $"could not create junction {link}");
    }

    /// <summary>Makes an empty folder case-sensitive, so a.txt and A.txt are two different files in it.</summary>
    public static void MakeCaseSensitive(string emptyFolder)
    {
        Directory.CreateDirectory(emptyFolder);
        Run("fsutil.exe", $"file setCaseSensitiveInfo \"{emptyFolder}\" enable");
        File.WriteAllText(Path.Combine(emptyFolder, "probe.txt"), "");
        Assert.False(File.Exists(Path.Combine(emptyFolder, "PROBE.TXT")), $"{emptyFolder} is not case-sensitive");
        File.Delete(Path.Combine(emptyFolder, "probe.txt"));
    }

    /// <summary>Deletes a folder tree, removing junctions without following them.</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            if (new DirectoryInfo(dir).LinkTarget != null) Directory.Delete(dir);
            else DeleteTree(dir);
        }
        foreach (var f in Directory.EnumerateFiles(path))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        Directory.Delete(path);
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
    }
}
