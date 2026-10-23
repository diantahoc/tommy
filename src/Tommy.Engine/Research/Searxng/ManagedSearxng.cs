using System.Diagnostics;
using Tommy.Core.Diagnostics;

namespace Tommy.Engine.Research.Searxng;

/// <summary>
/// Owns a managed SearXNG process: launches <c>serve_waitress.py</c> with the venv's Python on
/// localhost and waits for its JSON API to answer. Mirrors <see cref="ManagedEngineProcess"/> but
/// for the Python server. Stopping kills the whole tree (Waitress spawns worker threads in-process,
/// but a tree kill is still the safe stop).
/// </summary>
public sealed class ManagedSearxng : IDisposable
{
    private readonly string _serverDir;
    private readonly string _venvPython;
    private readonly int _port;
    private Process? _process;

    public ManagedSearxng(string serverDir, string venvPython, int port)
    {
        _serverDir = serverDir;
        _venvPython = venvPython;
        _port = port;
    }

    public string Url => $"http://127.0.0.1:{_port}";

    public bool IsAlive => _process is { HasExited: false };

    public void Start()
    {
        if (IsAlive) return;

        var psi = new ProcessStartInfo(_venvPython)
        {
            WorkingDirectory = _serverDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("serve_waitress.py");
        psi.Environment["SEARXNG_BIND_ADDRESS"] = "127.0.0.1";
        psi.Environment["SEARXNG_PORT"] = _port.ToString();

        _process = Process.Start(psi);
        Log.Info($"managed searxng starting on {Url}");
    }

    /// <summary>Poll the JSON API until it answers, or the timeout elapses.</summary>
    public async Task<bool> WaitReadyAsync(HttpClient http, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{Url}/search?q=test&format=json";
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAlive) return false; // the process died during startup

            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { /* not up yet */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* per-try timeout */ }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    public void Stop()
    {
        if (IsAlive)
        {
            try { _process!.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
