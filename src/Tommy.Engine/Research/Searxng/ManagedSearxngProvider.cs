using Tommy.Core.Diagnostics;
using Tommy.Core.Research;

namespace Tommy.Engine.Research.Searxng;

/// <summary>
/// A search provider backed by the Tommy-managed SearXNG, started <b>lazily</b> on the first query
/// (download/install is a separate flow — see <see cref="SearxngInstaller"/>). It launches the
/// process, waits for the JSON API, then delegates to a <see cref="SearxngSearchProvider"/> pointed
/// at localhost. If the managed instance isn't installed or never becomes ready, it returns no hits
/// (the orchestrator then degrades) rather than throwing.
/// </summary>
public sealed class ManagedSearxngProvider : IWebSearchProvider, IDisposable
{
    private static readonly HttpClient Health = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly SearxngInstaller _installer;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ManagedSearxng? _managed;
    private SearxngSearchProvider? _client;

    public ManagedSearxngProvider(SearxngInstaller installer, int port)
    {
        _installer = installer;
        _port = port;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (!await EnsureStartedAsync(ct).ConfigureAwait(false))
            return Array.Empty<WebSearchResult>();

        return await _client!.SearchAsync(query, maxResults, ct).ConfigureAwait(false);
    }

    private async Task<bool> EnsureStartedAsync(CancellationToken ct)
    {
        if (_managed is { IsAlive: true } && _client is not null) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_managed is { IsAlive: true } && _client is not null) return true;

            var serverDir = _installer.FindServerDir();
            var venvPython = _installer.FindVenvPython();
            if (!_installer.IsInstalled || serverDir is null || venvPython is null)
            {
                Log.Warn("managed searxng is not installed; web_research has no search backend.");
                return false;
            }

            _managed = new ManagedSearxng(serverDir, venvPython, _port);
            _managed.Start();
            if (!await _managed.WaitReadyAsync(Health, TimeSpan.FromSeconds(40), ct).ConfigureAwait(false))
            {
                Log.Warn("managed searxng did not become ready in time.");
                _managed.Stop();
                _managed = null;
                return false;
            }

            _client = new SearxngSearchProvider(_managed.Url);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stop the managed process (e.g. when switching away from it or on shutdown).</summary>
    public void Stop() => _managed?.Stop();

    public void Dispose()
    {
        _managed?.Dispose();
        _gate.Dispose();
    }
}
