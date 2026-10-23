using Tommy.Core.Diagnostics;
using Tommy.Core.Runtime;

namespace Tommy.Engine.Provisioning;

/// <summary>
/// Bootstraps Tommy's external programs at runtime into <c>~/.tommy/integrated-tools</c>
/// (the layout <see cref="IntegratedTools"/> resolves), from the pinned, SHA-256-verified
/// <c>scripts/tools.manifest.json</c>. Replaces the old build-time <c>build-and-copy</c>
/// step so a user can just run Tommy and have it set itself up.
///
/// Steps, in dependency order: Node and Python runtimes first, then the MCP servers
/// (need Node), then our own scripts from the beside-app <c>scripts/</c> kit
/// (web-fetch-js needs Node + Chromium; pdf_extract needs Python + PyMuPDF). Each step
/// is idempotent — a per-tool marker under <c>.provision/</c> records the installed
/// signature, so finished tools are skipped and a version/content change re-provisions.
/// Everything runs off the UI thread; progress flows through the <c>onProgress</c> callback.
/// </summary>
public sealed class ToolProvisioner
{
    // Runtimes are installed before anything that shells out to them.
    private static readonly string[] ArchiveOrder = { "node", "python" };

    private readonly string _root;          // integrated-tools root
    private readonly string _scriptsSource; // beside-app scripts/ kit
    private readonly string _markerDir;

    public ToolProvisioner(string? integratedToolsRoot = null, string? scriptsSource = null)
    {
        _root = integratedToolsRoot ?? IntegratedTools.Root;
        _scriptsSource = scriptsSource ?? IntegratedTools.ScriptsSource;
        _markerDir = Path.Combine(_root, ".provision");
    }

    /// <summary>The manifest shipped beside the app, or null if the scripts kit isn't present.</summary>
    public string ManifestPath => Path.Combine(_scriptsSource, "tools.manifest.json");

    public bool NodeReady => IntegratedTools.FindNode() is not null;
    public bool PythonReady => IntegratedTools.FindPython() is not null;

    /// <summary>True when every tool in the manifest is installed and up to date (markers match).</summary>
    public bool IsFullyProvisioned()
    {
        if (!File.Exists(ManifestPath)) return false;
        ToolManifest manifest;
        try { manifest = ToolManifest.Load(ManifestPath); }
        catch { return false; }

        foreach (var (name, spec) in manifest.Archives)
            if (spec.AssetForHere() is { } asset && !MarkerMatches(name, ArchiveSignature(spec, asset)))
                return false;
        foreach (var (name, spec) in manifest.Scripts)
            if (AppliesHere(spec) && !MarkerMatches(name, ScriptSignature(spec)))
                return false;

        if (!IntegratedTools.HasChromium()) return false; // installed by Playwright, not the manifest
        return true;
    }

    /// <summary>Whether a script applies to the current OS (its <c>os</c> filter matches, or is unset).</summary>
    private static bool AppliesHere(ScriptSpec spec) =>
        spec.Os is null
        || (spec.Os.Equals("windows", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
        || (spec.Os.Equals("linux", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsLinux());

    /// <summary>
    /// Provision everything the manifest declares. <paramref name="force"/> re-installs even
    /// up-to-date tools (the Settings "Repair" button). Throws on a hard failure (network,
    /// bad hash, npm/pip/playwright) after reporting it through <paramref name="onProgress"/>.
    /// </summary>
    public async Task ProvisionAsync(Action<string, double?>? onProgress, bool force, CancellationToken ct)
    {
        if (!File.Exists(ManifestPath))
            throw new InvalidOperationException($"provisioning manifest not found at {ManifestPath}.");

        var manifest = ToolManifest.Load(ManifestPath);
        Directory.CreateDirectory(_markerDir);

        var archives = manifest.Archives
            .OrderBy(kv => Array.IndexOf(ArchiveOrder, kv.Key) is var i && i >= 0 ? i : int.MaxValue)
            .ToList();

        foreach (var (name, spec) in archives)
            await ProvisionArchiveAsync(name, spec, onProgress, force, ct).ConfigureAwait(false);

        foreach (var (name, spec) in manifest.Scripts)
            await ProvisionScriptAsync(name, spec, onProgress, force, ct).ConfigureAwait(false);

        await ProvisionChromiumAsync(onProgress, force, ct).ConfigureAwait(false);

        onProgress?.Invoke("ready", 100);
    }

    /// <summary>
    /// Install Chromium for Playwright into <c>integrated-tools/browsers</c> via Microsoft.Playwright's
    /// own installer (its build is pinned by the package version). Idempotent: a no-op when Chromium is
    /// already present. Backs both web_fetch_js and the interactive browser.
    /// </summary>
    private async Task ProvisionChromiumAsync(Action<string, double?>? onProgress, bool force, CancellationToken ct)
    {
        if (!force && IntegratedTools.HasChromium()) return;

        onProgress?.Invoke("chromium: downloading (first run, a few minutes)…", null);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", IntegratedTools.Browsers());
        Directory.CreateDirectory(IntegratedTools.Browsers());

        var exit = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }), ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"Playwright Chromium install failed (exit {exit}).");
        Log.Info("provisioned Chromium for Playwright");
    }

