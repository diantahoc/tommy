using Tommy.Core.Mcp;

namespace Tommy.Core.Configuration;

/// <summary>
/// A user-defined MCP server saved in <c>config.json</c> and reconnected on
/// startup. A plain, serialization-friendly shape that maps to the richer
/// <see cref="McpServerConfig"/> at connect time. User servers take the plain
/// route (always-ask) since the harness can't know their side effects.
/// </summary>
public sealed class McpServerEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;

    /// <summary>Executable for stdio transport (e.g. <c>npx</c>).</summary>
    public string? Command { get; set; }

    /// <summary>Arguments for the stdio command.</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>Endpoint for HTTP transport.</summary>
    public string? Url { get; set; }

    /// <summary>Hint that the server is read-only (informational for plain servers).</summary>
    public bool ReadOnly { get; set; }

    public McpServerConfig ToConfig() => new(
        Id: Id,
        DisplayName: string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName,
        Transport: Transport,
        Command: Command,
        Args: Args.Count > 0 ? Args : null,
        Url: Url,
        Route: McpRoute.Plain,
        Cardinality: McpCardinality.Singleton,
        ReadOnly: ReadOnly);
}
