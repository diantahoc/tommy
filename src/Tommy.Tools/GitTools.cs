using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Typed git tools — log, diff, status, branch, add, commit, restore, push,
/// pull — each wrapping the git CLI behind a fixed argument list so the model
/// calls a schema, never a shell string. Reads are <c>Read</c>; staging and
/// commits are <c>Write</c>; restore is <c>Destructive</c>; push/pull are
/// <c>Network</c> (approval). The repo is the chat's working directory.
/// </summary>
internal static class GitShared
{
    public const int MaxOutput = 20_000;

    public static string Cap(string s) =>
        s.Length <= MaxOutput ? s : s[..MaxOutput] + $"\n… (truncated at {MaxOutput} chars)";

    public static ToolResult FromRun(GitRunner.GitResult r, string title, ToolResultKind okKind = ToolResultKind.Text)
    {
        var text = r.Combined.Trim();
        if (!r.Ok)
            return ToolResult.Fail(text.Length > 0 ? text : $"git exited with code {r.ExitCode}.");
        return ToolResult.Ok(text.Length == 0 ? "(no output)" : Cap(text), okKind, title);
    }
}

[Tool]
public sealed class GitStatusTool : ITool
{
    public string Name => "git_status";
    public string Description => "Show the working tree status (current branch and changed/untracked files).";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object();

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, new[] { "status", "--porcelain=v1", "-b" }, ct)
            .ConfigureAwait(false);
        return GitShared.FromRun(r, "git status");
    }
}

[Tool]
public sealed class GitLogTool : ITool
{
    public string Name => "git_log";
    public string Description => "Show recent commits as a one-line-per-commit history.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("count", ToolSchema.Int("How many commits to show (default 20, max 100)."), false),
        ("path", ToolSchema.Str("Limit history to this file or directory (optional)."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var count = Math.Clamp(Args.Int(args, "count") ?? 20, 1, 100);
        var argv = new List<string> { "log", $"-n{count}", "--oneline", "--decorate" };

        var path = Args.Str(args, "path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!PathSandbox.TryResolve(ctx, path, out _, out var error))
                return ToolResult.Fail(error!);
            argv.Add("--");
            argv.Add(path);
        }

        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct).ConfigureAwait(false);
        return GitShared.FromRun(r, "git log");
    }
}

[Tool]
public sealed class GitDiffTool : ITool
{
    public string Name => "git_diff";
    public string Description => "Show unstaged changes, or staged changes when staged=true; optionally limited to a path.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("staged", ToolSchema.Bool("Diff the staged (index) changes instead of the working tree."), false),
        ("path", ToolSchema.Str("Limit the diff to this file or directory (optional)."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var argv = new List<string> { "diff" };
        if (Args.Bool(args, "staged")) argv.Add("--staged");

        var path = Args.Str(args, "path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!PathSandbox.TryResolve(ctx, path, out _, out var error))
                return ToolResult.Fail(error!);
            argv.Add("--");
            argv.Add(path);
        }

        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct).ConfigureAwait(false);
        return GitShared.FromRun(r, "git diff", ToolResultKind.Diff);
    }
}

[Tool]
public sealed class GitBranchTool : ITool
{
    public string Name => "git_branch";
    public string Description => "List branches (the current one marked with *).";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("all", ToolSchema.Bool("Include remote-tracking branches."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var argv = new List<string> { "branch" };
        if (Args.Bool(args, "all")) argv.Add("--all");
        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct).ConfigureAwait(false);
        return GitShared.FromRun(r, "git branch");
    }
}

[Tool]
public sealed class GitAddTool : ITool
{
    public string Name => "git_add";
    public string Description => "Stage changes for commit. Give a path, a list of paths, or set all=true to stage everything.";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("A single path to stage."), false),
        ("paths", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Several paths to stage." }, false),
        ("all", ToolSchema.Bool("Stage all changes under the working directory."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var argv = new List<string> { "add" };

        if (Args.Bool(args, "all"))
        {
            argv.Add("--");
            argv.Add(".");
        }
        else
        {
            var paths = CollectPaths(args);
            if (paths.Count == 0)
                return ToolResult.Fail("give a path, a paths list, or all=true.");

            argv.Add("--");
            foreach (var p in paths)
            {
                if (!PathSandbox.TryResolve(ctx, p, out _, out var error))
                    return ToolResult.Fail(error!);
                argv.Add(p);
            }
        }

        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct).ConfigureAwait(false);
        if (!r.Ok) return GitShared.FromRun(r, "git add");

        // git add is silent on success; confirm with a short status.
        var status = await GitRunner.RunAsync(ctx.WorkingDirectory, new[] { "status", "--short" }, ct).ConfigureAwait(false);
        return ToolResult.Ok(status.StdOut.Trim() is { Length: > 0 } s ? s : "staged.", ToolResultKind.Text, "git add");
    }

    internal static List<string> CollectPaths(JsonElement args)
    {
        var list = new List<string>();
        var single = Args.Str(args, "path");
        if (!string.IsNullOrWhiteSpace(single)) list.Add(single);

        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("paths", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } p)
                    list.Add(p);
        }
        return list;
    }
}

