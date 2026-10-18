namespace Tommy.Core.Mcp;

/// <summary>Transport by which an MCP server is reached.</summary>
public enum McpTransportKind
{
    /// <summary>Local subprocess over stdio.</summary>
    Stdio,

    /// <summary>Remote server over streamable HTTP / SSE.</summary>
    Http
}

/// <summary>
/// How an MCP server's tools reach the model — the four routes of the catalog
/// collapse to two the manager cares about:
/// <list type="bullet">
/// <item><see cref="Integrated"/> — wrapped by a C# integration layer that derives
/// a permission tier from config (read-only flag), so tools register at the right
/// tier and follow the autonomy grid.</item>
/// <item><see cref="Plain"/> — bundled-generic or user servers reached through the
/// plain client with no wrapper; the harness can't know their side effects, so
/// they register at a conservative tier that always asks.</item>
/// </list>
/// </summary>
public enum McpRoute
{
    Plain,
    Integrated
}

/// <summary>Whether a server definition yields one instance or many named ones.</summary>
public enum McpCardinality
{
    Singleton,
    Instanceable
}

/// <summary>
/// A fully-resolved MCP server instance: its transport, how it routes to the
/// model, and its read-only disposition. Credentials are injected into
/// <see cref="Env"/> / <see cref="Headers"/> by the caller (from the secrets
/// interface), never stored here.
/// </summary>
public sealed record McpServerConfig(
    string Id,
    string DisplayName,
    McpTransportKind Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? WorkingDirectory = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    McpRoute Route = McpRoute.Plain,
    McpCardinality Cardinality = McpCardinality.Singleton,
    bool ReadOnly = false);
