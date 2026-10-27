using Tommy.Core.Diagnostics;
using Tommy.Core.Mcp;
using Tommy.Core.Tools;

namespace Tommy.Mcp;

/// <summary>
/// Owns MCP server connections and plugs their tools into the shared
/// <see cref="ToolRegistry"/>, so the agent loop treats native and MCP tools
/// uniformly. Connecting a server lists its tools and registers a
/// <see cref="McpToolProxy"/> for each; disconnecting withdraws them.
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly ToolRegistry _registry;
    private readonly Dictionary<string, McpConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _registered = new(StringComparer.Ordinal);

    public McpManager(ToolRegistry registry) => _registry = registry;

    public IReadOnlyCollection<string> ConnectedIds => _connections.Keys;

    /// <summary>Tool names a connected server contributes to the registry, or empty.</summary>
    public IReadOnlyList<string> RegisteredTools(string id) =>
        _registered.TryGetValue(id, out var names) ? names : Array.Empty<string>();

    /// <summary>
    /// Connect (or reconnect) a server and register its tools. Returns the
    /// advertised tool names. Never throws into the caller's UI path — a failed
    /// connect is logged and surfaced as an empty result.
    /// </summary>
    public async Task<IReadOnlyList<string>> ConnectAsync(McpServerConfig config, CancellationToken ct = default)
    {
        await DisconnectAsync(config.Id).ConfigureAwait(false);

        McpConnection conn;
        try
        {
            conn = await OpenWithRetryAsync(config, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"mcp connect failed: {config.Id}", ex);
            return Array.Empty<string>();
        }

        _connections[config.Id] = conn;

        var (tier, approvalOverride) = McpToolMapping.Resolve(config);
        var names = new List<string>();
        foreach (var tool in conn.Tools)
        {
            var advertised = McpToolMapping.AdvertisedName(config, tool.Name);
            var schema = McpToolMapping.Schema(tool.JsonSchema);
            _registry.Register(new McpToolProxy(advertised, tool.Name, tool.Description ?? string.Empty, schema, tier, approvalOverride, conn));
            names.Add(advertised);
        }

        _registered[config.Id] = names;
        Log.Info($"mcp connected: {config.Id} route={config.Route} readonly={config.ReadOnly} tools={names.Count}");
        return names;
    }

    private const int ConnectAttempts = 3;

    /// <summary>
    /// Open a connection, retrying a transient cold-start failure. A stdio server's
    /// process can exit on the first spawn (ClientTransportClosedException) and
    /// succeed on a retry, so one failed attempt shouldn't leave the server
    /// unavailable. Throws the last attempt's exception if every attempt fails.
    /// </summary>
    private static Task<McpConnection> OpenWithRetryAsync(McpServerConfig config, CancellationToken ct) =>
        AsyncRetry.RunAsync(
            c => McpConnection.OpenAsync(config, c),
            ConnectAttempts,
            TimeSpan.FromMilliseconds(700),
            (attempt, ex) => Log.Warn($"mcp connect attempt {attempt}/{ConnectAttempts} failed: {config.Id}: {ex.Message}"),
            ct);

    public async Task DisconnectAsync(string id)
    {
        if (_registered.Remove(id, out var names))
            foreach (var name in names)
                _registry.Unregister(name);

        if (_connections.Remove(id, out var conn))
            await conn.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _connections.Keys.ToList())
            await DisconnectAsync(id).ConfigureAwait(false);
    }
}
