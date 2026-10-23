using System.Security.Cryptography;
using Tommy.Core.Diagnostics;
using Tommy.Core.Runtime;
using Tommy.Engine.Provisioning;

namespace Tommy.Engine.Research.Searxng;

/// <summary>
/// Clones upstream SearXNG at a pinned commit (with the bundled git — the commit SHA is the
/// integrity pin), lays our overlay (patch + settings + launcher, from the scripts/ kit) on top,
/// and builds a Python venv against the <b>bundled</b> interpreter — no system Python or a published
/// release archive needed. The result is a ready-to-run SearXNG under the install root that
/// <see cref="ManagedSearxng"/> launches.
///
/// Idempotent: <see cref="IsInstalled"/> reports whether a usable install already exists, and the
/// <c>.installed</c> marker records the commit it was built from (so a changed commit is an update).
/// All work runs off the UI thread; progress is reported through the <c>onProgress</c> callback.
/// </summary>
public sealed class SearxngInstaller
{
    // Overlay files copied from scripts/searxng/ onto the cloned source (the rest — README, setup.ps1 —
    // are repo-building aids, not needed at runtime).
    private static readonly string[] OverlayFiles =
        { "serve_waitress.py", "settings_windows.yml", "apply_windows_patch.py", "test_fakeredis_limiter.py" };

    private const string SecretPlaceholder = "CHANGE_ME_run_the_token_hex_command";

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

    /// <summary>The commit that produced the current install (from the marker), or null.</summary>
    public string? InstalledFrom =>
        File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;

    /// <summary>True when the pinned commit differs from what's installed — an update is available.</summary>
    public bool UpdateAvailable(string? commit) =>
        !string.IsNullOrWhiteSpace(commit) && IsInstalled &&
        !string.Equals(commit!.Trim(), InstalledFrom, StringComparison.Ordinal);

    private string MarkerPath => Path.Combine(_root, Marker);

