using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>Find files by name/glob — replaces the shell-find antipattern.</summary>
[Tool]
public sealed class FindFileTool : ITool
{
    private const int MaxResults = 200;

    public string Name => "find_file";
    public string Description => "Find files by name or glob pattern (e.g. *.cs) under the workspace.";
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
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                matches.Add(PathSandbox.Relative(ctx, file));
                if (matches.Count >= MaxResults) break;
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"search failed: {ex.Message}"));
        }

        var text = matches.Count == 0 ? "(no matches)" : string.Join('\n', matches);
        return Task.FromResult(ToolResult.Ok(text, ToolResultKind.Text, $"find {pattern}"));
    }
}

/// <summary>Search file contents by regular expression.</summary>
[Tool]
public sealed class SearchTool : ITool
{
    private const int MaxResults = 100;
    private const long MaxFileBytes = 1024 * 1024;

    public string Name => "search";
    public string Description => "Search file contents by regular expression, returning file:line matches.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("pattern", ToolSchema.Str("Regular expression to search for."), true),
        ("path", ToolSchema.Str("Subdirectory to search within (default: root)."), false),
        ("glob", ToolSchema.Str("Filename glob filter, e.g. *.cs (default: all files)."), false));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var pattern = Args.Str(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(ToolResult.Fail("pattern is required."));

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail($"invalid regex: {ex.Message}"));
        }

        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path") ?? ".", out var dir, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!Directory.Exists(dir))
            return Task.FromResult(ToolResult.Fail("search directory not found."));

        var glob = Args.Str(args, "glob") ?? "*";
        var results = new StringBuilder();
        var hits = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, glob, SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(file).Length > MaxFileBytes) continue;

                var rel = PathSandbox.Relative(ctx, file);
                var lineNo = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (!regex.IsMatch(line)) continue;

                    results.Append(rel).Append(':').Append(lineNo).Append(": ")
                           .AppendLine(line.Trim());
                    if (++hits >= MaxResults)
                    {
                        results.AppendLine($"… (stopped at {MaxResults} matches)");
                        return Task.FromResult(ToolResult.Ok(results.ToString(), ToolResultKind.Text, $"search {pattern}"));
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"search failed: {ex.Message}"));
        }

        var text = hits == 0 ? "(no matches)" : results.ToString();
        return Task.FromResult(ToolResult.Ok(text, ToolResultKind.Text, $"search {pattern}"));
    }
}
