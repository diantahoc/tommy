using System.Collections.Concurrent;
using Tommy.Core.Diagnostics;
using Tommy.Core.Research;

namespace Tommy.Engine.Research.Browser;

/// <summary>
/// Owns one <see cref="ManagedBrowser"/> per chat profile directory (persistent contexts can't share
/// a single browser), creating lazily and keyed by the isolated per-chat profile path. The app owns
/// one of these; a chat's tools reach its session through <c>ToolContext.OpenBrowser</c>.
/// </summary>
public sealed class BrowserSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ManagedBrowser> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Get (or lazily create) the browser session for a chat's profile directory.</summary>
    public IBrowserSession Get(string profileDir) =>
        _sessions.GetOrAdd(profileDir, dir => new ManagedBrowser(dir));

    /// <summary>Close and drop the session for a profile directory (on chat evict).</summary>
    public async ValueTask CloseAsync(string profileDir)
    {
        if (_sessions.TryRemove(profileDir, out var session))
            await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"browser session dispose: {ex.Message}"); }
        }
        _sessions.Clear();
    }
}
