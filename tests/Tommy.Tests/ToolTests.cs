using System.Text.Json;
using Tommy.Core.Tools;
using Tommy.Tools;

namespace Tommy.Tests;

public class PermissionGridTests
{
    [Theory]
    // Read always allows
    [InlineData(PermissionTier.Read, AutonomyLevel.ReviewEach, PermissionAction.Allow)]
    [InlineData(PermissionTier.Read, AutonomyLevel.Auto, PermissionAction.Allow)]
    // Destructive always asks
    [InlineData(PermissionTier.Destructive, AutonomyLevel.ReviewEach, PermissionAction.Ask)]
    [InlineData(PermissionTier.Destructive, AutonomyLevel.Auto, PermissionAction.Ask)]
    // Write/Network/Exec: ask at review-each, allow otherwise
    [InlineData(PermissionTier.Write, AutonomyLevel.ReviewEach, PermissionAction.Ask)]
    [InlineData(PermissionTier.Write, AutonomyLevel.ReviewDestructive, PermissionAction.Allow)]
    [InlineData(PermissionTier.Write, AutonomyLevel.Auto, PermissionAction.Allow)]
    [InlineData(PermissionTier.Network, AutonomyLevel.ReviewEach, PermissionAction.Ask)]
    [InlineData(PermissionTier.Network, AutonomyLevel.Auto, PermissionAction.Allow)]
    [InlineData(PermissionTier.Exec, AutonomyLevel.ReviewEach, PermissionAction.Ask)]
    [InlineData(PermissionTier.Exec, AutonomyLevel.Auto, PermissionAction.Allow)]
    public void Resolve_matches_grid(PermissionTier tier, AutonomyLevel autonomy, PermissionAction expected)
    {
        Assert.Equal(expected, PermissionGrid.Resolve(tier, autonomy));
    }

    [Fact]
    public void Per_tool_override_wins()
    {
        Assert.Equal(PermissionAction.Allow,
            PermissionGrid.Resolve(PermissionTier.Destructive, AutonomyLevel.ReviewEach, PermissionAction.Allow));
        Assert.Equal(PermissionAction.Ask,
            PermissionGrid.Resolve(PermissionTier.Read, AutonomyLevel.Auto, PermissionAction.Ask));
    }
}

public class ToolRegistryTests
{
    [Fact]
    public void Discovers_native_file_tools()
    {
        var reg = new ToolRegistry();
        reg.DiscoverFrom(typeof(ReadFileTool).Assembly);

        Assert.NotNull(reg.Find("read_file"));
        Assert.NotNull(reg.Find("edit_file"));
        Assert.NotNull(reg.Find("search"));
        Assert.True(reg.Tools.Count >= 9);
    }

    [Fact]
    public void Resolves_aliases_and_is_case_insensitive()
    {
        var reg = new ToolRegistry();
        reg.DiscoverFrom(typeof(ReadFileTool).Assembly);
        Assert.Equal("read_file_range", reg.Find("read_range")?.Name);
        Assert.Equal("read_file_range", reg.Find("READ_FILE_RANGE")?.Name);
        Assert.Null(reg.Find("totally_made_up"));
    }

    [Fact]
    public void Tools_array_has_function_shape()
    {
        var reg = new ToolRegistry();
        reg.DiscoverFrom(typeof(ReadFileTool).Assembly);
        var arr = reg.BuildToolsArray(new HashSet<string> { "read_file" });

        Assert.Single(arr);
        var fn = arr[0]!["function"]!;
        Assert.Equal("read_file", (string?)fn["name"]);
        Assert.Equal("object", (string?)fn["parameters"]!["type"]);
    }
}

