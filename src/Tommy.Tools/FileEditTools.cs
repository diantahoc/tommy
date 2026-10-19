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
        "Replace an exact block of text in a file with new text. The old_text must occur once; line endings are handled for you (use \\n), and whitespace is matched leniently. To add new content, use insert_file; to replace by line number, use replace_lines.";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true),
        ("old_text", ToolSchema.Str("The existing text block to replace (use \\n for newlines; must be unique)."), true),
        ("new_text", ToolSchema.Str("The replacement text."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        var oldText = TextEdit.ToLf(Args.Str(args, "old_text") ?? string.Empty);
        var newText = TextEdit.ToLf(Args.Str(args, "new_text") ?? string.Empty);

        if (!PathSandbox.TryResolve(ctx, rel, out var path, out _) || !File.Exists(path))
            return Task.FromResult<ToolPreview?>(new ToolPreview($"Edit {rel}", Diff.Block(oldText, newText)));

        var content = TextEdit.ToLf(File.ReadAllText(path));
        var kind = TextEdit.FindBlock(content, oldText, out _, out _, out var occ);
        var note = (kind, occ) switch
        {
            (_, 0) => " (old_text not found — will fail)",
            (_, > 1) => $" (matches {occ}× — will fail; not unique)",
            (TextEdit.MatchKind.Exact, _) => "",
            _ => " (whitespace-tolerant match)"
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

        var oldText = TextEdit.ToLf(Args.Str(args, "old_text") ?? string.Empty);
        var newText = TextEdit.ToLf(Args.Str(args, "new_text") ?? string.Empty);
        if (oldText.Length == 0)
            return Task.FromResult(ToolResult.Fail("old_text is required."));

        var raw = File.ReadAllText(path);
        var eol = TextEdit.DetectEol(raw);
        var content = TextEdit.ToLf(raw);
        var rel = PathSandbox.Relative(ctx, path);

        // Exact (LF-normalized) match handles mid-line edits via a substring replace.
        var exact = TextEdit.Occurrences(content, oldText);
        if (exact > 1)
            return Task.FromResult(ToolResult.Fail($"old_text is not unique ({exact} matches); add more surrounding context."));
        if (exact == 1)
        {
            File.WriteAllText(path, TextEdit.ApplyEol(content.Replace(oldText, newText), eol));
            return Task.FromResult(ToolResult.Ok($"Edited {rel}.\n{Diff.Block(oldText, newText)}", ToolResultKind.Diff, rel));
        }

        // Whitespace-tolerant line-based fallback (trailing, then full trim with re-indent).
        var kind = TextEdit.FindBlock(content, oldText, out var start, out var len, out var occ);
        if (occ > 1)
            return Task.FromResult(ToolResult.Fail($"old_text matches {occ} places (ignoring whitespace); add more surrounding context."));
        if (kind is TextEdit.MatchKind.None || occ == 0)
            return Task.FromResult(ToolResult.Fail($"old_text not found in file.{TextEdit.NearestHint(content, oldText)}"));

        var fileLines = content.Split('\n').ToList();
        var newLines = TextEdit.SplitLines(newText);
        if (kind == TextEdit.MatchKind.Trimmed && len > 0)
        {
            var oldFirstIndent = TextEdit.LeadingWs(TextEdit.SplitLines(oldText)[0]);
            newLines = TextEdit.Reindent(newLines, oldFirstIndent, TextEdit.LeadingWs(fileLines[start]));
        }
        fileLines.RemoveRange(start, len);
        fileLines.InsertRange(start, newLines);

        File.WriteAllText(path, TextEdit.ApplyEol(string.Join('\n', fileLines), eol));
        return Task.FromResult(ToolResult.Ok(
            $"Edited {rel} (matched ignoring whitespace).\n{Diff.Block(oldText, newText)}", ToolResultKind.Diff, rel));
    }
}

/// <summary>Insert text at a 1-based line (or append), preserving the file's line endings.</summary>
[Tool]
public sealed class InsertFileTool : ITool
{
    public string Name => "insert_file";
    public string Description =>
        "Insert text into a file before a given 1-based line (same numbering as read_file_range), or append at the end when no line is given. No exact-match needed — the right tool for adding new content. Use \\n for newlines.";
    public PermissionTier Tier => PermissionTier.Write;
    public IReadOnlyList<string> Aliases { get; } = new[] { "insert", "insert_lines" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true),
        ("text", ToolSchema.Str("Text to insert (use \\n for newlines)."), true),
        ("line", ToolSchema.Int("1-based line to insert BEFORE (omit to append at the end)."), false));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var where = Args.Int(args, "line") is { } l ? $"before line {l}" : "at end";
        return Task.FromResult<ToolPreview?>(new ToolPreview(
            $"Insert into {Args.Str(args, "path")} ({where})", Diff.Block(null, TextEdit.ToLf(Args.Str(args, "text") ?? string.Empty))));
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"file not found: {Args.Str(args, "path")}"));

        var text = Args.Str(args, "text");
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(ToolResult.Fail("text is required."));

        var raw = File.ReadAllText(path);
        var eol = TextEdit.DetectEol(raw);
        var hadFinalNewline = TextEdit.ToLf(raw).EndsWith('\n');
        var fileLines = File.ReadAllLines(path).ToList();
        var textLines = TextEdit.SplitLines(TextEdit.ToLf(text));

        var idx = Args.Int(args, "line") is { } line ? Math.Clamp(line - 1, 0, fileLines.Count) : fileLines.Count;
        fileLines.InsertRange(idx, textLines);

        var outLf = string.Join('\n', fileLines) + (hadFinalNewline ? "\n" : string.Empty);
        File.WriteAllText(path, TextEdit.ApplyEol(outLf, eol));

        var rel = PathSandbox.Relative(ctx, path);
        return Task.FromResult(ToolResult.Ok(
            $"Inserted {textLines.Length} line(s) into {rel}.\n{Diff.Block(null, string.Join('\n', textLines))}", ToolResultKind.Diff, rel));
    }
}

