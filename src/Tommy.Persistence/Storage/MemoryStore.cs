using Tommy.Core.Memory;

namespace Tommy.Persistence.Storage;

/// <summary>
/// Reads/maintains a file-based memory directory (index.md + topic sub-files).
/// The app uses this to inject the always-loaded index into the prompt and to
/// run the "keep the index honest" reindex; the agent's own reads/writes go
/// through the memory tools.
/// </summary>
public sealed class MemoryStore
{
    private readonly string _dir;

    public MemoryStore(string dir) => _dir = dir;

    public string Directory => _dir;

    /// <summary>The always-loaded index (empty string when there is none yet).</summary>
    public string ReadIndex()
    {
        var path = Path.Combine(_dir, MemoryLayout.IndexFile);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public bool HasContent()
    {
        if (!System.IO.Directory.Exists(_dir)) return false;
        return System.IO.Directory.EnumerateFiles(_dir, "*.md")
            .Any(f => Path.GetFileName(f) != MemoryLayout.IndexFile);
    }

    /// <summary>Regenerate index.md descriptions from the sub-files' contents.</summary>
    public void Reindex()
    {
        if (!System.IO.Directory.Exists(_dir)) return;

        var lines = new List<string> { "# Memory index", "" };
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.md").OrderBy(f => f))
        {
            if (Path.GetFileName(file) == MemoryLayout.IndexFile) continue;
            var key = Path.GetFileNameWithoutExtension(file);
            lines.Add($"- {key}: {FirstLine(File.ReadAllText(file))}");
        }

        File.WriteAllText(Path.Combine(_dir, MemoryLayout.IndexFile),
            string.Join('\n', lines).TrimEnd() + "\n");
    }

    private static string FirstLine(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var t = line.Trim().TrimStart('#').Trim();
            if (t.Length > 0) return t.Length > 80 ? t[..80] : t;
        }
        return "(note)";
    }
}
