using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Skills;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Search installed skills when the always-loaded descriptions aren't enough to
/// pick one. (Constructed with a skill provider and registered manually.)
/// </summary>
public sealed class SkillSearchTool : ITool
{
    private readonly ISkillProvider _skills;

    public SkillSearchTool(ISkillProvider skills) => _skills = skills;

    public string Name => "skill_search";
    public string Description => "Search installed skills by keyword (name, description, or body). Returns matches to then skill_load.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("query", ToolSchema.Str("Keywords describing the task."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var query = Args.Str(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(ToolResult.Fail("query is required."));

        var hits = _skills.Search(query);
        if (hits.Count == 0)
            return Task.FromResult(ToolResult.Ok("(no matching skills)"));

        var sb = new StringBuilder();
        foreach (var s in hits)
            sb.Append(s.Name).Append(": ").AppendLine(s.Description);
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }
}

/// <summary>Load a skill's full body by name (progressive disclosure).</summary>
public sealed class SkillLoadTool : ITool
{
    private readonly ISkillProvider _skills;

    public SkillLoadTool(ISkillProvider skills) => _skills = skills;

    public string Name => "skill_load";
    public string Description => "Load the full instructions for a skill by name.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("name", ToolSchema.Str("The skill name."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var name = Args.Str(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Fail("name is required."));

        var skill = _skills.Load(name);
        if (skill is null)
            return Task.FromResult(ToolResult.Fail($"no skill named '{name}'. Use skill_search to find one."));

        return Task.FromResult(ToolResult.Ok(WithSkillContext(skill), ToolResultKind.Text, skill.Name));
    }

    /// <summary>
    /// Prepend the skill's on-disk location and companion files so the model can
    /// reach a script the SKILL.md references relatively (the body's paths are
    /// relative to the skill directory; tools need the absolute path).
    /// </summary>
    private static string WithSkillContext(Skill skill)
    {
        var dir = string.IsNullOrEmpty(skill.Path) ? null : Path.GetDirectoryName(skill.Path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return skill.Body;

        var sb = new StringBuilder();
        sb.Append("Skill directory (absolute): ").AppendLine(dir);

        try
        {
            // Companion files (besides SKILL.md), with absolute paths the model can
            // pass straight to a tool. Capped so a large skill can't flood context.
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToList();
            if (files.Count > 0)
            {
                sb.AppendLine("Companion files (use these absolute paths when a tool needs one):");
                foreach (var f in files)
                    sb.Append("  - ").AppendLine(f);
            }
        }
        catch
        {
            // Listing is best-effort; the directory line above is the essential part.
        }

        sb.AppendLine("The instructions below reference files relative to the skill directory; resolve them against it.");
        sb.AppendLine();
        sb.Append(skill.Body);
        return sb.ToString();
    }
}