    // --- archives (Node, Python, MCP servers) -------------------------------

    private async Task ProvisionArchiveAsync(string name, ArchiveSpec spec, Action<string, double?>? onProgress, bool force, CancellationToken ct)
    {
        var asset = spec.AssetForHere();
        if (asset is null)
        {
            Log.Warn($"provision: {name} has no asset for {ToolManifest.PlatformKey}; skipping.");
            return;
        }

        var signature = ArchiveSignature(spec, asset);
        if (!force && MarkerMatches(name, signature)) return;

        var dest = Path.Combine(_root, spec.Dest.Replace('/', Path.DirectorySeparatorChar));

        // Clean a stale install so a version bump doesn't leave old files behind.
        CleanDestination(dest, spec.Dest);

        Directory.CreateDirectory(dest);
        var ext = PreserveArchiveExtension(asset.Url);
        var tmp = Path.Combine(_markerDir, $"{name}{ext}");

        onProgress?.Invoke($"{name}: downloading…", null);
        await ProvisionIo.DownloadAsync(asset.Url, tmp, (m, p) => onProgress?.Invoke($"{name}: {m}", p), ct).ConfigureAwait(false);

        onProgress?.Invoke($"{name}: verifying…", null);
        await ProvisionIo.VerifyAsync(tmp, asset.Sha256, ct).ConfigureAwait(false);

        if (string.Equals(spec.Install, "msi", StringComparison.OrdinalIgnoreCase))
        {
            // Admin-extract the MSI (no system install / elevation). msiexec wants TARGETDIR="…".
            onProgress?.Invoke($"{name}: installing…", null);
            await ProvisionIo.RunRawAsync("msiexec", $"/a \"{tmp}\" /qn TARGETDIR=\"{dest}\"", dest, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        }
        else
        {
            onProgress?.Invoke($"{name}: extracting…", null);
            await ProvisionIo.ExtractAsync(tmp, dest, spec.StripComponents, ct).ConfigureAwait(false);
        }
        try { File.Delete(tmp); } catch { /* best effort */ }

        if (spec.NpmInstall is { } npmArgs)
        {
            onProgress?.Invoke($"{name}: installing dependencies…", null);
            await NpmInstallAsync(dest, npmArgs, ct).ConfigureAwait(false);
        }

        WriteMarker(name, signature);
        Log.Info($"provisioned {name} ({spec.Version}) into {dest}");
    }

    // --- scripts (our own code from the scripts/ kit) -----------------------

    private async Task ProvisionScriptAsync(string name, ScriptSpec spec, Action<string, double?>? onProgress, bool force, CancellationToken ct)
    {
        if (!AppliesHere(spec)) return; // OS-restricted script that doesn't apply here

        var signature = ScriptSignature(spec);
        if (!force && MarkerMatches(name, signature)) return;

        var src = Path.Combine(_scriptsSource, spec.Source.Replace('/', Path.DirectorySeparatorChar));
        var dest = Path.Combine(_root, spec.Dest.Replace('/', Path.DirectorySeparatorChar));

        // A missing source (e.g. a user-supplied binary not yet placed in scripts/) is skipped,
        // not fatal — that one tool stays unavailable without blocking the rest of provisioning.
        if (!Directory.Exists(src) && !File.Exists(src))
        {
            Log.Warn($"provision: script source not found, skipping {name}: {src}");
            return;
        }

        onProgress?.Invoke($"{name}: installing…", null);
        if (Directory.Exists(src))
            CopyDirectory(src, dest, skipTopLevel: new[] { "node_modules" });
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }

        if (spec.NpmInstall is not null)
        {
            onProgress?.Invoke($"{name}: npm install…", null);
            await NpmInstallAsync(dest, spec.NpmInstall, ct).ConfigureAwait(false);
        }

        if (spec.Pip is { } pip && pip.Length > 0)
        {
            onProgress?.Invoke($"{name}: pip install…", null);
            await PipInstallAsync(pip, ct).ConfigureAwait(false);
        }

        WriteMarker(name, signature);
        Log.Info($"provisioned script {name} into {dest}");
    }

