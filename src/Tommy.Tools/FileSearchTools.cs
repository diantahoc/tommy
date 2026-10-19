using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Directory walking shared by the search/find tools. The flat
/// <see cref="Directory.EnumerateFiles(string,string,SearchOption)"/> cannot prune
/// subtrees, so a root-level walk descends into <c>node_modules</c>/<c>.git</c> and
/// (for content search) opens every file — on a real repo that looks like a freeze.
/// This walk prunes those directories by default and skips unreadable ones.
/// </summary>
internal static class WorkspaceWalk
{
    /// <summary>
    /// Dependency/build/VCS directories skipped by the default walk. They are pruned
    /// only when discovered during recursion — the walk root is always scanned, so
    /// pointing <c>path</c> straight at <c>node_modules</c> still works.
    /// </summary>
    public static readonly IReadOnlySet<string> HeavyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", ".vs", ".idea", "bin", "obj",
        "dist", "build", "out", ".next", ".nuxt", "target", "__pycache__",
        ".venv", "venv", ".tox", ".gradle", ".cache", ".angular"
    };

    public static IEnumerable<string> Files(string root, string glob, bool skipHeavy, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir, glob); }
            catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subs)
            {
                if (skipHeavy && HeavyDirs.Contains(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }
        }
    }
}

/// <summary>Find files by name/glob — replaces the shell-find antipattern.</summary>
[Tool]
public sealed class FindFileTool : ITool
{
    private const int MaxResults = 200;

    public string Name => "find_file";

    public string Description =>
        "Find files by name or glob pattern (e.g. *.cs) under the workspace. " +
        "Skips dependency/build folders (node_modules, .git, .vs, bin, obj, dist, etc.); " +
        "point 'path' straight at one to look inside it.";

    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("pattern", ToolSchema.Str("Filename glob, e.g. *.cs or Program.cs."), true),
        ("path", ToolSchema.Str("Subdirectory to search within (default: root)."), false));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var pattern = Args.Str(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(ToolResult.Fail("pattern is required."));

        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path") ?? ".", out var dir, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!Directory.Exists(dir))
            return Task.FromResult(ToolResult.Fail("search directory not found."));

        var matches = new List<string>();
        try
        {
            foreach (var file in WorkspaceWalk.Files(dir, pattern, skipHeavy: true, ct))
            {
                matches.Add(PathSandbox.Relative(ctx, file));
                if (matches.Count >= MaxResults) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"search failed: {ex.Message}"));
        }

        var text = matches.Count == 0 ? "(no matches)" : string.Join('\n', matches);
        return Task.FromResult(ToolResult.Ok(text, ToolResultKind.Text, $"find {pattern}"));
    }
}

/// <summary>Search file contents by regular expression (skips dependency/build dirs).</summary>
[Tool]
public sealed class SearchTool : ITool
{
    public string Name => "search";

    public string Description =>
        "Search file contents by regular expression, returning file:line matches. " +
        "Skips dependency/build folders (node_modules, .git, .vs, bin, obj, dist, etc.) " +
        "and binary files — use search_all to include dependency/build folders.";

    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => SearchCore.Schema;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(SearchCore.Run(args, ctx, skipHeavy: true, "search", ct));
}

/// <summary>Search file contents everywhere, including dependency/build dirs.</summary>
[Tool]
public sealed class SearchAllTool : ITool
{
    public string Name => "search_all";

    public string Description =>
        "Like search, but scans every folder INCLUDING dependency/build dirs " +
        "(node_modules, .git, bin, obj, dist). Slower and noisier; prefer 'search' " +
        "unless you specifically need those folders. Stops with a note if it runs long.";

    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => SearchCore.Schema;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(SearchCore.Run(args, ctx, skipHeavy: false, "search_all", ct));
}

/// <summary>Shared regex-search implementation with a time/file budget so it can never hang.</summary>
internal static class SearchCore
{
    private const int MaxResults = 100;
    private const long MaxFileBytes = 1024 * 1024;
    private const int BinarySniffBytes = 8192;
    private const int MaxFilesScanned = 50_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    public static JsonObject Schema => ToolSchema.Object(
        ("pattern", ToolSchema.Str("Regular expression to search for."), true),
        ("path", ToolSchema.Str("Subdirectory to search within (default: root)."), false),
        ("glob", ToolSchema.Str("Filename glob filter, e.g. *.cs (default: all files)."), false));

    public static ToolResult Run(JsonElement args, ToolContext ctx, bool skipHeavy, string toolName, CancellationToken ct)
    {
        var pattern = Args.Str(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Fail("pattern is required.");

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail($"invalid regex: {ex.Message}");
        }

        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path") ?? ".", out var dir, out var error))
            return ToolResult.Fail(error!);
        if (!Directory.Exists(dir))
            return ToolResult.Fail("search directory not found.");

        var glob = Args.Str(args, "glob") ?? "*";
        var results = new StringBuilder();
        var hits = 0;
        var scanned = 0;
        var budgetHit = false;
        var sw = Stopwatch.StartNew();

        try
        {
            foreach (var file in WorkspaceWalk.Files(dir, glob, skipHeavy, ct))
            {
                ct.ThrowIfCancellationRequested();
                // Self-terminate before a huge tree (e.g. node_modules under search_all)
                // turns into a freeze: there is no user-cancel path during a tool call.
                if (++scanned > MaxFilesScanned || sw.Elapsed > Budget) { budgetHit = true; break; }

                if (new FileInfo(file).Length > MaxFileBytes) continue;
                // Skip binaries: matching inside a .dll/.exe returns garbage bytes
                // that corrupt the tool result (and break the model's JSON).
                if (LooksBinary(file)) continue;

                var rel = PathSandbox.Relative(ctx, file);
                var lineNo = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (!regex.IsMatch(line)) continue;

                    results.Append(rel).Append(':').Append(lineNo).Append(": ")
                           .AppendLine(Clean(line.Trim()));
                    if (++hits >= MaxResults)
                    {
                        results.AppendLine($"… (stopped at {MaxResults} matches)");
                        return ToolResult.Ok(results.ToString(), ToolResultKind.Text, $"{toolName} {pattern}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolResult.Fail($"search failed: {ex.Message}");
        }

        if (budgetHit)
        {
            var hint = skipHeavy
                ? "narrow with 'path' or 'glob'"
                : "narrow with 'path'/'glob', or use 'search' to skip dependency folders";
            results.AppendLine($"… (stopped after {scanned} files / {sw.Elapsed.TotalSeconds:0}s — {hint}).");
            return ToolResult.Ok(results.ToString(), ToolResultKind.Text, $"{toolName} {pattern}");
        }

        var text = hits == 0 ? "(no matches)" : results.ToString();
        return ToolResult.Ok(text, ToolResultKind.Text, $"{toolName} {pattern}");
    }

    /// <summary>A file is treated as binary if its first chunk contains a NUL byte.</summary>
    private static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[BinarySniffBytes];
            var read = fs.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch
        {
            return true; // unreadable → skip rather than risk garbage
        }
    }

    /// <summary>Strip stray control characters from a matched line (keep tabs).</summary>
    private static string Clean(string line)
    {
        if (!line.Any(c => c < ' ' && c != '\t')) return line;
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
            if (c >= ' ' || c == '\t') sb.Append(c);
        return sb.ToString();
    }
}
