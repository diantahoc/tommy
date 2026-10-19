namespace Tommy.Core.Runtime;

/// <summary>
/// Resolves the <c>integrated-tools</c> directory that ships beside the app —
/// the bundled runtimes (Node, Python), browsers (Chromium), helper programs
/// (LibreOffice, the PDF script) and preshipped MCP servers. These are
/// immutable program assets, so they live next to the executable, never in the
/// user's <c>~/.tommy</c> config root.
///
/// Resolution order: the <c>TOMMY_INTEGRATED_TOOLS</c> override, then a copy
/// sitting next to the app binaries (the packaged layout), then a walk up the
/// directory tree to the repo copy (so a dev build running out of
/// <c>bin/Debug/net9.0</c> finds the source-tree <c>integrated-tools</c> before
/// the build-and-copy step has run).
/// </summary>
public static class IntegratedTools
{
    public static string Root { get; } = Resolve();

    /// <summary>Absolute path to a program under <c>helpers/</c> (e.g. the PDF script).</summary>
    public static string Helper(params string[] parts) => Combine("helpers", parts);

    /// <summary>Absolute path under <c>runtimes/</c>.</summary>
    public static string Runtime(params string[] parts) => Combine("runtimes", parts);

    /// <summary>Absolute path under <c>browsers/</c> (Playwright's bundled Chromium).</summary>
    public static string Browsers(params string[] parts) => Combine("browsers", parts);

    /// <summary>Whether a Playwright Chromium has been installed into the bundle.</summary>
    public static bool HasChromium()
    {
        var dir = Path.Combine(Root, "browsers");
        return Directory.Exists(dir) && Directory.EnumerateDirectories(dir, "chromium*").Any();
    }

    /// <summary>The bundled Node executable, or null if none is present.</summary>
    public static string? FindNode()
    {
        var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return FindInRuntimes("node*", exe, Path.Combine("bin", exe));
    }

    /// <summary>
    /// The directory of a preshipped MCP server whose folder name starts with
    /// <paramref name="prefix"/> (e.g. "mcp-server-mysql"), or null if absent.
    /// </summary>
    public static string? FindMcpServer(string prefix)
    {
        var dir = Path.Combine(Root, "mcp");
        return Directory.Exists(dir)
            ? Directory.EnumerateDirectories(dir, prefix + "*").FirstOrDefault()
            : null;
    }

    /// <summary>
    /// The bundled LibreOffice <c>soffice</c> executable (from the office helper),
    /// or null if not present. Windows-only bundle; other platforms degrade.
    /// </summary>
    public static string? FindSoffice()
    {
        var helpers = Path.Combine(Root, "helpers");
        if (!Directory.Exists(helpers)) return null;

        var exe = OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";
        foreach (var proc in Directory.EnumerateDirectories(helpers, "office-document-processor*"))
        {
            var p = Path.Combine(proc, "LibreOffice", "program", exe);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>
    /// The bundled full Python interpreter, or null if none is present (callers
    /// then fall back to a system Python or degrade). Tolerant of a couple of
    /// common standalone/portable layouts.
    /// </summary>
    public static string? FindPython()
    {
        var runtimes = Path.Combine(Root, "runtimes");
        if (!Directory.Exists(runtimes)) return null;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "python.exe", Path.Combine("python", "python.exe") }
            : new[] { Path.Combine("bin", "python3"), Path.Combine("python", "bin", "python3") };

        return FindInRuntimes("python*", candidates);
    }

    private static string? FindInRuntimes(string dirGlob, params string[] relativeExes)
    {
        var runtimes = Path.Combine(Root, "runtimes");
        if (!Directory.Exists(runtimes)) return null;

        foreach (var dir in Directory.EnumerateDirectories(runtimes, dirGlob))
            foreach (var rel in relativeExes)
            {
                var exe = Path.Combine(dir, rel);
                if (File.Exists(exe)) return exe;
            }

        return null;
    }

    private static string Combine(string sub, string[] parts)
    {
        var all = new string[parts.Length + 2];
        all[0] = Root;
        all[1] = sub;
        Array.Copy(parts, 0, all, 2, parts.Length);
        return Path.Combine(all);
    }

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("TOMMY_INTEGRATED_TOOLS");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        var beside = Path.Combine(AppContext.BaseDirectory, "integrated-tools");
        if (Directory.Exists(beside)) return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "integrated-tools");
            if (Directory.Exists(candidate)) return candidate;
        }

        // Nothing found — return the packaged location anyway; callers handle absence.
        return beside;
    }
}