public class FileToolTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public FileToolTests()
    {
        _root = Directory.CreateTempSubdirectory("tommy-tools-").FullName;
        _ctx = new ToolContext(_root);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "line1\nline2\nline3\n");
        File.WriteAllText(Path.Combine(_root, "code.cs"), "class A { }\n// TODO: fix\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task LineCount_counts_lines()
    {
        var r = await new LineCountTool().ExecuteAsync(El(new { path = "hello.txt" }), _ctx, default);
        Assert.True(r.Success);
        Assert.Equal("3", r.Content);
    }

    [Fact]
    public async Task ReadRange_returns_numbered_lines()
    {
        var r = await new ReadRangeTool().ExecuteAsync(El(new { path = "hello.txt", start_line = 2, end_line = 3 }), _ctx, default);
        Assert.Contains("2: line2", r.Content);
        Assert.Contains("3: line3", r.Content);
        Assert.DoesNotContain("line1", r.Content);
    }

    [Fact]
    public async Task Search_finds_regex_matches()
    {
        var r = await new SearchTool().ExecuteAsync(El(new { pattern = "TODO", glob = "*.cs" }), _ctx, default);
        Assert.True(r.Success);
        Assert.Contains("code.cs:2", r.Content);
    }

    [Fact]
    public async Task FindFile_matches_glob()
    {
        var r = await new FindFileTool().ExecuteAsync(El(new { pattern = "*.cs" }), _ctx, default);
        Assert.Contains("code.cs", r.Content);
        Assert.DoesNotContain("hello.txt", r.Content);
    }

    [Fact]
    public async Task Edit_replaces_unique_block()
    {
        var tool = new EditFileTool();
        var r = await tool.ExecuteAsync(
            El(new { path = "hello.txt", old_text = "line2", new_text = "LINE-TWO" }), _ctx, default);
        Assert.True(r.Success);
        Assert.Equal(ToolResultKind.Diff, r.Kind);
        Assert.Contains("LINE-TWO", File.ReadAllText(Path.Combine(_root, "hello.txt")));
    }

    [Fact]
    public async Task Edit_fails_when_not_unique()
    {
        File.WriteAllText(Path.Combine(_root, "dup.txt"), "x\nx\n");
        var r = await new EditFileTool().ExecuteAsync(
            El(new { path = "dup.txt", old_text = "x", new_text = "y" }), _ctx, default);
        Assert.False(r.Success);
        Assert.Contains("not unique", r.Content);
    }

    [Fact]
    public async Task Edit_preview_shows_diff()
    {
        var p = await new EditFileTool().PreviewAsync(
            El(new { path = "hello.txt", old_text = "line2", new_text = "LINE-TWO" }), _ctx, default);
        Assert.NotNull(p);
        Assert.Contains("- line2", p!.Diff);
        Assert.Contains("+ LINE-TWO", p.Diff);
    }

    [Fact]
    public async Task Create_then_read()
    {
        var c = await new CreateFileTool().ExecuteAsync(
            El(new { path = "sub/new.txt", content = "hi" }), _ctx, default);
        Assert.True(c.Success);
        var r = await new ReadFileTool().ExecuteAsync(El(new { path = "sub/new.txt" }), _ctx, default);
        Assert.Equal("hi", r.Content);
    }

    [Fact]
    public async Task Sandbox_rejects_escape()
    {
        var r = await new ReadFileTool().ExecuteAsync(El(new { path = "../outside.txt" }), _ctx, default);
        Assert.False(r.Success);
        Assert.Contains("escapes", r.Content);
    }

    [Fact]
    public async Task Readonly_context_blocks_write()
    {
        var ro = new ToolContext(_root, ReadOnly: true);
        var r = await new EditFileTool().ExecuteAsync(
            El(new { path = "hello.txt", old_text = "line1", new_text = "x" }), ro, default);
        Assert.False(r.Success);
        Assert.Contains("read-only", r.Content);
    }

    [Fact]
    public async Task Rename_moves_file()
    {
        var r = await new RenameTool().ExecuteAsync(El(new { path = "hello.txt", to = "sub/renamed.txt" }), _ctx, default);
        Assert.True(r.Success);
        Assert.False(File.Exists(Path.Combine(_root, "hello.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "sub", "renamed.txt")));
    }

    [Fact]
    public async Task Rename_fails_when_destination_exists()
    {
        var r = await new RenameTool().ExecuteAsync(El(new { path = "hello.txt", to = "code.cs" }), _ctx, default);
        Assert.False(r.Success);
        Assert.Contains("already exists", r.Content);
    }

    [Fact]
    public async Task FileInfo_reports_file_and_directory()
    {
        var f = await new FileInfoTool().ExecuteAsync(El(new { path = "hello.txt" }), _ctx, default);
        Assert.True(f.Success);
        Assert.Contains("file", f.Content);

        Directory.CreateDirectory(Path.Combine(_root, "d"));
        var d = await new FileInfoTool().ExecuteAsync(El(new { path = "d" }), _ctx, default);
        Assert.True(d.Success);
        Assert.Contains("directory", d.Content);
    }

    [Fact]
    public async Task DeleteDir_moves_to_trash()
    {
        Directory.CreateDirectory(Path.Combine(_root, "doomed"));
        File.WriteAllText(Path.Combine(_root, "doomed", "x.txt"), "x");

        var r = await new DeleteDirTool().ExecuteAsync(El(new { path = "doomed" }), _ctx, default);
        Assert.True(r.Success);
        Assert.False(Directory.Exists(Path.Combine(_root, "doomed")));
        Assert.True(Directory.Exists(Path.Combine(_root, ".tommy-trash")));
    }

    [Fact]
    public async Task DeleteDir_refuses_root()
    {
        var r = await new DeleteDirTool().ExecuteAsync(El(new { path = "." }), _ctx, default);
        Assert.False(r.Success);
        Assert.Contains("working directory itself", r.Content);
    }
}
