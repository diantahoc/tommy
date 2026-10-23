namespace Tommy.Core.Runtime;

/// <summary>
/// Resolves the <c>integrated-tools</c> directory — the runtimes (Node, Python),
/// browsers (Chromium), helper programs (the PDF script, the Node web-fetch
/// program) and MCP servers. Tommy <b>provisions these at runtime</b> into
/// <c>~/.tommy/integrated-tools</c> (see <c>Tommy.Engine.Provisioning</c>), so the
/// root is the user's home, not a copy beside the executable. The layout under it
/// is unchanged: <c>runtimes/ mcp/ browsers/ helpers/</c>.
///
/// Resolution order: the <c>TOMMY_INTEGRATED_TOOLS</c> override (dev / prebaked /
/// air-gapped), otherwise <c>TommyHome/integrated-tools</c>.
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
    /// The bundled ffmpeg executable — it ships alongside Playwright's Chromium under
    /// <c>browsers/ffmpeg-*/</c> — or null if not present. Used to read media metadata.
    /// </summary>
    public static string? FindFfmpeg()
    {
        var dir = Path.Combine(Root, "browsers");
        if (!Directory.Exists(dir)) return null;

        var exeGlob = OperatingSystem.IsWindows() ? "ffmpeg*.exe" : "ffmpeg*";
        foreach (var ff in Directory.EnumerateDirectories(dir, "ffmpeg*"))
            foreach (var exe in Directory.EnumerateFiles(ff, exeGlob))
                return exe;
        return null;
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

    /// <summary>
    /// The <c>scripts/</c> source kit shipped beside the app (our Node web-fetch
    /// program, the PDF helper, the provisioning manifest). The provisioner reads
    /// it to install our tools into <see cref="Root"/>; it is never the run
    /// location. Honours <c>TOMMY_SCRIPTS</c> for dev overrides.
    /// </summary>
    public static string ScriptsSource { get; } =
        Environment.GetEnvironmentVariable("TOMMY_SCRIPTS") is { Length: > 0 } s
            ? s
            : Path.Combine(AppContext.BaseDirectory, "scripts");

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("TOMMY_INTEGRATED_TOOLS");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        return Path.Combine(TommyHome.Root, "integrated-tools");
    }
}
