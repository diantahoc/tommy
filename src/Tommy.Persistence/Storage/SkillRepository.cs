using Tommy.Core.Skills;

namespace Tommy.Persistence.Storage;

/// <summary>
/// Reads installed skills from <c>~/.tommy/skills/&lt;name&gt;/SKILL.md</c>. Only the
/// descriptions stay in context; the body is loaded on demand via skill_load.
/// </summary>
public sealed class SkillRepository : ISkillProvider
{
    private readonly string _dir;

    public SkillRepository(string? dir = null) => _dir = dir ?? TommyPaths.SkillsDir;

    public IReadOnlyList<Skill> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<Skill>();

        var skills = new List<Skill>();
        foreach (var sub in Directory.EnumerateDirectories(_dir))
        {
            var path = Path.Combine(sub, "SKILL.md");
            if (!File.Exists(path)) continue;
            try
            {
                var (name, description, body) = SkillMarkdown.Parse(File.ReadAllText(path), Path.GetFileName(sub));
                skills.Add(new Skill { Name = name, Description = description, Body = body, Path = path });
            }
            catch
            {
                // skip an unreadable skill rather than failing the whole list
            }
        }
        return skills.OrderBy(s => s.Name).ToList();
    }

    public Skill? Load(string name) =>
        List().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<Skill> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<Skill>();
        bool Has(string s) => s.Contains(query, StringComparison.OrdinalIgnoreCase);
        return List().Where(s => Has(s.Name) || Has(s.Description) || Has(s.Body)).ToList();
    }
}
