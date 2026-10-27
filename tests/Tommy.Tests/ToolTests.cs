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
    // AutoReviewed: Exec/Network are reviewed; reads allow, writes allow, destructive asks
    [InlineData(PermissionTier.Read, AutonomyLevel.AutoReviewed, PermissionAction.Allow)]
    [InlineData(PermissionTier.Write, AutonomyLevel.AutoReviewed, PermissionAction.Allow)]
    [InlineData(PermissionTier.Destructive, AutonomyLevel.AutoReviewed, PermissionAction.Ask)]
    [InlineData(PermissionTier.Exec, AutonomyLevel.AutoReviewed, PermissionAction.Review)]
    [InlineData(PermissionTier.Network, AutonomyLevel.AutoReviewed, PermissionAction.Review)]
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

    [Fact]
    public void Default_tools_exclude_external_mcp_tools()
    {
        var reg = new ToolRegistry();
        reg.Register(new FakeTool("native_x", external: false));
        reg.Register(new FakeTool("mcp_y", external: true));

        // Null enabled set = "all tools": native only, MCP tools stay opt-in.
        var dflt = Names(reg.BuildToolsArray(null));
        Assert.Contains("native_x", dflt);
        Assert.DoesNotContain("mcp_y", dflt);

        // An explicit enabled set advertises the named MCP tool (how a chat that
        // connected the server gets it).
        var enabled = Names(reg.BuildToolsArray(new HashSet<string> { "native_x", "mcp_y" }));
        Assert.Contains("mcp_y", enabled);
    }

    private static List<string?> Names(System.Text.Json.Nodes.JsonArray arr) =>
        arr.Select(t => (string?)t!["function"]!["name"]).ToList();

    private sealed class FakeTool : ITool
    {
        public FakeTool(string name, bool external) { Name = name; IsExternal = external; }
        public string Name { get; }
        public string Description => "fake";
        public PermissionTier Tier => PermissionTier.Read;
        public bool IsExternal { get; }
        public System.Text.Json.Nodes.JsonObject ParameterSchema => ToolSchema.Object();
        public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
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
    public async Task Search_skips_binary_files()
    {
        // A "binary" file containing the pattern plus a NUL byte must be skipped,
        // while a text file with the same pattern is still matched.
        File.WriteAllBytes(Path.Combine(_root, "lib.dll"),
            System.Text.Encoding.UTF8.GetBytes("TODO fix\0\u0001\u0002binary"));
        File.WriteAllText(Path.Combine(_root, "notes.md"), "TODO fix me\n");

        var r = await new SearchTool().ExecuteAsync(El(new { pattern = "TODO" }), _ctx, default);
        Assert.True(r.Success);
        Assert.Contains("notes.md", r.Content);
        Assert.DoesNotContain("lib.dll", r.Content);
    }

    [Fact]
    public async Task Search_skips_dependency_dirs_but_search_all_includes_them()
    {
        // The same token in node_modules (a heavy dir) and in src.
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "dep.js"), "NEEDLE here\n");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "app.js"), "NEEDLE here\n");

        var pruned = await new SearchTool().ExecuteAsync(El(new { pattern = "NEEDLE" }), _ctx, default);
        Assert.True(pruned.Success);
        Assert.Contains("app.js", pruned.Content);
        Assert.DoesNotContain("node_modules", pruned.Content);

        var all = await new SearchAllTool().ExecuteAsync(El(new { pattern = "NEEDLE" }), _ctx, default);
        Assert.True(all.Success);
        Assert.Contains("app.js", all.Content);
        Assert.Contains("node_modules", all.Content);
    }

    [Fact]
    public async Task Search_can_be_pointed_into_a_dependency_dir()
    {
        // Pruning applies to discovered subdirs, not the walk root: an explicit path
        // into node_modules is honoured.
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "dep.js"), "NEEDLE here\n");

        var r = await new SearchTool().ExecuteAsync(
            El(new { pattern = "NEEDLE", path = "node_modules" }), _ctx, default);
        Assert.True(r.Success);
        Assert.Contains("dep.js", r.Content);
    }

    [Fact]
    public async Task FindFile_matches_glob()
    {
        var r = await new FindFileTool().ExecuteAsync(El(new { pattern = "*.cs" }), _ctx, default);
        Assert.Contains("code.cs", r.Content);
        Assert.DoesNotContain("hello.txt", r.Content);
    }

    [Fact]
    public async Task FindFile_skips_dependency_dirs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "junk.cs"), "// x\n");

        var r = await new FindFileTool().ExecuteAsync(El(new { pattern = "*.cs" }), _ctx, default);
        Assert.Contains("code.cs", r.Content);
        Assert.DoesNotContain("node_modules", r.Content);
    }

    [Fact]
    public async Task AskQuestion_returns_the_users_answer()
    {
        Tommy.Core.Tools.UserQuestion? seen = null;
        var ctx = _ctx with { AskUser = (q, _) => { seen = q; return Task.FromResult("Blue"); } };

        var r = await new AskQuestionTool().ExecuteAsync(
            El(new { question = "Pick a color", options = new[] { "Red", "Blue" } }), ctx, default);

        Assert.True(r.Success);
        Assert.Equal("Blue", r.Content);
        Assert.NotNull(seen);
        Assert.Equal("Pick a color", seen!.Question);
        Assert.Equal(2, seen.Options.Count);
    }

    [Fact]
    public async Task AskQuestion_without_a_handler_fails_gracefully()
    {
        var r = await new AskQuestionTool().ExecuteAsync(El(new { question = "Anything?" }), _ctx, default);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task PresentFile_opens_an_existing_file()
    {
        Tommy.Core.Tools.PresentedFile? shown = null;
        var ctx = _ctx with { PresentFile = f => shown = f };

        var r = await new PresentFileTool().ExecuteAsync(El(new { path = "hello.txt", title = "Greeting" }), ctx, default);

        Assert.True(r.Success);
        Assert.NotNull(shown);
        Assert.Equal("Greeting", shown!.Title);
        Assert.EndsWith("hello.txt", shown.Path.Replace('\\', '/'));
    }

    [Fact]
    public async Task PresentFile_missing_file_fails()
    {
        var ctx = _ctx with { PresentFile = _ => { } };
        var r = await new PresentFileTool().ExecuteAsync(El(new { path = "nope.txt" }), ctx, default);
        Assert.False(r.Success);
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

    [Fact]
    public async Task Edit_matches_across_crlf_and_preserves_endings()
    {
        File.WriteAllText(Path.Combine(_root, "crlf.txt"), "line1\r\nline2\r\nline3\r\n");
        // old_text uses \n but the file is CRLF — must still match and stay CRLF.
        var r = await new EditFileTool().ExecuteAsync(
            El(new { path = "crlf.txt", old_text = "line1\nline2", new_text = "LINE1\nLINE2" }), _ctx, default);
        Assert.True(r.Success, r.Content);
        var after = File.ReadAllText(Path.Combine(_root, "crlf.txt"));
        Assert.Contains("LINE1\r\nLINE2", after);
        Assert.Contains("line3\r\n", after);
    }

    [Fact]
    public async Task Edit_tolerates_indentation_mismatch()
    {
        File.WriteAllText(Path.Combine(_root, "ind.txt"), "    foo\n    bar\n");
        // old_text has no indentation; the tool should match leniently and re-indent.
        var r = await new EditFileTool().ExecuteAsync(
            El(new { path = "ind.txt", old_text = "foo\nbar", new_text = "baz\nqux" }), _ctx, default);
        Assert.True(r.Success, r.Content);
        var after = File.ReadAllText(Path.Combine(_root, "ind.txt"));
        Assert.Contains("    baz", after);
        Assert.Contains("    qux", after);
    }

    [Fact]
    public async Task Edit_not_found_gives_hint()
    {
        var r = await new EditFileTool().ExecuteAsync(
            El(new { path = "hello.txt", old_text = "nonexistent\nblock", new_text = "x" }), _ctx, default);
        Assert.False(r.Success);
        Assert.Contains("not found", r.Content);
    }

    [Fact]
    public async Task Insert_before_line_and_append()
    {
        File.WriteAllText(Path.Combine(_root, "ins.txt"), "a\nb\nc\n");
        var r1 = await new InsertFileTool().ExecuteAsync(El(new { path = "ins.txt", text = "X", line = 2 }), _ctx, default);
        Assert.True(r1.Success, r1.Content);
        Assert.Equal("a\nX\nb\nc\n", File.ReadAllText(Path.Combine(_root, "ins.txt")));

        var r2 = await new InsertFileTool().ExecuteAsync(El(new { path = "ins.txt", text = "Z" }), _ctx, default);
        Assert.True(r2.Success);
        Assert.Equal("a\nX\nb\nc\nZ\n", File.ReadAllText(Path.Combine(_root, "ins.txt")));
    }

    [Fact]
    public async Task ReplaceLines_replaces_range()
    {
        File.WriteAllText(Path.Combine(_root, "rep.txt"), "a\nb\nc\nd\n");
        var r = await new ReplaceLinesTool().ExecuteAsync(
            El(new { path = "rep.txt", start_line = 2, end_line = 3, new_text = "X\nY\nZ" }), _ctx, default);
        Assert.True(r.Success, r.Content);
        Assert.Equal("a\nX\nY\nZ\nd\n", File.ReadAllText(Path.Combine(_root, "rep.txt")));
    }
}
