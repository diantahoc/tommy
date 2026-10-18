using System.Text;

namespace Tommy.Persistence.Storage;

/// <summary>
/// Loads cascading convention files (CLAUDE.md and friends) by walking up the
/// directory tree from the workspace folder, stopping before the drive root —
/// read-only inherited context. Ancestors load first, nearer files last (nearer
/// wins when the model reads top-to-bottom). The downward-lazy part is deferred.
/// </summary>
public static class ConventionLoader
{
    private static readonly string[] FileNames = { "CLAUDE.md", ".clinerules", "CONVENTIONS.md" };

    public static string LoadUpward(string startDir)
    {
        if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir))
            return string.Empty;

        var dirs = new List<string>();
        var current = Path.GetFullPath(startDir);
        while (true)
        {
            // Stop before the filesystem / drive root.
            if (string.Equals(Path.GetPathRoot(current), current, StringComparison.OrdinalIgnoreCase))
                break;

            dirs.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        dirs.Reverse(); // ancestors first

        var sb = new StringBuilder();
        foreach (var dir in dirs)
        {
            foreach (var name in FileNames)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                sb.AppendLine($"## {path}").AppendLine(File.ReadAllText(path).Trim()).AppendLine();
            }
        }

        return sb.ToString().Trim();
    }
}
