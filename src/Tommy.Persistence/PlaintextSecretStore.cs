using System.Text.Json;
using Tommy.Core.Security;
using Tommy.Persistence.Serialization;

namespace Tommy.Persistence;

/// <summary>
/// v1 secret store: a plaintext JSON map at <c>~/.tommy/secrets.json</c>.
/// Acceptable for a single-user local tool but readable by anything running as
/// the user — it sits behind <see cref="ISecretStore"/> so the OS store can
/// replace it later without a data migration.
/// </summary>
public sealed class PlaintextSecretStore : ISecretStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;

    public PlaintextSecretStore(string? path = null)
    {
        _path = path ?? TommyPaths.SecretsFile;
        _values = LoadFile(_path);
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value)
    {
        _values[key] = value;
        Flush();
    }

    public void Delete(string key)
    {
        if (_values.Remove(key))
            Flush();
    }

    private static Dictionary<string, string> LoadFile(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.DictionaryStringString)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private void Flush()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_values, PersistenceJsonContext.Default.DictionaryStringString);
        File.WriteAllText(_path, json);
    }
}
