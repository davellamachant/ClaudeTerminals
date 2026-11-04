using System.Text;

namespace LlamaTree.Sessions;

/// <summary>
/// The end of a session's terminal output, kept so a window that attaches later (reopened while the session ran on)
/// can show it. Cut at a line break when it's trimmed, so what's kept doesn't start inside an escape sequence.
/// </summary>
internal sealed class OutputTail(int max = OutputTail.DefaultMax)
{
    public const int DefaultMax = 512 * 1024;

    private readonly StringBuilder text = new();

    public void Append(string output)
    {
        lock (text)
        {
            text.Append(output);
            if (text.Length <= max) return;
            var cut = text.Length - max * 3 / 4;   // trimmed to 3/4, so it isn't trimmed again on every write
            var s = text.ToString();
            var newline = s.IndexOf('\n', cut);
            text.Clear().Append(newline >= 0 ? s[(newline + 1)..] : s[cut..]);
        }
    }

    public void Clear()
    {
        lock (text) text.Clear();
    }

    public override string ToString()
    {
        lock (text) return text.ToString();
    }
}
