using System.Text.Json;
using Tommy.Core.Chat;

namespace Tommy.Persistence.Storage;

/// <summary>A chat opened for reading/writing: its header, event log, and blobs.</summary>
public sealed record ChatHandle(ChatRecord Record, EventStore Events, BlobStore Blobs);

/// <summary>
/// Stores chats under <c>~/.tommy/chats/&lt;id&gt;/</c> — chat.json, events.jsonl, and
/// a per-chat blobs/ directory, so a chat is self-contained and portable.
/// </summary>
public sealed class ChatRepository
{
    private readonly string _root;

    public ChatRepository(string? root = null) => _root = root ?? TommyPaths.ChatsDir;

    private string Dir(string id) => Path.Combine(_root, id);
    private string MetaPath(string id) => Path.Combine(Dir(id), "chat.json");

    public ChatHandle Create(ChatRecord record)
    {
        Directory.CreateDirectory(Dir(record.Id));
        SaveMeta(record);
        return Open(record);
    }

    public void SaveMeta(ChatRecord record)
    {
        record.Updated = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Dir(record.Id));
        File.WriteAllText(MetaPath(record.Id), JsonSerializer.Serialize(record, StoreJson.Options));
    }

    public ChatRecord? LoadMeta(string id)
    {
        var path = MetaPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(path), StoreJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ChatHandle Open(ChatRecord record) => new(
        record,
        new EventStore(Path.Combine(Dir(record.Id), "events.jsonl")),
        new BlobStore(Path.Combine(Dir(record.Id), "blobs")));

    public ChatHandle? Open(string id)
    {
        var record = LoadMeta(id);
        return record is null ? null : Open(record);
    }

    /// <summary>All chats, most-recently-updated first.</summary>
    public IReadOnlyList<ChatRecord> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<ChatRecord>();

        var records = new List<ChatRecord>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var record = LoadMeta(Path.GetFileName(dir));
            if (record is not null) records.Add(record);
        }
        return records.OrderByDescending(r => r.Updated).ToList();
    }

    public void Delete(string id)
    {
        var dir = Dir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Duplicate a chat under a new id — copies its header, event log, and blobs,
    /// so the fork is independent. Returns the new record (not yet pinned/active).
    /// </summary>
    public ChatRecord? Fork(string sourceId, string newId)
    {
        var source = LoadMeta(sourceId);
        if (source is null) return null;

        var forked = new ChatRecord
        {
            Id = newId,
            Kind = source.Kind,
            Title = source.Title + " (fork)",
            ProjectId = source.ProjectId,
            WorkspaceId = source.WorkspaceId,
            ReadOnlyFolder = source.ReadOnlyFolder,
            Parameters = source.Parameters,
            Created = DateTimeOffset.UtcNow
        };

        Directory.CreateDirectory(Dir(newId));
        SaveMeta(forked);

        var srcEvents = Path.Combine(Dir(sourceId), "events.jsonl");
        if (File.Exists(srcEvents))
            File.Copy(srcEvents, Path.Combine(Dir(newId), "events.jsonl"), overwrite: true);

        var srcBlobs = Path.Combine(Dir(sourceId), "blobs");
        if (Directory.Exists(srcBlobs))
        {
            var dstBlobs = Path.Combine(Dir(newId), "blobs");
            Directory.CreateDirectory(dstBlobs);
            foreach (var file in Directory.EnumerateFiles(srcBlobs))
                File.Copy(file, Path.Combine(dstBlobs, Path.GetFileName(file)), overwrite: true);
        }

        return forked;
    }
}