    // --- toolchain invocations ----------------------------------------------

    private static async Task NpmInstallAsync(string workingDir, IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        var node = IntegratedTools.FindNode() ?? throw new InvalidOperationException("bundled Node not found for npm install.");
        var npmCli = NpmCli(node) ?? throw new InvalidOperationException("bundled npm (npm-cli.js) not found.");
        var args = new List<string> { npmCli, "install" };
        args.AddRange(extraArgs);
        await ProvisionIo.RunAsync(node, args, workingDir, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
    }

    private static async Task PipInstallAsync(IReadOnlyList<string> packages, CancellationToken ct)
    {
        var python = IntegratedTools.FindPython() ?? throw new InvalidOperationException("bundled Python not found for pip install.");
        var upgrade = new[] { "-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip" };
        await ProvisionIo.RunAsync(python, upgrade, Path.GetDirectoryName(python)!, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        var args = new List<string> { "-m", "pip", "install", "--disable-pip-version-check" };
        args.AddRange(packages);
        await ProvisionIo.RunAsync(python, args, Path.GetDirectoryName(python)!, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
    }

    /// <summary>Resolve <c>npm-cli.js</c> for the bundled Node (Windows and Linux tarball layouts).</summary>
    private static string? NpmCli(string nodeExe)
    {
        var dir = Path.GetDirectoryName(nodeExe)!;
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js") }
            : new[]
            {
                Path.Combine(dir, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
                Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js")
            };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    // --- markers & signatures -----------------------------------------------

    private string MarkerPath(string name) => Path.Combine(_markerDir, name);

    private bool MarkerMatches(string name, string signature)
    {
        var path = MarkerPath(name);
        return File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), signature, StringComparison.Ordinal);
    }

    private void WriteMarker(string name, string signature)
    {
        Directory.CreateDirectory(_markerDir);
        File.WriteAllText(MarkerPath(name), signature);
    }

    private static string ArchiveSignature(ArchiveSpec spec, PlatformAsset asset) => $"{spec.Version}|{asset.Sha256}";

    /// <summary>A script's signature changes when its source changes, so app updates re-install it.</summary>
    private string ScriptSignature(ScriptSpec spec)
    {
        var src = Path.Combine(_scriptsSource, spec.Source.Replace('/', Path.DirectorySeparatorChar));
        var body = SourceHash(src);
        var knobs = $"{string.Join(',', spec.NpmInstall ?? Array.Empty<string>())}|{string.Join(',', spec.Pip ?? Array.Empty<string>())}";
        return $"{body}|{knobs}";
    }

    /// <summary>Stable hash of a source file or directory (sorted, excluding node_modules).</summary>
    private static string SourceHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        if (File.Exists(path)) return Convert.ToHexStringLower(sha.ComputeHash(File.ReadAllBytes(path)));
        if (!Directory.Exists(path)) return "missing";

        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/node_modules/"))
            .OrderBy(f => f, StringComparer.Ordinal);
        var acc = new System.Text.StringBuilder();
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(path, f).Replace('\\', '/');
            acc.Append(rel).Append(':').Append(Convert.ToHexStringLower(sha.ComputeHash(File.ReadAllBytes(f)))).Append('\n');
        }
        return Convert.ToHexStringLower(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(acc.ToString())));
    }

    // --- filesystem helpers -------------------------------------------------

    /// <summary>Remove a stale install before re-extracting. For a shared parent (e.g. runtimes/
    /// holding both node and python), only wipe the version-prefixed subfolders.</summary>
    private static void CleanDestination(string dest, string relDest)
    {
        var rel = relDest.Trim('/', '\\');
        if (string.Equals(rel, "runtimes", StringComparison.OrdinalIgnoreCase))
        {
            // node extracts node-* here; leave python-* (its own dest) alone.
            if (Directory.Exists(dest))
                foreach (var d in Directory.EnumerateDirectories(dest, "node-*"))
                    TryDelete(d);
            return;
        }
        TryDelete(dest);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static void CopyDirectory(string src, string dest, IReadOnlyList<string> skipTopLevel)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (skipTopLevel.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            CopyDirectory(dir, Path.Combine(dest, name), Array.Empty<string>());
        }
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    }

    private static string PreserveArchiveExtension(string url)
    {
        if (url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return ".tar.gz";
        if (url.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)) return ".tar.xz";
        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ".zip";
        return Path.GetExtension(new Uri(url).AbsolutePath) is { Length: > 0 } e ? e : ".bin";
    }
}
