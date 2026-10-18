using System.Text;

namespace Tommy.Tools.Internal;

/// <summary>Minimal block diff for edit previews: old lines as -, new lines as +.</summary>
internal static class Diff
{
    public static string Block(string? oldText, string newText)
    {
        var sb = new StringBuilder();
        if (oldText is not null)
            foreach (var line in oldText.Replace("\r\n", "\n").Split('\n'))
                sb.Append("- ").AppendLine(line);
        foreach (var line in newText.Replace("\r\n", "\n").Split('\n'))
            sb.Append("+ ").AppendLine(line);
        return sb.ToString().TrimEnd();
    }
}
