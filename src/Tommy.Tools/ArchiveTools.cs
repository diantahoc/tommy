using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

internal enum ArchiveFormat { Zip, Tar, TarGz, Unknown }

internal static class ArchiveSupport
{
    public const int MaxEntries = 2000;

    public static ArchiveFormat Detect(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.EndsWith(".zip")) return ArchiveFormat.Zip;
        if (p.EndsWith(".tar.gz") || p.EndsWith(".tgz")) return ArchiveFormat.TarGz;
        if (p.EndsWith(".tar")) return ArchiveFormat.Tar;
        return ArchiveFormat.Unknown;
    }

    /// <summary>Combine a destination root with an archive entry name, refusing any
    /// path that escapes the root (zip-slip) — extracted archives are untrusted.</summary>
    public static bool SafeJoin(string destRoot, string entryName, out string fullPath)
    {
        fullPath = string.Empty;
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        var root = Path.GetFullPath(destRoot);
        var combined = Path.GetFullPath(Path.Combine(root, normalized));
        var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!combined.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !(combined + Path.DirectorySeparatorChar).StartsWith(rootSep, StringComparison.OrdinalIgnoreCase))
            return false;
        fullPath = combined;
        return true;
    }
}

/// <summary>List the entries of a .zip, .tar, or .tar.gz archive without extracting.</summary>
[Tool]
public sealed class ArchiveListTool : ITool
{
    public string Name => "archive_list";
    public string Description => "List the contents of a .zip, .tar, or .tar.gz archive without extracting it.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Path to the archive file."), true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return ToolResult.Fail("path is required.");
        if (!PathSandbox.TryResolve(ctx, rel, out var archive, out var error))
            return ToolResult.Fail(error!);
        if (!File.Exists(archive))
            return ToolResult.Fail("archive not found.");

        var format = ArchiveSupport.Detect(archive);
        if (format == ArchiveFormat.Unknown)
            return ToolResult.Fail("unsupported archive type (expected .zip, .tar, .tar.gz, or .tgz).");

        var sb = new StringBuilder();
        var count = 0;
        try
        {
            if (format == ArchiveFormat.Zip)
            {
                using var zip = ZipFile.OpenRead(archive);
                foreach (var entry in zip.Entries)
                {
                    sb.Append(entry.FullName);
                    if (!entry.FullName.EndsWith('/')) sb.Append("  (").Append(entry.Length).Append(" bytes)");
                    sb.AppendLine();
                    if (++count >= ArchiveSupport.MaxEntries) { sb.AppendLine("… (more entries)"); break; }
                }
            }
            else
            {
                await using var stream = OpenTar(archive, format);
                using var reader = new TarReader(stream);
                while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
                {
                    sb.Append(entry.Name);
                    if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                        sb.Append("  (").Append(entry.Length).Append(" bytes)");
                    sb.AppendLine();
                    if (++count >= ArchiveSupport.MaxEntries) { sb.AppendLine("… (more entries)"); break; }
                }
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"could not read archive: {ex.Message}");
        }

        return ToolResult.Ok(count == 0 ? "(empty archive)" : sb.ToString().TrimEnd(), ToolResultKind.Text, $"{count} entries");
    }

    private static Stream OpenTar(string path, ArchiveFormat format)
    {
        var file = File.OpenRead(path);
        return format == ArchiveFormat.TarGz ? new GZipStream(file, CompressionMode.Decompress) : file;
    }
}

/// <summary>Extract a .zip, .tar, or .tar.gz archive into a destination directory.</summary>
[Tool]
public sealed class ArchiveExtractTool : ITool
{
    public string Name => "archive_extract";
    public string Description => "Extract a .zip, .tar, or .tar.gz archive into a destination directory (created if needed).";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Path to the archive file."), true),
        ("dest", ToolSchema.Str("Destination directory (default: a folder named after the archive)."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return ToolResult.Fail("path is required.");
        if (!PathSandbox.TryResolve(ctx, rel, out var archive, out var error))
            return ToolResult.Fail(error!);
        if (!File.Exists(archive))
            return ToolResult.Fail("archive not found.");

        var format = ArchiveSupport.Detect(archive);
        if (format == ArchiveFormat.Unknown)
            return ToolResult.Fail("unsupported archive type (expected .zip, .tar, .tar.gz, or .tgz).");

        var destRel = Args.Str(args, "dest");
        if (string.IsNullOrWhiteSpace(destRel))
        {
            var name = Path.GetFileName(archive);
            foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar", ".zip" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
            destRel = name;
        }
        if (!PathSandbox.TryResolve(ctx, destRel, out var dest, out var destError))
            return ToolResult.Fail(destError!);

        try
        {
            Directory.CreateDirectory(dest);
            var extracted = format == ArchiveFormat.Zip
                ? ExtractZip(archive, dest)
                : await ExtractTarAsync(archive, dest, format, ct).ConfigureAwait(false);
            return ToolResult.Ok($"extracted {extracted} file(s) to {PathSandbox.Relative(ctx, dest)}/.", ToolResultKind.Text, "archive extract");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message); // zip-slip refusal
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"extraction failed: {ex.Message}");
        }
    }

    private static int ExtractZip(string archive, string dest)
    {
        using var zip = ZipFile.OpenRead(archive);
        var files = 0;
        foreach (var entry in zip.Entries)
        {
            if (!ArchiveSupport.SafeJoin(dest, entry.FullName, out var target))
                throw new InvalidOperationException($"refused: entry escapes the destination ({entry.FullName}).");

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            files++;
        }
        return files;
    }

    private static async Task<int> ExtractTarAsync(string archive, string dest, ArchiveFormat format, CancellationToken ct)
    {
        await using var file = File.OpenRead(archive);
        await using Stream stream = format == ArchiveFormat.TarGz
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new TarReader(stream);

        var files = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            if (!ArchiveSupport.SafeJoin(dest, entry.Name, out var target))
                throw new InvalidOperationException($"refused: entry escapes the destination ({entry.Name}).");

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue; // skip symlinks/devices — untrusted archive hygiene

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await entry.ExtractToFileAsync(target, overwrite: true, ct).ConfigureAwait(false);
            files++;
        }
        return files;
    }
}