[Tool]
public sealed class GitCommitTool : ITool
{
    public string Name => "git_commit";
    public string Description => "Commit staged changes with a message. Set all=true to also stage tracked modifications first.";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("message", ToolSchema.Str("The commit message."), true),
        ("all", ToolSchema.Bool("Stage all tracked modifications before committing (git commit -a)."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var message = Args.Str(args, "message");
        if (string.IsNullOrWhiteSpace(message))
            return ToolResult.Fail("message is required.");

        var argv = new List<string> { "commit", "-m", message };
        if (Args.Bool(args, "all")) argv.Add("-a");

        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct).ConfigureAwait(false);
        return GitShared.FromRun(r, "git commit");
    }

    public async Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var all = Args.Bool(args, "all");
        var argv = all
            ? new[] { "diff", "HEAD", "--stat" }
            : new[] { "diff", "--staged", "--stat" };
        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct).ConfigureAwait(false);
        var summary = r.Ok && r.StdOut.Trim().Length > 0 ? r.StdOut.Trim() : "(nothing to commit)";
        return new ToolPreview($"Commit {(all ? "all tracked changes" : "staged changes")}:", summary);
    }
}

[Tool]
public sealed class GitRestoreTool : ITool
{
    public string Name => "git_restore";
    public string Description => "Discard uncommitted changes to a tracked file, restoring it to HEAD. Cannot be undone.";
    public PermissionTier Tier => PermissionTier.Destructive;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("The tracked file or directory to restore to HEAD."), true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("path is required.");
        if (!PathSandbox.TryResolve(ctx, path, out _, out var error))
            return ToolResult.Fail(error!);

        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, new[] { "checkout", "HEAD", "--", path }, ct)
            .ConfigureAwait(false);
        return r.Ok
            ? ToolResult.Ok($"restored {path} to HEAD.", ToolResultKind.Text, "git restore")
            : GitShared.FromRun(r, "git restore");
    }

    public async Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, new[] { "diff", "--", path }, ct).ConfigureAwait(false);
        var diff = r.Ok && r.StdOut.Trim().Length > 0 ? GitShared.Cap(r.StdOut) : "(no uncommitted changes)";
        return new ToolPreview($"Discard changes to {path} — this is lost:", diff);
    }
}

[Tool]
public sealed class GitPushTool : ITool
{
    public string Name => "git_push";
    public string Description => "Push commits to a remote (default: the current branch's upstream).";
    public PermissionTier Tier => PermissionTier.Network;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("remote", ToolSchema.Str("Remote name (default: origin / configured upstream)."), false),
        ("branch", ToolSchema.Str("Branch to push (default: the current branch)."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var argv = new List<string> { "push" };
        var remote = Args.Str(args, "remote");
        var branch = Args.Str(args, "branch");
        if (!string.IsNullOrWhiteSpace(remote))
        {
            argv.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch)) argv.Add(branch);
        }

        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct, timeoutMs: 120_000).ConfigureAwait(false);
        return GitShared.FromRun(r, "git push");
    }
}

[Tool]
public sealed class GitPullTool : ITool
{
    public string Name => "git_pull";
    public string Description => "Fetch and integrate changes from a remote into the current branch.";
    public PermissionTier Tier => PermissionTier.Network;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("remote", ToolSchema.Str("Remote name (default: the configured upstream)."), false),
        ("branch", ToolSchema.Str("Branch to pull (default: the tracked branch)."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var argv = new List<string> { "pull" };
        var remote = Args.Str(args, "remote");
        var branch = Args.Str(args, "branch");
        if (!string.IsNullOrWhiteSpace(remote))
        {
            argv.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch)) argv.Add(branch);
        }

        var r = await GitRunner.RunAsync(ctx.WorkingDirectory, argv, ct, timeoutMs: 120_000).ConfigureAwait(false);
        return GitShared.FromRun(r, "git pull");
    }
}
