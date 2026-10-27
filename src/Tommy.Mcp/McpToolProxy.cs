using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Tommy.Core.Tools;

namespace Tommy.Mcp;

/// <summary>
/// Adapts one remote MCP tool to Tommy's <see cref="ITool"/> so native and MCP
/// tools reach the model through the same registry and the same permission grid.
/// </summary>
public sealed class McpToolProxy : ITool
{
    private readonly IMcpToolInvoker _invoker;
    private readonly string _remoteName;

    public McpToolProxy(
        string name,
        string remoteName,
        string description,
        JsonObject parameterSchema,
        PermissionTier tier,
        PermissionAction? approvalOverride,
        IMcpToolInvoker invoker)
    {
        Name = name;
        _remoteName = remoteName;
        Description = description;
        ParameterSchema = parameterSchema;
        Tier = tier;
        ApprovalOverride = approvalOverride;
        _invoker = invoker;
    }

    public string Name { get; }
    public string Description { get; }
    public PermissionTier Tier { get; }
    public PermissionAction? ApprovalOverride { get; }
    public bool IsExternal => true; // an MCP server's tool — opt-in per chat
    public JsonObject ParameterSchema { get; }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await _invoker.CallAsync(_remoteName, ToArgs(arguments), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"MCP call '{_remoteName}' failed: {ex.Message}");
        }

        var text = ExtractText(result);
        return result.IsError == true ? ToolResult.Fail(text) : ToolResult.Ok(text);
    }

    /// <summary>Flatten the model's JSON argument object into the SDK's argument map.</summary>
    private static IReadOnlyDictionary<string, object?> ToArgs(JsonElement arguments)
    {
        var dict = new Dictionary<string, object?>();
        if (arguments.ValueKind == JsonValueKind.Object)
            foreach (var p in arguments.EnumerateObject())
                dict[p.Name] = p.Value; // boxed JsonElement; the SDK serializes each value
        return dict;
    }

    /// <summary>Collapse a tool result to text; describe non-text blocks rather than dumping bytes.</summary>
    private static string ExtractText(CallToolResult result)
    {
        var blocks = result.Content ?? new List<ContentBlock>();
        var parts = blocks.OfType<TextContentBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        if (parts.Count > 0) return string.Join("\n", parts);

        var kinds = blocks.Select(b => b.Type).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        return kinds.Count > 0 ? $"[{string.Join(", ", kinds)} content]" : "(no content)";
    }
}
