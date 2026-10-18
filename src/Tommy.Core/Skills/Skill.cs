namespace Tommy.Core.Skills;

/// <summary>
/// A just-in-time instruction set: a short description ("when to use this") kept
/// in context, and a longer body loaded on demand. Claude-compatible: a folder
/// with a SKILL.md (frontmatter description + markdown body).
/// </summary>
public sealed class Skill
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Body { get; init; }
    public string? Path { get; init; }
}

/// <summary>Source of installed skills (implemented by the persistence layer).</summary>
public interface ISkillProvider
{
    IReadOnlyList<Skill> List();
    Skill? Load(string name);
    IReadOnlyList<Skill> Search(string query);
}
