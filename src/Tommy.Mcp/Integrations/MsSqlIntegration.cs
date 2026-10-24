using System.Text;
using Tommy.Core.Mcp;
using Tommy.Core.Runtime;

namespace Tommy.Mcp.Integrations;

/// <summary>
/// Integration layer for the open-source Microsoft SQL Server MCP server
/// (<c>mssql-mcp-server</c> — <c>vicagbasi/mssql-mcp</c>), launched over stdio via
/// the bundled Node runtime (npx fallback). Instanceable, exactly like
/// <see cref="MySqlIntegration"/>: distinct named instances (e.g. "Reporting DB",
/// "Analytics DB") with their own configs and tools.
///
/// The server is <b>read-only by design</b> — it exposes schema / index /
/// constraint / data-pattern / stored-procedure analysis tools plus an optional
/// free-form <c>execute_query</c> (SELECT only), and has no INSERT/UPDATE/DELETE
/// path. So every instance maps to the <see cref="PermissionTier"/> Read tier
/// regardless of the instance's read-only toggle; there is no "writable" MSSQL
/// instance the way there is for MySQL.
///
/// Unlike the MySQL server (individual MYSQL_* env vars), this server is driven by
/// a SQL Server <b>connection string</b> (<c>MSSQL_CONNECTION_STRING</c>); explicit
/// per-call connection strings are disabled there for security. To keep the same
/// per-field UX as MySQL, we collect host/port/user/password/database and compose
/// the connection string here.
///
/// Alternative considered: <c>@bilims/mcp-sqlserver</c> — also env-configured
/// (SQLSERVER_* vars), read-only via a SELECT/WITH/SHOW/DESCRIBE/EXPLAIN allowlist,
/// same pure-JS <c>tedious</c> driver, more GitHub stars but a smaller toolset
/// (~10 tools) and an older MCP SDK (0.5). To switch, swap the npm package in
/// tools.manifest.json and map the SQLSERVER_* env vars instead of composing a
/// connection string. We chose vicagbasi for its far richer read-only tooling
/// (29 tools) and current MCP SDK.
/// </summary>
public sealed class MsSqlIntegration : IMcpIntegration
{
    public const string IntegrationKey = "mssql";

    /// <summary>The npm package / provisioned server folder prefix (see tools.manifest.json).</summary>
    public const string PackageName = "mssql-mcp-server";

    public string Key => IntegrationKey;
    public string DisplayName => "Microsoft SQL Server";
    public McpCardinality Cardinality => McpCardinality.Instanceable;

    public IReadOnlyList<McpFieldSpec> Fields { get; } = new[]
    {
        new McpFieldSpec("host", "Host", Required: true, Default: "127.0.0.1"),
        new McpFieldSpec("port", "Port", Default: "1433"),
        new McpFieldSpec("user", "User", Required: true),
        new McpFieldSpec("password", "Password", Secret: true),
        new McpFieldSpec("database", "Database", Default: "master"),
        // SQL Server connections commonly fail on TLS without these; the local-dev
        // friendly default is unencrypted + trust self-signed. Azure SQL needs
        // encrypt=true (and usually trust=false).
        new McpFieldSpec("encrypt", "Encrypt connection", Default: "false", Boolean: true),
        new McpFieldSpec("trust_cert", "Trust server certificate", Default: "true", Boolean: true),
        // The free-form SELECT tool is off unless enabled (the server calls it
        // "high-risk"); the structured analysis tools are always available.
        new McpFieldSpec("allow_query", "Allow custom SELECT queries", Default: "true", Boolean: true)
    };

    public McpServerConfig Build(McpInstance instance)
    {
        string Value(string key, string fallback = "")
        {
            if (instance.Values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            var spec = Fields.FirstOrDefault(f => f.Key == key);
            return spec?.Default ?? fallback;
        }

        static bool Truthy(string s) => s is "true" or "1" or "yes" or "on";

        var env = new Dictionary<string, string>
        {
            ["MSSQL_CONNECTION_STRING"] = BuildConnectionString(Value),
            // Enables the free-form execute_query (SELECT) tool; still read-only.
            ["MSSQL_ENABLE_EXECUTE_QUERY"] = Truthy(Value("allow_query")).ToString().ToLowerInvariant()
        };

        var (command, args) = ResolveLaunch();

        return new McpServerConfig(
            Id: instance.Id,
            DisplayName: instance.DisplayName,
            Transport: McpTransportKind.Stdio,
            Command: command,
            Args: args,
            Env: env,
            Route: McpRoute.Integrated,
            Cardinality: McpCardinality.Instanceable,
            // The server has no write path, so an MSSQL instance is always read-only
            // (Read tier) regardless of the instance's read-only toggle.
            ReadOnly: true);
    }

    /// <summary>
    /// Compose a SQL Server connection string from the typed fields. The server
    /// parses this by splitting on ';' and '=', so values containing those
    /// characters (e.g. a password with a ';') aren't supported — the underlying
    /// server has the same limitation.
    /// </summary>
    private static string BuildConnectionString(Func<string, string, string> value)
    {
        string V(string k, string fb = "") => value(k, fb);
        static bool Truthy(string s) => s is "true" or "1" or "yes" or "on";

        var host = V("host", "127.0.0.1");
        var port = V("port", "1433");
        var server = string.IsNullOrEmpty(port) ? host : $"{host},{port}";

        var sb = new StringBuilder();
        sb.Append("Server=").Append(server).Append(';');
        sb.Append("Database=").Append(V("database", "master")).Append(';');
        sb.Append("User Id=").Append(V("user")).Append(';');
        sb.Append("Password=").Append(V("password")).Append(';');
        sb.Append("Encrypt=").Append(Truthy(V("encrypt", "false")) ? "true" : "false").Append(';');
        sb.Append("TrustServerCertificate=").Append(Truthy(V("trust_cert", "true")) ? "true" : "false").Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Prefer the bundled Node running the provisioned server's <c>dist/index.js</c>
    /// (no system runtime, no network fetch). Fall back to <c>npx</c> so a dev
    /// machine without the provisioning step still launches the published package.
    /// Mirrors <see cref="MySqlIntegration"/>.
    /// </summary>
    private static (string Command, IReadOnlyList<string> Args) ResolveLaunch()
    {
        var node = IntegratedTools.FindNode();
        var serverDir = IntegratedTools.FindMcpServer(PackageName);
        var dist = serverDir is not null ? Path.Combine(serverDir, "dist", "index.js") : null;

        return node is not null && dist is not null && File.Exists(dist)
            ? (node, new[] { dist })
            : ("npx", new[] { "-y", PackageName });
    }
}