/// <summary>Replace a 1-based inclusive line range with new text, preserving line endings.</summary>
[Tool]
public sealed class ReplaceLinesTool : ITool
{
    public string Name => "replace_lines";
    public string Description =>
        "Replace a 1-based inclusive line range (same numbering as read_file_range) with new text. Use when you know the line numbers; no exact-match needed. Use \\n for newlines.";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File path relative to the working directory."), true),
        ("start_line", ToolSchema.Int("First line to replace (1-based)."), true),
        ("end_line", ToolSchema.Int("Last line to replace (inclusive)."), true),
        ("new_text", ToolSchema.Str("Replacement text (use \\n for newlines)."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var s = Args.Int(args, "start_line"); var e = Args.Int(args, "end_line");
        var newText = TextEdit.ToLf(Args.Str(args, "new_text") ?? string.Empty);
        string? old = null;
        if (PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out _) && File.Exists(path) && s is { } ss && e is { } ee)
        {
            var lines = File.ReadAllLines(path);
            var from = Math.Clamp(ss, 1, lines.Length);
            var to = Math.Clamp(ee, from, lines.Length);
            old = string.Join('\n', lines[(from - 1)..to]);
        }
        return Task.FromResult<ToolPreview?>(new ToolPreview($"Replace lines {s}-{e} in {Args.Str(args, "path")}", Diff.Block(old, newText)));
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"file not found: {Args.Str(args, "path")}"));

        var startArg = Args.Int(args, "start_line");
        var endArg = Args.Int(args, "end_line");
        if (startArg is null || endArg is null)
            return Task.FromResult(ToolResult.Fail("start_line and end_line are required."));
        var newText = Args.Str(args, "new_text");
        if (newText is null)
            return Task.FromResult(ToolResult.Fail("new_text is required."));

        var raw = File.ReadAllText(path);
        var eol = TextEdit.DetectEol(raw);
        var hadFinalNewline = TextEdit.ToLf(raw).EndsWith('\n');
        var fileLines = File.ReadAllLines(path).ToList();

        var start = startArg.Value;
        if (start < 1 || start > fileLines.Count)
            return Task.FromResult(ToolResult.Fail($"start_line {start} is out of range (file has {fileLines.Count} lines)."));
        var end = Math.Clamp(endArg.Value, start, fileLines.Count);

        var removed = string.Join('\n', fileLines.GetRange(start - 1, end - start + 1));
        var newLines = TextEdit.SplitLines(TextEdit.ToLf(newText));
        fileLines.RemoveRange(start - 1, end - start + 1);
        fileLines.InsertRange(start - 1, newLines);

        var outLf = string.Join('\n', fileLines) + (hadFinalNewline ? "\n" : string.Empty);
        File.WriteAllText(path, TextEdit.ApplyEol(outLf, eol));

        var rel = PathSandbox.Relative(ctx, path);
        return Task.FromResult(ToolResult.Ok(
            $"Replaced lines {start}-{end} in {rel}.\n{Diff.Block(removed, string.Join('\n', newLines))}", ToolResultKind.Diff, rel));
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

