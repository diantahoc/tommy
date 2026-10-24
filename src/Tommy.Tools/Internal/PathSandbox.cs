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

    /// <summary>
    /// Like <see cref="TryResolve"/>, but for read-only tools: first the workspace,
    /// then an absolute path that falls under one of <see cref="ToolContext.ReadRoots"/>
    /// (trusted installed content such as the skills directory). Writes never use this.
    /// </summary>
    public static bool TryResolveReadable(ToolContext ctx, string? pathArg, out string fullPath, out string? error)
    {
        if (TryResolve(ctx, pathArg, out fullPath, out error))
            return true;

        if (ctx.ReadRoots is { Count: > 0 } && !string.IsNullOrWhiteSpace(pathArg))
        {
            string combined;
            try { combined = Path.GetFullPath(pathArg); }
            catch { return false; } // keep the workspace-escape error

            foreach (var r in ctx.ReadRoots)
            {
                var root = Path.GetFullPath(r);
                var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                var isInside = string.Equals(combined, root, StringComparison.OrdinalIgnoreCase)
                    || (combined + Path.DirectorySeparatorChar).StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
                if (isInside)
                {
                    fullPath = combined;
                    error = null;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Path relative to the working directory, for display.</summary>
    public static string Relative(ToolContext ctx, string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(ctx.WorkingDirectory), fullPath).Replace('\\', '/');
}
