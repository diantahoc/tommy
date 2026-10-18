using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Surgical edit: replace an exact block of text with new text. The block must
/// occur exactly once, so an edit touches only what was intended — never a
/// whole-file rewrite. Shows a diff before writing.
/// </summary>
[Tool]
public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public string Description =>
        "Replace an exact block of text in a file with new text. The old_text must occur exactly once; add surrounding context to make it unique.";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true),
        ("old_text", ToolSchema.Str("The exact existing text block to replace (must be unique)."), true),
        ("new_text", ToolSchema.Str("The replacement text."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        var oldText = Args.Str(args, "old_text") ?? string.Empty;
        var newText = Args.Str(args, "new_text") ?? string.Empty;

        if (!PathSandbox.TryResolve(ctx, rel, out var path, out _) || !File.Exists(path))
            return Task.FromResult<ToolPreview?>(new ToolPreview($"Edit {rel}", Diff.Block(oldText, newText)));

        var content = File.ReadAllText(path);
        var count = Occurrences(content, oldText);
        var note = count switch
        {
            0 => " (old_text not found — will fail)",
            1 => "",
            _ => $" (old_text matches {count}× — will fail; not unique)"
        };
        return Task.FromResult<ToolPreview?>(new ToolPreview($"Edit {rel}{note}", Diff.Block(oldText, newText)));
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"file not found: {Args.Str(args, "path")}"));

        var oldText = Args.Str(args, "old_text") ?? string.Empty;
        var newText = Args.Str(args, "new_text") ?? string.Empty;
        if (oldText.Length == 0)
            return Task.FromResult(ToolResult.Fail("old_text is required."));

        var content = File.ReadAllText(path);
        var count = Occurrences(content, oldText);
        if (count == 0)
            return Task.FromResult(ToolResult.Fail("old_text not found in file."));
        if (count > 1)
            return Task.FromResult(ToolResult.Fail($"old_text is not unique ({count} matches); add more surrounding context."));

        File.WriteAllText(path, content.Replace(oldText, newText));
        var rel = PathSandbox.Relative(ctx, path);
        return Task.FromResult(ToolResult.Ok(
            $"Edited {rel}.\n{Diff.Block(oldText, newText)}", ToolResultKind.Diff, rel));
    }

    private static int Occurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}

/// <summary>Create a new file with the given content (fails if it exists).</summary>
[Tool]
public sealed class CreateFileTool : ITool
{
    public string Name => "create_file";
    public string Description => "Create a new file with the given content. Fails if the file already exists.";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("New file path relative to the working directory."), true),
        ("content", ToolSchema.Str("File content."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        var content = Args.Str(args, "content") ?? string.Empty;
        return Task.FromResult<ToolPreview?>(new ToolPreview($"Create {rel}", Diff.Block(null, content)));
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (File.Exists(path))
            return Task.FromResult(ToolResult.Fail("file already exists; use edit_file."));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Args.Str(args, "content") ?? string.Empty);

        var rel = PathSandbox.Relative(ctx, path);
        return Task.FromResult(ToolResult.Ok($"Created {rel}.", ToolResultKind.Text, rel));
    }
}

/// <summary>Create a directory (and any missing parents).</summary>
[Tool]
public sealed class CreateDirTool : ITool
{
    public string Name => "create_dir";
    public string Description => "Create a directory (including any missing parent directories).";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Directory path relative to the working directory."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));

        Directory.CreateDirectory(path);
        return Task.FromResult(ToolResult.Ok($"Created directory {PathSandbox.Relative(ctx, path)}."));
    }
}

/// <summary>Delete a file by moving it to the workspace trash (soft delete).</summary>
[Tool]
public sealed class DeleteFileTool : ITool
{
    private const string TrashDir = ".tommy-trash";

    public string Name => "delete_file";
    public string Description => "Delete a file by moving it to the workspace trash (recoverable).";
    public PermissionTier Tier => PermissionTier.Destructive;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult<ToolPreview?>(new ToolPreview($"Move {Args.Str(args, "path")} to trash ({TrashDir}/)"));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"file not found: {Args.Str(args, "path")}"));

        var trash = Path.Combine(Path.GetFullPath(ctx.WorkingDirectory), TrashDir);
        Directory.CreateDirectory(trash);
        var dest = Path.Combine(trash, $"{Path.GetFileName(path)}.{DateTime.UtcNow:yyyyMMddHHmmss}");
        File.Move(path, dest, overwrite: true);

        return Task.FromResult(ToolResult.Ok($"Moved {PathSandbox.Relative(ctx, path)} to {TrashDir}/."));
    }
}
