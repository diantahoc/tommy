namespace Tommy.Core.Mcp;

/// <summary>
/// A field an integrated MCP instance needs (host, user, password, …). The
/// integration declares these so the UI can collect them and the wrapper can map
/// them onto however the underlying server wants them.
/// </summary>
public sealed record McpFieldSpec(
    string Key,
    string Label,
    bool Required = false,
    bool Secret = false,
    string? Default = null);

/// <summary>
/// A configured, named instance of an integrated server: its field values and
/// read-only disposition. Instances live at the project level and chats inherit
/// them. Secret values are resolved from the secrets interface by the caller
/// before building the launch config — never persisted here.
/// </summary>
public sealed record McpInstance(
    string IntegrationKey,
    string Id,
    string DisplayName,
    IReadOnlyDictionary<string, string> Values,
    bool ReadOnly = true);

/// <summary>
/// The C# wrapper that makes a generic preshipped MCP server behave as a
/// first-class Tommy tool: it declares the fields an instance needs and maps a
/// configured instance onto the server's launch config (command, args, env).
/// Name-prefixing and tier-from-config are handled uniformly by the manager /
/// mapping, so an integration only owns the server-specific launch mapping.
/// </summary>
public interface IMcpIntegration
{
    /// <summary>Stable key, e.g. "mysql".</summary>
    string Key { get; }

    string DisplayName { get; }

    McpCardinality Cardinality { get; }

    IReadOnlyList<McpFieldSpec> Fields { get; }

    /// <summary>Map a configured instance onto the server's launch config.</summary>
    McpServerConfig Build(McpInstance instance);
}
