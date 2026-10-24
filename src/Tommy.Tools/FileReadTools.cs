using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>Read the full text of a file (size-capped).</summary>
[Tool]
public sealed class ReadFileTool : ITool
{
    private const int MaxBytes = 256 * 1024;

    public string Name => "read_file";
    public string Description => "Read the full text content of a file in the workspace.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!PathSandbox.TryResolveReadable(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"file not found: {Args.Str(args, "path")}"));

        var info = new FileInfo(path);
        var text = File.ReadAllText(path);
        if (info.Length > MaxBytes)
            text = text[..Math.Min(text.Length, MaxBytes)] + $"\n… (truncated at {MaxBytes} bytes; use read_file_range)";

        return Task.FromResult(ToolResult.Ok(text, ToolResultKind.Text, PathSandbox.Relative(ctx, path)));
    }
}

/// <summary>Read a 1-based inclusive line range of a file.</summary>
[Tool]
public sealed class ReadRangeTool : ITool
{
    public string Name => "read_file_range";
    public string Description => "Read a specific inclusive line range (1-based) from a file.";
    public PermissionTier Tier => PermissionTier.Read;
    public IReadOnlyList<string> Aliases => new[] { "read_range", "read_lines" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true),
        ("start_line", ToolSchema.Int("First line to read (1-based)."), true),
        ("end_line", ToolSchema.Int("Last line to read (inclusive)."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!PathSandbox.TryResolveReadable(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"file not found: {Args.Str(args, "path")}"));

        var start = Math.Max(1, Args.Int(args, "start_line") ?? 1);
        var end = Math.Max(start, Args.Int(args, "end_line") ?? start);

        var lines = File.ReadAllLines(path);
        if (start > lines.Length)
            return Task.FromResult(ToolResult.Fail($"start_line {start} is past end of file ({lines.Length} lines)."));

        end = Math.Min(end, lines.Length);
        var sb = new StringBuilder();
        for (var i = start; i <= end; i++)
            sb.Append(i).Append(": ").AppendLine(lines[i - 1]);

        return Task.FromResult(ToolResult.Ok(sb.ToString(), ToolResultKind.Text,
            $"{PathSandbox.Relative(ctx, path)}:{start}-{end}"));
    }
}

/// <summary>Count the lines in a file.</summary>
[Tool]
public sealed class LineCountTool : ITool
{
    public string Name => "line_count";
    public string Description => "Count the number of lines in a file.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!PathSandbox.TryResolveReadable(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"file not found: {Args.Str(args, "path")}"));

        var count = File.ReadLines(path).Count();
        return Task.FromResult(ToolResult.Ok(count.ToString()));
    }
}

/// <summary>List the entries of a directory.</summary>
[Tool]
public sealed class ListDirTool : ITool
{
    public string Name => "list_dir";
    public string Description => "List files and subdirectories of a directory in the workspace.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Directory path relative to the working directory (default: root)."), false));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path") ?? ".";
        if (!PathSandbox.TryResolveReadable(ctx, rel, out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Fail($"directory not found: {rel}"));

        var sb = new StringBuilder();
        foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d))
            sb.Append(Path.GetFileName(dir)).AppendLine("/");
        foreach (var file in Directory.EnumerateFiles(path).OrderBy(f => f))
            sb.Append(Path.GetFileName(file)).Append("  (").Append(new FileInfo(file).Length).AppendLine(" bytes)");

        var text = sb.Length == 0 ? "(empty)" : sb.ToString();
        return Task.FromResult(ToolResult.Ok(text, ToolResultKind.Text, PathSandbox.Relative(ctx, path) + "/"));
    }
}
