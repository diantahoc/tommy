using System.Diagnostics;
using System.Text.Json;
using Tommy.Core.Tools;
using Tommy.Tools;

namespace Tommy.Tests;

public class GitToolsTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public GitToolsTests()
    {
        _root = Directory.CreateTempSubdirectory("tommy-git-").FullName;
        _ctx = new ToolContext(_root);

        Git("init", "-q");
        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "Tommy Test");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (int Code, string Out) Git(params string[] args) => GitIn(_root, args);

    private static (int Code, string Out) GitIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr);
    }

    /// <summary>Creates <paramref name="name"/> as its own git repo under the workspace root.</summary>
    private string InitSubRepo(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        GitIn(dir, "init", "-q");
        GitIn(dir, "config", "user.email", "t@example.com");
        GitIn(dir, "config", "user.name", "Tommy Test");
        GitIn(dir, "config", "commit.gpgsign", "false");
        GitIn(dir, "config", "core.autocrlf", "false");
        return dir;
    }

    private static JsonElement Args(string json = "{}") => JsonDocument.Parse(json).RootElement.Clone();

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_root, name), content);

    private async Task<ToolResult> Run(ITool tool, string json = "{}") =>
        await tool.ExecuteAsync(Args(json), _ctx, default);

    // --- Reads --------------------------------------------------------------

    [Fact]
    public async Task Status_lists_untracked_file()
    {
        Write("a.txt", "hello");
        var result = await Run(new GitStatusTool());
        Assert.True(result.Success);
        Assert.Contains("a.txt", result.Content);
    }

    [Fact]
    public async Task Add_then_commit_then_log()
    {
        Write("a.txt", "hello");
        Assert.True((await Run(new GitAddTool(), """{"path":"a.txt"}""")).Success);

        var commit = await Run(new GitCommitTool(), """{"message":"first commit"}""");
        Assert.True(commit.Success);

        var log = await Run(new GitLogTool());
        Assert.True(log.Success);
        Assert.Contains("first commit", log.Content);
    }

    [Fact]
    public async Task Diff_staged_shows_added_file_then_commit_clears_it()
    {
        Write("a.txt", "line1\n");
        await Run(new GitAddTool(), """{"all":true}""");

        var staged = await Run(new GitDiffTool(), """{"staged":true}""");
        Assert.Equal(ToolResultKind.Diff, staged.Kind);
        Assert.Contains("a.txt", staged.Content);

        await Run(new GitCommitTool(), """{"message":"c1"}""");
        var afterCommit = await Run(new GitDiffTool(), """{"staged":true}""");
        Assert.Equal("(no output)", afterCommit.Content);
    }

    [Fact]
    public async Task Branch_marks_current()
    {
        Write("a.txt", "x");
        await Run(new GitAddTool(), """{"all":true}""");
        await Run(new GitCommitTool(), """{"message":"c1"}""");

        var branch = await Run(new GitBranchTool());
        Assert.True(branch.Success);
        Assert.Contains("*", branch.Content);
    }

    // --- Destructive restore ------------------------------------------------

    [Fact]
    public async Task Restore_discards_working_tree_change()
    {
        Write("a.txt", "original\n");
        await Run(new GitAddTool(), """{"all":true}""");
        await Run(new GitCommitTool(), """{"message":"c1"}""");

        Write("a.txt", "modified\n");
        var result = await Run(new GitRestoreTool(), """{"path":"a.txt"}""");
        Assert.True(result.Success);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Restore_preview_shows_lost_diff()
    {
        Write("a.txt", "original\n");
        await Run(new GitAddTool(), """{"all":true}""");
        await Run(new GitCommitTool(), """{"message":"c1"}""");
        Write("a.txt", "modified\n");

        var preview = await new GitRestoreTool().PreviewAsync(Args("""{"path":"a.txt"}"""), _ctx, default);
        Assert.NotNull(preview);
        Assert.Contains("modified", preview!.Diff);
    }

    // --- Nested repo (repo subfolder) --------------------------------------

    [Fact]
    public async Task Status_targets_a_repo_in_a_subfolder()
    {
        var sub = InitSubRepo("Project");
        File.WriteAllText(Path.Combine(sub, "inner.txt"), "hi");

        var result = await Run(new GitStatusTool(), """{"repo":"Project"}""");
        Assert.True(result.Success);
        Assert.Contains("inner.txt", result.Content);
    }

    [Fact]
    public async Task Add_commit_log_in_a_subfolder_repo_with_repo_relative_paths()
    {
        var sub = InitSubRepo("Project");
        File.WriteAllText(Path.Combine(sub, "inner.txt"), "hi");

        // path is relative to the repo subfolder, not the workspace root.
        Assert.True((await Run(new GitAddTool(), """{"repo":"Project","path":"inner.txt"}""")).Success);
        Assert.True((await Run(new GitCommitTool(), """{"repo":"Project","message":"sub commit"}""")).Success);

        var log = await Run(new GitLogTool(), """{"repo":"Project"}""");
        Assert.True(log.Success);
        Assert.Contains("sub commit", log.Content);
    }

    [Fact]
    public async Task Repo_subfolder_must_exist()
    {
        var result = await Run(new GitStatusTool(), """{"repo":"nope"}""");
        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Content);
    }

    [Fact]
    public async Task Repo_subfolder_cannot_escape_the_workspace()
    {
        var result = await Run(new GitStatusTool(), """{"repo":"../elsewhere"}""");
        Assert.False(result.Success);
        Assert.Contains("escapes", result.Content);
    }

    [Fact]
    public async Task Repos_lists_nested_repositories()
    {
        InitSubRepo(Path.Combine("Dashboard", "dashboard-backend"));
        InitSubRepo(Path.Combine("Dashboard", "dashboard-frontend"));

        var result = await Run(new GitReposTool());
        Assert.True(result.Success);
        // The workspace root is itself a repo (set up in the ctor), plus the two nested ones.
        Assert.Contains("workspace root", result.Content);
        Assert.Contains("dashboard-backend", result.Content);
        Assert.Contains("dashboard-frontend", result.Content);
    }

    [Fact]
    public async Task Repos_can_be_scoped_to_a_subfolder()
    {
        InitSubRepo(Path.Combine("Dashboard", "dashboard-backend"));

        var result = await Run(new GitReposTool(), """{"path":"Dashboard"}""");
        Assert.True(result.Success);
        Assert.Contains("dashboard-backend", result.Content);
        Assert.DoesNotContain("workspace root", result.Content);
    }

    // --- Guards -------------------------------------------------------------

    [Fact]
    public async Task Commit_requires_message()
    {
        var result = await Run(new GitCommitTool(), "{}");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Add_rejects_path_escaping_the_workspace()
    {
        var result = await Run(new GitAddTool(), """{"path":"../outside.txt"}""");
        Assert.False(result.Success);
        Assert.Contains("escapes", result.Content);
    }

    // --- Tiers / discovery --------------------------------------------------

    [Fact]
    public void Tiers_follow_the_catalog()
    {
        Assert.Equal(PermissionTier.Read, new GitStatusTool().Tier);
        Assert.Equal(PermissionTier.Write, new GitCommitTool().Tier);
        Assert.Equal(PermissionTier.Destructive, new GitRestoreTool().Tier);
        Assert.Equal(PermissionTier.Network, new GitPushTool().Tier);
    }

    [Fact]
    public void Git_tools_are_discovered_by_the_registry()
    {
        var registry = new ToolRegistry();
        registry.DiscoverFrom(typeof(GitStatusTool).Assembly);
        foreach (var name in new[] { "git_status", "git_repos", "git_log", "git_diff", "git_branch", "git_add", "git_commit", "git_restore", "git_push", "git_pull" })
            Assert.NotNull(registry.Find(name));
    }
}
