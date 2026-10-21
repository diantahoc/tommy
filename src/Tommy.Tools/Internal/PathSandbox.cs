using Tommy.Core.Tools;

namespace Tommy.Tools.Internal;

/// <summary>
/// Resolves a tool's relative path against the working directory and refuses any
/// path that escapes it — the sandbox every file tool runs inside.
/// </summary>
internal static class PathSandbox
{
    public static bool TryResolve(ToolContext ctx, string? relative, out string fullPath, out string? error) =>
        TryResolveUnder(ctx, Path.GetFullPath(ctx.WorkingDirectory), relative, out fullPath, out error);

    /// <summary>
    /// Resolves <paramref name="relative"/> against <paramref name="baseDir"/> (e.g. a sub-repo
    /// inside the workspace) while still refusing anything that escapes the workspace root.
    /// </summary>
    public static bool TryResolveUnder(ToolContext ctx, string baseDir, string? relative, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relative))
        {
            error = "path is required.";
            return false;
        }

        var root = Path.GetFullPath(ctx.WorkingDirectory);
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(baseDir, relative));
        }
        catch (Exception ex)
        {
            error = $"invalid path: {ex.Message}";
            return false;
        }

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var isInside = string.Equals(combined, root, StringComparison.OrdinalIgnoreCase)
            || (combined + Path.DirectorySeparatorChar).StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);

        if (!isInside)
        {
            error = $"path escapes the working directory: {relative}";
            return false;
        }

        fullPath = combined;
        return true;
    }

    /// <summary>Path relative to the working directory, for display.</summary>
    public static string Relative(ToolContext ctx, string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(ctx.WorkingDirectory), fullPath).Replace('\\', '/');
}