/// <summary>Move or rename a file or directory within the workspace.</summary>
[Tool]
public sealed class RenameTool : ITool
{
    public string Name => "rename";
    public string Description => "Move or rename a file or directory within the workspace. Fails if the destination already exists.";
    public PermissionTier Tier => PermissionTier.Write;
    public IReadOnlyList<string> Aliases { get; } = new[] { "move" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Existing file or directory path, relative to the working directory."), true),
        ("to", ToolSchema.Str("New path, relative to the working directory."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult<ToolPreview?>(new ToolPreview($"Rename {Args.Str(args, "path")} → {Args.Str(args, "to")}"));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var src, out var srcError))
            return Task.FromResult(ToolResult.Fail(srcError!));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "to"), out var dest, out var destError))
            return Task.FromResult(ToolResult.Fail(destError!));

        var isDir = Directory.Exists(src);
        if (!isDir && !File.Exists(src))
            return Task.FromResult(ToolResult.Fail($"not found: {Args.Str(args, "path")}"));
        if (File.Exists(dest) || Directory.Exists(dest))
            return Task.FromResult(ToolResult.Fail($"destination already exists: {Args.Str(args, "to")}"));

        var destParent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destParent)) Directory.CreateDirectory(destParent);

        if (isDir) Directory.Move(src, dest);
        else File.Move(src, dest);

        return Task.FromResult(ToolResult.Ok(
            $"Renamed {PathSandbox.Relative(ctx, src)} → {PathSandbox.Relative(ctx, dest)}."));
    }
}

/// <summary>Report whether a path is a file or directory, with size and modified time.</summary>
[Tool]
public sealed class FileInfoTool : ITool
{
    public string Name => "file_info";
    public string Description => "Report whether a path is a file or directory, its size, and when it was last modified.";
    public PermissionTier Tier => PermissionTier.Read;
    public IReadOnlyList<string> Aliases { get; } = new[] { "stat", "dir_info" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("File or directory path, relative to the working directory."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));

        var rel = PathSandbox.Relative(ctx, path);
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            var files = Directory.EnumerateFiles(path).Count();
            var dirs = Directory.EnumerateDirectories(path).Count();
            return Task.FromResult(ToolResult.Ok(
                $"{rel}/ — directory, {files} file(s), {dirs} subdir(s), modified {di.LastWriteTimeUtc:u}.",
                ToolResultKind.Text, rel + "/"));
        }
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return Task.FromResult(ToolResult.Ok(
                $"{rel} — file, {fi.Length} bytes, modified {fi.LastWriteTimeUtc:u}.",
                ToolResultKind.Text, rel));
        }
        return Task.FromResult(ToolResult.Fail($"not found: {Args.Str(args, "path")}"));
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

/// <summary>Delete a directory (and its contents) by moving it to the workspace trash.</summary>
[Tool]
public sealed class DeleteDirTool : ITool
{
    private const string TrashDir = ".tommy-trash";

    public string Name => "delete_dir";
    public string Description => "Delete a directory and everything in it by moving it to the workspace trash (recoverable).";
    public PermissionTier Tier => PermissionTier.Destructive;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Directory path relative to the working directory."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        var note = PathSandbox.TryResolve(ctx, rel, out var p, out _) && Directory.Exists(p)
            ? $" ({Directory.EnumerateFileSystemEntries(p, "*", SearchOption.AllDirectories).Count()} entr(ies))"
            : "";
        return Task.FromResult<ToolPreview?>(new ToolPreview($"Move directory {rel}{note} to trash ({TrashDir}/)"));
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return Task.FromResult(ToolResult.Fail("working directory is read-only."));
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Fail($"directory not found: {Args.Str(args, "path")}"));

        var root = Path.GetFullPath(ctx.WorkingDirectory);
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Fail("refusing to delete the working directory itself."));

        var trash = Path.Combine(root, TrashDir);
        // Don't let the trash folder trash itself.
        if ((path + Path.DirectorySeparatorChar).StartsWith(trash + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Fail("that path is already in the trash."));

        Directory.CreateDirectory(trash);
        var dest = Path.Combine(trash, $"{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}.{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.Move(path, dest);

        return Task.FromResult(ToolResult.Ok($"Moved directory {PathSandbox.Relative(ctx, path)}/ to {TrashDir}/."));
    }
}
