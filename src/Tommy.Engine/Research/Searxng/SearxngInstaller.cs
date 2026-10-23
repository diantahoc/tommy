using System.IO.Compression;
using Tommy.Core.Diagnostics;
using Tommy.Core.Runtime;
using Tommy.Engine.Provisioning;

namespace Tommy.Engine.Research.Searxng;

/// <summary>
/// Downloads a managed-SearXNG release archive (patched source + kit), extracts it, and builds a
/// Python venv against the <b>bundled</b> interpreter — no system Python needed. The result is a
/// ready-to-run SearXNG under the install root that <see cref="ManagedSearxng"/> launches.
///
/// Idempotent: <see cref="IsInstalled"/> reports whether a usable install already exists, and the
/// <c>.installed</c> marker records which release URL produced it (so a changed URL is an update).
/// All work runs off the UI thread; progress is reported through the <c>onProgress</c> callback.
/// </summary>
public sealed class SearxngInstaller
{
    private const string Marker = ".installed";

    private readonly string _root;

    public SearxngInstaller(string installRoot) => _root = installRoot;

    /// <summary>The extracted directory that holds <c>serve_waitress.py</c> (the SearXNG server), or null.</summary>
    public string? FindServerDir()
    {
        if (!Directory.Exists(_root)) return null;
        foreach (var f in Directory.EnumerateFiles(_root, "serve_waitress.py", SearchOption.AllDirectories))
            return Path.GetDirectoryName(f);
        return null;
    }

    /// <summary>The venv interpreter for a located server dir, or null if the venv is missing.</summary>
    public string? FindVenvPython()
    {
        var server = FindServerDir();
        if (server is null) return null;
        var py = VenvPython(server);
        return File.Exists(py) ? py : null;
    }

    public static string VenvPython(string serverDir) => OperatingSystem.IsWindows()
        ? Path.Combine(serverDir, ".venv", "Scripts", "python.exe")
        : Path.Combine(serverDir, ".venv", "bin", "python3");

    /// <summary>True when a built SearXNG is present and runnable.</summary>
    public bool IsInstalled => File.Exists(MarkerPath) && FindVenvPython() is not null;

    /// <summary>The release URL that produced the current install (from the marker), or null.</summary>
    public string? InstalledFrom =>
        File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;

    /// <summary>True when the configured URL differs from what's installed — an update is available.</summary>
    public bool UpdateAvailable(string? releaseUrl) =>
        !string.IsNullOrWhiteSpace(releaseUrl) && IsInstalled &&
        !string.Equals(releaseUrl!.Trim(), InstalledFrom, StringComparison.Ordinal);

    private string MarkerPath => Path.Combine(_root, Marker);

    /// <summary>
    /// Download → extract → venv → pip install (incl. waitress + fakeredis[lua]) → optional patch →
    /// marker. Throws on failure (download/network, pip, missing bundled Python, bad archive).
    /// </summary>
    public async Task InstallAsync(string releaseUrl, Action<string, double?>? onProgress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl))
            throw new InvalidOperationException("No managed-SearXNG release URL is configured.");

        var basePython = IntegratedTools.FindPython()
            ?? throw new InvalidOperationException("The bundled Python was not found; run build-and-copy first.");

        Directory.CreateDirectory(_root);

        // 1. Download the archive.
        var zipPath = Path.Combine(_root, "download.zip");
        await ProvisionIo.DownloadAsync(releaseUrl.Trim(), zipPath, onProgress, ct).ConfigureAwait(false);

        // 2. Extract fresh (drop any previous source so an update is clean).
        onProgress?.Invoke("extracting…", null);
        var srcDir = Path.Combine(_root, "src");
        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, srcDir);
        try { File.Delete(zipPath); } catch { /* best effort */ }

        // 3. Locate the server directory (the one with serve_waitress.py).
        var serverDir = FindServerDir()
            ?? throw new InvalidOperationException("serve_waitress.py was not found in the downloaded archive.");

        // 4. Create the venv with the bundled interpreter.
        onProgress?.Invoke("creating virtual environment…", null);
        var venvDir = Path.Combine(serverDir, ".venv");
        if (Directory.Exists(venvDir)) Directory.Delete(venvDir, recursive: true);
        await ProvisionIo.RunAsync(basePython, new[] { "-m", "venv", venvDir }, serverDir, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        var venvPy = VenvPython(serverDir);
        if (!File.Exists(venvPy))
            throw new InvalidOperationException("venv creation did not produce a Python interpreter.");

        // 5. Install dependencies (this is the slow part).
        onProgress?.Invoke("upgrading pip…", null);
        await ProvisionIo.RunAsync(venvPy, new[] { "-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip" },
            serverDir, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        onProgress?.Invoke("installing dependencies (a few minutes the first time)…", null);
        var pip = new List<string> { "-m", "pip", "install", "--disable-pip-version-check" };
        var requirements = Path.Combine(serverDir, "requirements.txt");
        if (File.Exists(requirements)) { pip.Add("-r"); pip.Add(requirements); }
        pip.Add("waitress");
        pip.Add("fakeredis[lua]");
        await ProvisionIo.RunAsync(venvPy, pip, serverDir, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);

        // 6. Apply the Windows patch if the kit shipped it (idempotent; harmless if pre-patched).
        var patch = Path.Combine(serverDir, "apply_windows_patch.py");
        var valkeydb = Path.Combine(serverDir, "searx", "valkeydb.py");
        if (File.Exists(patch) && File.Exists(valkeydb))
        {
            onProgress?.Invoke("applying patch…", null);
            await ProvisionIo.RunAsync(venvPy, new[] { patch, valkeydb }, serverDir, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        }

        // 7. Record what we installed.
        File.WriteAllText(MarkerPath, releaseUrl.Trim());
        onProgress?.Invoke("ready", 100);
        Log.Info($"managed searxng installed at {serverDir}");
    }
}
