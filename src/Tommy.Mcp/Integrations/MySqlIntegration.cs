using Tommy.Core.Mcp;

namespace Tommy.Mcp.Integrations;

/// <summary>
/// Integration layer for the open-source MariaDB/MySQL MCP server
/// (<c>@benborla29/mcp-server-mysql</c>), launched over stdio via npx.
/// Instanceable — the production/test case runs two instances (read-only
/// "Production DB", read-write "Test DB") with distinct configs and tiers.
/// Maps typed instance fields onto the server's environment variables, and
/// gates writes behind the read-only flag: the server only inserts/updates/
/// deletes when those operations are explicitly enabled.
/// </summary>
public sealed class MySqlIntegration : IMcpIntegration
{
    public const string IntegrationKey = "mysql";

    public string Key => IntegrationKey;
    public string DisplayName => "MariaDB / MySQL";
    public McpCardinality Cardinality => McpCardinality.Instanceable;

    public IReadOnlyList<McpFieldSpec> Fields { get; } = new[]
    {
        new McpFieldSpec("host", "Host", Required: true, Default: "127.0.0.1"),
        new McpFieldSpec("port", "Port", Default: "3306"),
        new McpFieldSpec("user", "User", Required: true),
        new McpFieldSpec("password", "Password", Secret: true),
        new McpFieldSpec("database", "Database")
    };

    public McpServerConfig Build(McpInstance instance)
    {
        string Value(string key, string fallback = "")
        {
            if (instance.Values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            var spec = Fields.FirstOrDefault(f => f.Key == key);
            return spec?.Default ?? fallback;
        }

        var writesAllowed = (!instance.ReadOnly).ToString().ToLowerInvariant();
        var env = new Dictionary<string, string>
        {
            ["MYSQL_HOST"] = Value("host", "127.0.0.1"),
            ["MYSQL_PORT"] = Value("port", "3306"),
            ["MYSQL_USER"] = Value("user"),
            ["MYSQL_PASS"] = Value("password"),
            ["MYSQL_DB"] = Value("database"),
            ["ALLOW_INSERT_OPERATION"] = writesAllowed,
            ["ALLOW_UPDATE_OPERATION"] = writesAllowed,
            ["ALLOW_DELETE_OPERATION"] = writesAllowed
        };

        return new McpServerConfig(
            Id: instance.Id,
            DisplayName: instance.DisplayName,
            Transport: McpTransportKind.Stdio,
            Command: "npx",
            Args: new[] { "-y", "@benborla29/mcp-server-mysql" },
            Env: env,
            Route: McpRoute.Integrated,
            Cardinality: McpCardinality.Instanceable,
            ReadOnly: instance.ReadOnly);
    }
}
