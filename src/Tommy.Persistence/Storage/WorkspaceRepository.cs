using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tommy.Core.Workspaces;

namespace Tommy.Persistence.Storage;

/// <summary>
/// Stores workspaces under <c>~/.tommy/workspaces/&lt;id&gt;/</c>, where the id is a hash
/// of the selected folder path (the folder itself is referenced, never copied in).
/// </summary>
public sealed class WorkspaceRepository
{
    private readonly string _root;

    public WorkspaceRepository(string? root = null) => _root = root ?? TommyPaths.WorkspacesDir;

    private string Dir(string id) => Path.Combine(_root, id);
    private string MetaPath(string id) => Path.Combine(Dir(id), "workspace.json");

    public string MemoryDir(string id) => Path.Combine(Dir(id), "memory");

    /// <summary>Stable id for a folder path (hash of the normalized full path).</summary>
    public static string HashFolder(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    public WorkspaceRecord GetOrCreate(string folderPath)
    {
        var id = HashFolder(folderPath);
        var existing = Load(id);
        if (existing is not null) return existing;

        var record = new WorkspaceRecord { Id = id, FolderPath = Path.GetFullPath(folderPath) };
        Save(record);
        return record;
    }

    public void Save(WorkspaceRecord record)
    {
        Directory.CreateDirectory(Dir(record.Id));
        Directory.CreateDirectory(MemoryDir(record.Id));
        File.WriteAllText(MetaPath(record.Id), JsonSerializer.Serialize(record, StoreJson.Options));
    }

    public WorkspaceRecord? Load(string id)
    {
        var path = MetaPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<WorkspaceRecord>(File.ReadAllText(path), StoreJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void AddChat(string workspaceId, string chatId)
    {
        var record = Load(workspaceId);
        if (record is null) return;
        if (!record.MemberChatIds.Contains(chatId))
        {
            record.MemberChatIds.Add(chatId);
            Save(record);
        }
    }

    public IReadOnlyList<WorkspaceRecord> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<WorkspaceRecord>();

        var records = new List<WorkspaceRecord>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var record = Load(Path.GetFileName(dir));
            if (record is not null) records.Add(record);
        }
        return records;
    }
}
