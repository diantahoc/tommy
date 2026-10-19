using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Tommy.Core.Mcp;
using Tommy.Core.Tools;
using Tommy.Mcp;
using Tommy.Mcp.Integrations;

namespace Tommy.Tests;

public class McpTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeInvoker : IMcpToolInvoker
    {
        private readonly Func<CallToolResult> _fn;
        public string? LastTool;
        public IReadOnlyDictionary<string, object?>? LastArgs;

        public FakeInvoker(Func<CallToolResult> fn) => _fn = fn;

        public ValueTask<CallToolResult> CallAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            LastTool = toolName;
            LastArgs = arguments;
            return new ValueTask<CallToolResult>(_fn());
        }
    }

    private static CallToolResult Text(string text, bool error = false) =>
        new() { Content = new List<ContentBlock> { new TextContentBlock { Text = text } }, IsError = error };

    private static McpServerConfig Config(string id, McpRoute route = McpRoute.Plain, bool readOnly = false) =>
        new(id, id, McpTransportKind.Stdio, Command: "noop", Route: route, ReadOnly: readOnly);

    // --- Name mapping -------------------------------------------------------

    [Fact]
    public void AdvertisedName_prefixes_with_sanitized_instance_id()
    {
        var name = McpToolMapping.AdvertisedName(Config("Production DB"), "show_tables");
        Assert.Equal("production_db_show_tables", name);
    }

    [Fact]
    public void AdvertisedName_keeps_two_instances_distinct()
    {
        var prod = McpToolMapping.AdvertisedName(Config("prod"), "query");
        var test = McpToolMapping.AdvertisedName(Config("test"), "query");
        Assert.NotEqual(prod, test);
    }

    // --- Tier policy --------------------------------------------------------

    [Fact]
    public void Resolve_integrated_readonly_is_read_tier_no_override()
    {
        var (tier, ov) = McpToolMapping.Resolve(Config("db", McpRoute.Integrated, readOnly: true));
        Assert.Equal(PermissionTier.Read, tier);
        Assert.Null(ov);
    }

    [Fact]
    public void Resolve_integrated_readwrite_is_write_tier_no_override()
    {
        var (tier, ov) = McpToolMapping.Resolve(Config("db", McpRoute.Integrated, readOnly: false));
        Assert.Equal(PermissionTier.Write, tier);
        Assert.Null(ov);
    }

    [Fact]
    public void Resolve_plain_server_is_conservative_always_ask()
    {
        var (_, ov) = McpToolMapping.Resolve(Config("misc", McpRoute.Plain));
        Assert.Equal(PermissionAction.Ask, ov);
        // And the grid honours that override even under full autonomy.
        Assert.Equal(PermissionAction.Ask, PermissionGrid.Resolve(PermissionTier.Write, AutonomyLevel.Auto, ov));
    }

    // --- Schema -------------------------------------------------------------

    [Fact]
    public void Schema_passes_through_object_schema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""");
        var schema = McpToolMapping.Schema(doc.RootElement);
        Assert.Equal("object", (string?)schema["type"]);
        Assert.NotNull(schema["properties"]);
    }

    [Fact]
    public void Schema_falls_back_to_empty_object_when_missing()
    {
        var schema = McpToolMapping.Schema(null);
        Assert.Equal("object", (string?)schema["type"]);
    }

    // --- Proxy execution ----------------------------------------------------

    private static McpToolProxy Proxy(IMcpToolInvoker invoker, string remote = "query") =>
        new("db_query", remote, "run a query", new JsonObject { ["type"] = "object" },
            PermissionTier.Write, PermissionAction.Ask, invoker);

    [Fact]
    public async Task Execute_maps_text_content_to_success()
    {
        var proxy = Proxy(new FakeInvoker(() => Text("42 rows")));
        var result = await proxy.ExecuteAsync(Args("{}"), new ToolContext("."), default);
        Assert.True(result.Success);
        Assert.Equal("42 rows", result.Content);
    }

    [Fact]
    public async Task Execute_maps_error_result_to_failure()
    {
        var proxy = Proxy(new FakeInvoker(() => Text("table not found", error: true)));
        var result = await proxy.ExecuteAsync(Args("{}"), new ToolContext("."), default);
        Assert.False(result.Success);
        Assert.Equal(ToolResultKind.Error, result.Kind);
        Assert.Contains("table not found", result.Content);
    }

    [Fact]
    public async Task Execute_catches_transport_exception()
    {
        var proxy = Proxy(new FakeInvoker(() => throw new IOException("pipe closed")));
        var result = await proxy.ExecuteAsync(Args("{}"), new ToolContext("."), default);
        Assert.False(result.Success);
        Assert.Contains("pipe closed", result.Content);
    }

    [Fact]
    public async Task Execute_forwards_remote_name_and_arguments()
    {
        var invoker = new FakeInvoker(() => Text("ok"));
        var proxy = Proxy(invoker, remote: "list_tables");
        await proxy.ExecuteAsync(Args("""{"schema":"public"}"""), new ToolContext("."), default);

        Assert.Equal("list_tables", invoker.LastTool);
        Assert.True(invoker.LastArgs!.ContainsKey("schema"));
        Assert.Equal("public", ((JsonElement)invoker.LastArgs!["schema"]!).GetString());
    }

    [Fact]
    public void Proxy_exposes_tier_override_and_schema()
    {
        var proxy = Proxy(new FakeInvoker(() => Text("")));
        Assert.Equal("db_query", proxy.Name);
        Assert.Equal(PermissionTier.Write, proxy.Tier);
        Assert.Equal(PermissionAction.Ask, proxy.ApprovalOverride);
        Assert.Equal("object", (string?)proxy.ParameterSchema["type"]);
    }

    // --- Registry lifecycle -------------------------------------------------

    [Fact]
    public void Unregister_removes_tool_from_registry()
    {
        var registry = new ToolRegistry();
        var proxy = Proxy(new FakeInvoker(() => Text("")));
        registry.Register(proxy);
        Assert.NotNull(registry.Find("db_query"));

        Assert.True(registry.Unregister("db_query"));
        Assert.Null(registry.Find("db_query"));
        Assert.False(registry.Unregister("db_query"));
    }

    // --- MySQL integration layer (4B) --------------------------------------

    private static McpInstance MySqlInstance(bool readOnly, params (string, string)[] values) =>
        new(MySqlIntegration.IntegrationKey, readOnly ? "prod" : "test", readOnly ? "Production DB" : "Test DB",
            values.ToDictionary(v => v.Item1, v => v.Item2), readOnly);

    [Fact]
    public void MySql_maps_fields_to_environment()
    {
        var cfg = new MySqlIntegration().Build(MySqlInstance(readOnly: true,
            ("host", "db.internal"), ("port", "3307"), ("user", "reader"), ("password", "secret"), ("database", "shop")));

        Assert.Equal(McpTransportKind.Stdio, cfg.Transport);
        // Launch targets the MySQL server — the bundled dist/index.js when built,
        // else the npx fallback; both name the server.
        Assert.Contains("mcp-server-mysql", cfg.Command + " " + string.Join(' ', cfg.Args!));
        Assert.Equal("db.internal", cfg.Env!["MYSQL_HOST"]);
        Assert.Equal("3307", cfg.Env["MYSQL_PORT"]);
        Assert.Equal("reader", cfg.Env["MYSQL_USER"]);
        Assert.Equal("secret", cfg.Env["MYSQL_PASS"]);
        Assert.Equal("shop", cfg.Env["MYSQL_DB"]);
    }

    [Fact]
    public void MySql_readonly_disables_write_operations()
    {
        var ro = new MySqlIntegration().Build(MySqlInstance(readOnly: true, ("user", "u")));
        Assert.Equal("false", ro.Env!["ALLOW_INSERT_OPERATION"]);
        Assert.Equal("false", ro.Env["ALLOW_UPDATE_OPERATION"]);
        Assert.Equal("false", ro.Env["ALLOW_DELETE_OPERATION"]);
        Assert.True(ro.ReadOnly);

        var rw = new MySqlIntegration().Build(MySqlInstance(readOnly: false, ("user", "u")));
        Assert.Equal("true", rw.Env!["ALLOW_INSERT_OPERATION"]);
        Assert.False(rw.ReadOnly);
    }

    [Fact]
    public void MySql_applies_field_defaults()
    {
        var cfg = new MySqlIntegration().Build(MySqlInstance(readOnly: true, ("user", "u")));
        Assert.Equal("127.0.0.1", cfg.Env!["MYSQL_HOST"]);
        Assert.Equal("3306", cfg.Env["MYSQL_PORT"]);
    }

    [Fact]
    public void MySql_ddl_requires_write_and_optin()
    {
        // Read-only: DDL stays off even if the flag is set.
        var ro = new MySqlIntegration().Build(MySqlInstance(readOnly: true, ("user", "u"), ("allow_ddl", "true")));
        Assert.Equal("false", ro.Env!["ALLOW_DDL_OPERATION"]);

        // Writable but no opt-in: DDL off.
        var rwOff = new MySqlIntegration().Build(MySqlInstance(readOnly: false, ("user", "u")));
        Assert.Equal("false", rwOff.Env!["ALLOW_DDL_OPERATION"]);

        // Writable and opted in: DDL on.
        var rwOn = new MySqlIntegration().Build(MySqlInstance(readOnly: false, ("user", "u"), ("allow_ddl", "true")));
        Assert.Equal("true", rwOn.Env!["ALLOW_DDL_OPERATION"]);
    }

    [Fact]
    public void MySql_password_field_is_secret()
    {
        var password = new MySqlIntegration().Fields.Single(f => f.Key == "password");
        Assert.True(password.Secret);
    }

    [Fact]
    public void Integration_tier_follows_read_only_flag()
    {
        var integration = new MySqlIntegration();
        var ro = McpToolMapping.Resolve(integration.Build(MySqlInstance(readOnly: true, ("user", "u"))));
        Assert.Equal(PermissionTier.Read, ro.Tier);
        Assert.Null(ro.Override);

        var rw = McpToolMapping.Resolve(integration.Build(MySqlInstance(readOnly: false, ("user", "u"))));
        Assert.Equal(PermissionTier.Write, rw.Tier);
    }

    [Fact]
    public void Registry_resolves_known_and_rejects_unknown()
    {
        var registry = new McpIntegrationRegistry();
        Assert.NotNull(registry.Find("mysql"));
        Assert.Null(registry.Find("nope"));
        Assert.NotNull(registry.BuildConfig(MySqlInstance(readOnly: true, ("user", "u"))));
        Assert.Null(registry.BuildConfig(new McpInstance("unknown", "x", "X", new Dictionary<string, string>())));
    }

    [Fact]
    public void Two_instances_get_distinct_tool_names()
    {
        var registry = new McpIntegrationRegistry();
        var prod = registry.BuildConfig(MySqlInstance(readOnly: true, ("user", "u")))!;
        var test = registry.BuildConfig(MySqlInstance(readOnly: false, ("user", "u")))!;
        Assert.NotEqual(
            McpToolMapping.AdvertisedName(prod, "query"),
            McpToolMapping.AdvertisedName(test, "query"));
    }
}
