using System.Text.Json;
using Tommy.Core.Skills;
using Tommy.Core.Tools;
using Tommy.Persistence.Storage;
using Tommy.Tools;

namespace Tommy.Tests;

public class SkillMarkdownTests
{
    [Fact]
    public void Parses_frontmatter_and_body()
    {
        var text = "---\nname: demo\ndescription: When to use demo.\n---\n# Body\nDo the thing.";
        var (name, desc, body) = SkillMarkdown.Parse(text, "fallback");
        Assert.Equal("demo", name);
        Assert.Equal("When to use demo.", desc);
        Assert.StartsWith("# Body", body);
    }

    [Fact]
    public void Falls_back_to_name_without_frontmatter()
    {
        var (name, desc, body) = SkillMarkdown.Parse("just a body", "myskill");
        Assert.Equal("myskill", name);
        Assert.Equal("", desc);
        Assert.Equal("just a body", body);
    }
}

public class SkillRepositoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tommy-skills-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Starter_skills_seed_five_and_are_idempotent()
    {
        StarterSkills.SeedIfEmpty(_dir);
        var repo = new SkillRepository(_dir);
        Assert.Equal(5, repo.List().Count);

        StarterSkills.SeedIfEmpty(_dir); // no duplication
        Assert.Equal(5, repo.List().Count);

        Assert.NotNull(repo.Load("memory-management"));
        Assert.Contains(repo.Search("git"), s => s.Name == "git-workflow");
    }
}

public class SkillToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tommy-skilltool-").FullName;
    private readonly SkillRepository _repo;

    public SkillToolTests()
    {
        StarterSkills.SeedIfEmpty(_dir);
        _repo = new SkillRepository(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);
    private static readonly ToolContext Ctx = new("/tmp");

    [Fact]
    public async Task Search_returns_descriptions()
    {
        var r = await new SkillSearchTool(_repo).ExecuteAsync(El(new { query = "edit" }), Ctx, default);
        Assert.True(r.Success);
        Assert.Contains("structured-code-editing", r.Content);
    }

    [Fact]
    public async Task Load_returns_body_or_error()
    {
        var ok = await new SkillLoadTool(_repo).ExecuteAsync(El(new { name = "git-workflow" }), Ctx, default);
        Assert.True(ok.Success);
        Assert.Contains("Git workflow", ok.Content);

        var missing = await new SkillLoadTool(_repo).ExecuteAsync(El(new { name = "nope" }), Ctx, default);
        Assert.False(missing.Success);
    }

    [Fact]
    public async Task Load_exposes_skill_directory_and_companion_files()
    {
        // A custom skill with a companion script.
        var skillDir = Path.Combine(_dir, "custom-skill");
        Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: custom-skill\ndescription: Does a thing.\n---\nRun scripts/run.py.");
        var script = Path.Combine(skillDir, "scripts", "run.py");
        File.WriteAllText(script, "print('hi')");

        var r = await new SkillLoadTool(_repo).ExecuteAsync(El(new { name = "custom-skill" }), Ctx, default);
        Assert.True(r.Success);
        Assert.Contains(skillDir, r.Content);              // the skill directory is stated
        Assert.Contains(script, r.Content);                // the companion script's absolute path
        Assert.Contains("Run scripts/run.py.", r.Content); // the body is still present

        // And read_file can reach that script by absolute path, since the skills dir
        // is a trusted read root — even though it's outside the workspace.
        var workspace = Directory.CreateTempSubdirectory("tommy-ws-").FullName;
        try
        {
            var readCtx = new ToolContext(workspace, ReadRoots: new[] { _dir });
            var read = await new ReadFileTool().ExecuteAsync(El(new { path = script }), readCtx, default);
            Assert.True(read.Success);
            Assert.Contains("print('hi')", read.Content);

            // A path outside both the workspace and the read roots is still refused.
            var outside = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".txt");
            var denied = await new ReadFileTool().ExecuteAsync(El(new { path = outside }), readCtx, default);
            Assert.False(denied.Success);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }
}
