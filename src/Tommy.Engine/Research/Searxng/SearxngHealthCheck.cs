using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Tommy.Engine.Research.Searxng;

/// <summary>
/// A diagnostic for the SearXNG backend: starts the managed server (capturing its output), probes
/// the JSON API, and reports a human-readable verdict — so a "installed but not working" case shows
/// the actual reason (a startup traceback, the JSON format disabled, 0 results, …) rather than
/// silently degrading. Tests a custom URL directly without launching anything.
/// </summary>
public static class SearxngHealthCheck
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Run the check. Returns a multi-line verdict for the Settings status area.</summary>
    public static async Task<string> RunAsync(SearxngInstaller installer, string? customUrl, int port, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(customUrl))
            return "Custom SearXNG URL:\n" + await ProbeAsync(customUrl!.Trim(), ct).ConfigureAwait(false);

        if (!installer.IsInstalled)
            return "Managed SearXNG is not installed — click Install / Update first.";

        var serverDir = installer.FindServerDir();
        var venvPython = installer.FindVenvPython();
        if (serverDir is null || venvPython is null)
            return "The install looks incomplete (server directory or venv is missing). Try Install / Update.";

        var output = new StringBuilder();
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(venvPython)
            {
                WorkingDirectory = serverDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("serve_waitress.py");
            psi.Environment["SEARXNG_BIND_ADDRESS"] = "127.0.0.1";
            psi.Environment["SEARXNG_PORT"] = port.ToString();

            proc = Process.Start(psi);
            if (proc is null) return "Could not start the SearXNG process.";
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var baseUrl = $"http://127.0.0.1:{port}";
            var ready = await PollReadyAsync(baseUrl, proc, TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
            if (!ready)
            {
                var tail = Tail(output.ToString());
                return proc.HasExited
                    ? $"The server exited during startup (exit {proc.ExitCode}).\n\n--- server output ---\n{tail}"
                    : $"The server did not answer on {baseUrl} within 40s.\n\n--- server output ---\n{tail}";
            }

            var verdict = await ProbeAsync(baseUrl, ct).ConfigureAwait(false);
            var warnings = Warnings(output.ToString());
            return "Managed SearXNG:\n" + verdict + warnings;
        }
        catch (Exception ex)
        {
            return $"Health check failed: {ex.Message}";
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            proc?.Dispose();
        }
    }

    /// <summary>GET the JSON API once and classify the response.</summary>
    private static async Task<string> ProbeAsync(string baseUrl, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/search?q=test&format=json";
        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";

            if (!resp.IsSuccessStatusCode)
                return $"✗ HTTP {(int)resp.StatusCode} from {url}\n{Snippet(body)}";

            if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && !body.TrimStart().StartsWith("{"))
                return $"✗ Reachable, but the JSON format is disabled (got {contentType}). Enable search.formats: [html, json] in settings_windows.yml.\n{Snippet(body)}";

            try
            {
                using var doc = JsonDocument.Parse(body);
                var count = doc.RootElement.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
                return count > 0
                    ? $"✓ Working — {count} results for \"test\"."
                    : "✗ Reachable and JSON is on, but 0 results — all engines may be failing or rate-limited (check the server output / settings).";
            }
            catch (JsonException)
            {
                return $"✗ Reachable, but the response wasn't valid JSON.\n{Snippet(body)}";
            }
        }
        catch (Exception ex)
        {
            return $"✗ Could not reach {url}: {ex.Message}";
        }
    }

    private static async Task<bool> PollReadyAsync(string baseUrl, Process proc, TimeSpan timeout, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/search?q=test&format=json";
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited) return false;
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { /* not up yet */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* per-try timeout */ }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static string Warnings(string output)
    {
        var lines = output.Split('\n')
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("warning", StringComparison.OrdinalIgnoreCase) || l.Contains("Traceback", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !IsBenign(l))
            .Distinct()
            .Take(5)
            .ToList();
        return lines.Count == 0 ? "" : "\n\nServer notes:\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// Lines that are expected and harmless for our install, so they don't make a working
    /// instance look half-failed:
    ///  • version / git-URL lookup — we install via `git archive` and drop .git (the NTFS-illegal
    ///    path workaround), so SearXNG can't read its version from a repo and falls back.
    ///  • first-run cache "hash token changed: truncate" warnings.
    ///  • a single engine failing to register (e.g. the Tor-only ahmia) — the rest still load.
    /// </summary>
    private static bool IsBenign(string line) =>
        line.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Error while getting the version", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Error while getting the git URL", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("hash token changed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("truncate all cache tables", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("can't register engine", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("loading engine failed", StringComparison.OrdinalIgnoreCase);

    private static string Tail(string text, int lines = 20)
    {
        var all = text.Replace("\r", "").Split('\n').Where(l => l.Length > 0).ToList();
        return all.Count <= lines ? string.Join("\n", all) : string.Join("\n", all.Skip(all.Count - lines));
    }

    private static string Snippet(string body)
    {
        var clean = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length == 0 ? "" : "  " + clean[..Math.Min(200, clean.Length)];
    }
}
