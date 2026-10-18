using Tommy.Core.Mcp;

namespace Tommy.Mcp.Integrations;

/// <summary>
/// The catalog of available MCP integrations (preshipped wrappers). Resolves a
/// configured <see cref="McpInstance"/> to a launch config via its integration.
/// </summary>
public sealed class McpIntegrationRegistry
{
    private readonly Dictionary<string, IMcpIntegration> _byKey;

    public McpIntegrationRegistry(IEnumerable<IMcpIntegration>? integrations = null) =>
        _byKey = (integrations ?? Default()).ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The integrations that ship with Tommy.</summary>
    public static IEnumerable<IMcpIntegration> Default() => new IMcpIntegration[]
    {
        new MySqlIntegration()
    };

    public IReadOnlyCollection<IMcpIntegration> All => _byKey.Values;

    public IMcpIntegration? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Build the launch config for an instance, or null if its integration is unknown.</summary>
    public McpServerConfig? BuildConfig(McpInstance instance) => Find(instance.IntegrationKey)?.Build(instance);
}
