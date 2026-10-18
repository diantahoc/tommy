using System.Text.Json;
using Tommy.Core.Configuration;
using Tommy.Persistence.Serialization;

namespace Tommy.Persistence;

public interface IConfigStore
{
    /// <summary>Load config, returning a fresh default when none exists yet.</summary>
    TommyConfig Load();

    /// <summary>Persist config to disk (creating the directory as needed).</summary>
    void Save(TommyConfig config);

    string Path { get; }
}

/// <summary>JSON-backed global config store (<c>~/.tommy/config.json</c>).</summary>
public sealed class ConfigStore : IConfigStore
{
    public ConfigStore(string? path = null)
    {
        Path = path ?? TommyPaths.ConfigFile;
    }

    public string Path { get; }

    public TommyConfig Load()
    {
        if (!File.Exists(Path))
            return new TommyConfig();

        try
        {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.TommyConfig)
                   ?? new TommyConfig();
        }
        catch (JsonException)
        {
            // Corrupt config shouldn't brick startup; fall back to first-run.
            return new TommyConfig();
        }
    }

    public void Save(TommyConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, PersistenceJsonContext.Default.TommyConfig);
        File.WriteAllText(Path, json);
    }
}
