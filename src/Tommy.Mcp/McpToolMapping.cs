using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Mcp;
using Tommy.Core.Tools;

namespace Tommy.Mcp;

/// <summary>
/// The pure policy that turns an MCP server instance + a remote tool into the
/// three things the registry needs: a collision-free advertised name, a
/// permission tier, and a parameter schema. Kept free of SDK/connection types so
/// it is directly unit-testable.
/// </summary>
public static class McpToolMapping
{
    /// <summary>
    /// Instance-prefixed name (<c>productiondb_show_tables</c>) so two instances of
    /// the same server don't collide in the model's flat list — the namespacing
    /// that makes instancing possible.
    /// </summary>
    public static string AdvertisedName(McpServerConfig config, string remoteName)
    {
        var prefix = Sanitize(config.Id);
        var name = Sanitize(remoteName);
        return prefix.Length == 0 ? name : $"{prefix}_{name}";
    }

    /// <summary>
    /// Tier from route + read-only flag: integrated read-only → <c>Read</c>,
    /// integrated read-write → <c>Write</c> (follows the autonomy grid); plain /
    /// user servers → a conservative <c>Write</c> tier forced to always ask, since
    /// the harness can't know their side effects.
    /// </summary>
    public static (PermissionTier Tier, PermissionAction? Override) Resolve(McpServerConfig config) =>
        config.Route == McpRoute.Integrated
            ? (config.ReadOnly ? PermissionTier.Read : PermissionTier.Write, (PermissionAction?)null)
            : (PermissionTier.Write, PermissionAction.Ask);

    /// <summary>The server's JSON-Schema object, or a permissive empty object schema.</summary>
    public static JsonObject Schema(JsonElement? inputSchema)
    {
        if (inputSchema is { ValueKind: JsonValueKind.Object } el &&
            JsonNode.Parse(el.GetRawText()) is JsonObject obj)
            return obj;
        return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
    }

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? char.ToLowerInvariant(c) : '_');
        return new string(chars.ToArray()).Trim('_');
    }
}
