using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tommy.Core.Tools;

/// <summary>
/// Execution context handed to a tool per call: the working directory it
/// operates relative to, whether the location is read-only (a chat-section
/// attached folder registers at the read tier only), and the memory directories
/// this chat draws from (workspace/project) plus where a memory_write lands.
/// </summary>
public sealed record ToolContext(
    string WorkingDirectory,
    bool ReadOnly = false,
    IReadOnlyList<string>? MemoryDirs = null,
    string? WriteMemoryDir = null,
    bool VisionEnabled = false,
    Func<UserQuestion, CancellationToken, Task<string>>? AskUser = null);

/// <summary>
/// A question the agent puts to the user via the <c>ask_question</c> tool: the
/// prompt, a set of offered answers, and whether the user may type their own.
/// </summary>
public sealed record UserQuestion(string Question, IReadOnlyList<string> Options, bool AllowCustom = true);

/// <summary>How a tool result should be rendered.</summary>
public enum ToolResultKind
{
    Text,
    Diff,
    Json,
    Error
}

/// <summary>
/// Result of running a tool. <see cref="Content"/> is threaded back to the model
/// as a <c>role=tool</c> message; <see cref="Kind"/> and <see cref="Title"/> drive
/// how the UI renders it (a diff as a diff, JSON as a tree, etc.).
/// </summary>
public sealed record ToolResult(
    bool Success,
    string Content,
    ToolResultKind Kind = ToolResultKind.Text,
    string? Title = null)
{
    public static ToolResult Ok(string content, ToolResultKind kind = ToolResultKind.Text, string? title = null) =>
        new(true, content, kind, title);

    public static ToolResult Fail(string error) =>
        new(false, error, ToolResultKind.Error);
}

/// <summary>
/// A preview of what a tool would do, shown at approval time. Write tools return
/// a diff so a <c>write</c> approval shows the actual change, not a bare "allow?".
/// </summary>
public sealed record ToolPreview(string Summary, string? Diff = null);

/// <summary>
/// A first-class typed tool on the native function-calling path. Implementations
/// are stateless singletons; per-call state comes via <see cref="ToolContext"/>.
/// </summary>
public interface ITool
{
    /// <summary>Unique tool name advertised to the model.</summary>
    string Name { get; }

    string Description { get; }

    PermissionTier Tier { get; }

    /// <summary>
    /// Forces a specific approval action regardless of the chat's autonomy level.
    /// Plain-client / user MCP tools use this to stay <c>Ask</c> always, since the
    /// harness can't know their side effects. Default: none (follow the grid).
    /// </summary>
    PermissionAction? ApprovalOverride => null;

    /// <summary>
    /// Alternate names the model might use for this tool. Resolved by the registry
    /// but not advertised in the tools array. Default: none.
    /// </summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    /// <summary>JSON Schema object for the tool's parameters.</summary>
    JsonObject ParameterSchema { get; }

    /// <summary>Run the tool with the given JSON arguments.</summary>
    Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct);

    /// <summary>
    /// Optional preview (e.g. a diff) shown before a gated call runs. Default: none.
    /// </summary>
    Task<ToolPreview?> PreviewAsync(JsonElement arguments, ToolContext context, CancellationToken ct) =>
        Task.FromResult<ToolPreview?>(null);
}

/// <summary>
/// Marks an <see cref="ITool"/> implementation for auto-discovery by the registry.
/// Adding a tool is one new class, no central edits.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolAttribute : Attribute
{
}