    /// <summary>
    /// Clone @commit → overlay → secret key → venv → pip install (incl. waitress + fakeredis[lua]) →
    /// patch → marker. Throws on failure (git/network, pip, missing bundled Python/git, bad overlay).
    /// </summary>
    public async Task InstallAsync(string repoUrl, string commit, string overlayDir, Action<string, double?>? onProgress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoUrl) || string.IsNullOrWhiteSpace(commit))
            throw new InvalidOperationException("No managed-SearXNG source (repo + commit) is configured.");

        var basePython = IntegratedTools.FindPython()
            ?? throw new InvalidOperationException("The bundled Python was not found; it must be provisioned first.");
        var git = IntegratedTools.FindGit() ?? "git";

        Directory.CreateDirectory(_root);

        // 1. Fetch the pinned commit (shallow) into a fresh src/, then export ONLY searx/ +
        // requirements.txt via `git archive`. A normal checkout can't be used: git validates every
        // tree entry's path while reading it into the index — before skip-worktree/sparse filtering —
        // and the repo has files with ':' in their names (utils/templates/…) that are illegal on
        // NTFS, so any checkout aborts. `git archive` with a pathspec never reads those entries, and
        // searx/ + requirements.txt is all the server needs at runtime (settings come from the overlay).
        var srcDir = Path.Combine(_root, "src");
        ForceDeleteDirectory(srcDir);
        Directory.CreateDirectory(srcDir);

        onProgress?.Invoke("fetching SearXNG…", null);
        await ProvisionIo.RunAsync(git, new[] { "init", "--quiet", srcDir }, _root, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        await ProvisionIo.RunAsync(git, new[] { "-C", srcDir, "fetch", "--depth", "1", repoUrl.Trim(), commit.Trim() },
            srcDir, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

        // core.protectNTFS=false lets git read the tree past the NTFS-illegal ':' paths; the pathspec
        // keeps the archive to searx/ + requirements.txt, so those paths are never written to disk.
        onProgress?.Invoke("extracting SearXNG…", null);
        var tarPath = Path.Combine(_root, "searxng-src.tar");
        await ProvisionIo.RunAsync(git, new[] { "-c", "core.protectNTFS=false", "-C", srcDir, "archive", "--format=tar", "-o", tarPath, "FETCH_HEAD", "searx", "requirements.txt" },
            srcDir, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        await ProvisionIo.RunAsync("tar", new[] { "-xf", tarPath, "-C", srcDir }, srcDir, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        try { File.Delete(tarPath); } catch { /* best effort */ }
        ForceDeleteDirectory(Path.Combine(srcDir, ".git")); // git marks pack files read-only

        // 2. Lay our overlay (launcher + settings + patch) on top of the vanilla source.
        onProgress?.Invoke("applying overlay…", null);
        ApplyOverlay(overlayDir, srcDir);

        // 3. Fill the settings secret_key if it is still the placeholder.
        FillSecretKey(Path.Combine(srcDir, "settings_windows.yml"));

        // 4. Locate the server directory (the one with serve_waitress.py).
        var serverDir = FindServerDir()
            ?? throw new InvalidOperationException("serve_waitress.py was not found after the overlay was applied.");

        // 5. Create the venv with the bundled interpreter.
        onProgress?.Invoke("creating virtual environment…", null);
        var venvDir = Path.Combine(serverDir, ".venv");
        ForceDeleteDirectory(venvDir);
        await ProvisionIo.RunAsync(basePython, new[] { "-m", "venv", venvDir }, serverDir, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        var venvPy = VenvPython(serverDir);
        if (!File.Exists(venvPy))
            throw new InvalidOperationException("venv creation did not produce a Python interpreter.");

        // 6. Install dependencies (this is the slow part).
        onProgress?.Invoke("upgrading pip…", null);
        await ProvisionIo.RunAsync(venvPy, new[] { "-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip" },
            serverDir, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        onProgress?.Invoke("installing dependencies (a few minutes the first time)…", null);
        var pip = new List<string> { "-m", "pip", "install", "--disable-pip-version-check" };
        var requirements = Path.Combine(serverDir, "requirements.txt");
        if (File.Exists(requirements)) { pip.Add("-r"); pip.Add(requirements); }
        pip.Add("waitress");
        pip.Add("fakeredis[lua]");
        // The bundled (python-build-standalone) interpreter ships no IANA tz database, so
        // zoneinfo.ZoneInfo(...) — used by several engines (e.g. bilibili) — fails on Windows
        // with "No module named 'tzdata'". The tzdata wheel is zoneinfo's standard fallback.
        pip.Add("tzdata");
        await ProvisionIo.RunAsync(venvPy, pip, serverDir, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);

        // 7. Apply the Windows patch (guards the Unix-only `import pwd`; adds the memory:// limiter).
        var patch = Path.Combine(serverDir, "apply_windows_patch.py");
        var valkeydb = Path.Combine(serverDir, "searx", "valkeydb.py");
        if (File.Exists(patch) && File.Exists(valkeydb))
        {
            onProgress?.Invoke("applying patch…", null);
            await ProvisionIo.RunAsync(venvPy, new[] { patch, valkeydb }, serverDir, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        }

        // 8. Record the commit we built from.
        File.WriteAllText(MarkerPath, commit.Trim());
        onProgress?.Invoke("ready", 100);
        Log.Info($"managed searxng installed at {serverDir} (commit {commit.Trim()[..Math.Min(8, commit.Trim().Length)]})");
    }

    /// <summary>Copy the runtime overlay files from the scripts/ kit onto the cloned source.</summary>
    private static void ApplyOverlay(string overlayDir, string destDir)
    {
        if (!Directory.Exists(overlayDir))
            throw new InvalidOperationException($"SearXNG overlay not found at {overlayDir}.");

        foreach (var name in OverlayFiles)
        {
            var src = Path.Combine(overlayDir, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(destDir, name), overwrite: true);
        }
    }

    /// <summary>Replace the settings secret_key placeholder with a fresh random token, if still present.</summary>
    private static void FillSecretKey(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return;
        var text = File.ReadAllText(settingsPath);
        if (!text.Contains(SecretPlaceholder)) return;

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(settingsPath, text.Replace(SecretPlaceholder, token));
    }

    /// <summary>
    /// Delete a directory, clearing read-only attributes first. Git marks objects/pack files
    /// read-only, so a plain <see cref="Directory.Delete(string,bool)"/> throws
    /// <see cref="UnauthorizedAccessException"/> on a leftover <c>.git</c> from a failed run.
    /// </summary>
    private static void ForceDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
        }
        Directory.Delete(dir, recursive: true);
    }
}
