using System.Text.Json;
using Tommy.Core.Projects;

namespace Tommy.Persistence.Storage;

/// <summary>Stores projects under <c>~/.tommy/projects/&lt;id&gt;/</c> (project.json + memory/).</summary>
public sealed class ProjectRepository
{
    private readonly string _root;

    public ProjectRepository(string? root = null) => _root = root ?? TommyPaths.ProjectsDir;

    private string Dir(string id) => Path.Combine(_root, id);
    private string MetaPath(string id) => Path.Combine(Dir(id), "project.json");

    public string MemoryDir(string id) => Path.Combine(Dir(id), "memory");

    public void Save(ProjectRecord record)
    {
        Directory.CreateDirectory(Dir(record.Id));
        Directory.CreateDirectory(MemoryDir(record.Id));
        File.WriteAllText(MetaPath(record.Id), JsonSerializer.Serialize(record, StoreJson.Options));
    }

    public ProjectRecord? Load(string id)
    {
        var path = MetaPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProjectRecord>(File.ReadAllText(path), StoreJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<ProjectRecord> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<ProjectRecord>();

        var records = new List<ProjectRecord>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var record = Load(Path.GetFileName(dir));
            if (record is not null) records.Add(record);
        }
        return records.OrderBy(r => r.Name).ToList();
    }

    public void Delete(string id)
    {
        var dir = Dir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
