using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Tommy.Core.Mcp;
using Tommy.Core.Tools;
using Tommy.Mcp;

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
}
