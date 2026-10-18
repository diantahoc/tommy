using System.Text.Json;
using Tommy.Core.Events;

namespace Tommy.Persistence.Storage;

/// <summary>
/// Append-only JSONL event log for one chat — one record per event, replays
/// faithfully, streams naturally.
/// </summary>
public sealed class EventStore
{
    private readonly string _path;

    public EventStore(string path) => _path = path;

    public string Path => _path;

    public void Append(ChatEvent e)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var line = JsonSerializer.Serialize<ChatEvent>(e, StoreJson.EventOptions);
        File.AppendAllText(_path, line + "\n");
    }

    public IEnumerable<ChatEvent> Read()
    {
        if (!File.Exists(_path)) yield break;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var e = TryParse(line);
            if (e is not null) yield return e;
        }
    }

    private static ChatEvent? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatEvent>(line, StoreJson.EventOptions);
        }
        catch (JsonException)
        {
            return null; // tolerate a corrupt line rather than losing the whole log
        }
    }
}
