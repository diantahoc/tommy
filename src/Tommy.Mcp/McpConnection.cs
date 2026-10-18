using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tommy.Core.Mcp;

namespace Tommy.Mcp;

/// <summary>
/// A live connection to one MCP server over stdio or HTTP. Owns the SDK client
/// and transport lifecycle; exposes the discovered tools and a call seam. Both
/// transports are supported from day one (local subprocess + remote).
/// </summary>
public sealed class McpConnection : IMcpToolInvoker, IAsyncDisposable
{
    private readonly McpClient _client;

    private McpConnection(McpClient client, IReadOnlyList<McpClientTool> tools)
    {
        _client = client;
        Tools = tools;
    }

    public IReadOnlyList<McpClientTool> Tools { get; }

    public static async Task<McpConnection> OpenAsync(McpServerConfig config, CancellationToken ct = default)
    {
        var transport = BuildTransport(config);
        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(default(RequestOptions), ct).ConfigureAwait(false);
        return new McpConnection(client, tools.ToList());
    }

    private static IClientTransport BuildTransport(McpServerConfig config)
    {
        switch (config.Transport)
        {
            case McpTransportKind.Stdio:
                return new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = config.Id,
                    Command = config.Command ?? throw new InvalidOperationException("stdio MCP requires a command"),
                    Arguments = config.Args?.ToList() ?? new List<string>(),
                    EnvironmentVariables = config.Env?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
                    WorkingDirectory = config.WorkingDirectory
                });

            case McpTransportKind.Http:
                var options = new HttpClientTransportOptions
                {
                    Name = config.Id,
                    Endpoint = new Uri(config.Url ?? throw new InvalidOperationException("http MCP requires a url"))
                };
                if (config.Headers is { Count: > 0 })
                    options.AdditionalHeaders = config.Headers.ToDictionary(kv => kv.Key, kv => kv.Value);
                return new HttpClientTransport(options);

            default:
                throw new ArgumentOutOfRangeException(nameof(config), config.Transport, "unknown MCP transport");
        }
    }

    public ValueTask<CallToolResult> CallAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct) =>
        _client.CallToolAsync(toolName, arguments, cancellationToken: ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
