using ModelContextProtocol.Protocol;

namespace Tommy.Mcp;

/// <summary>
/// The one operation a <see cref="McpToolProxy"/> needs from a live connection:
/// call a remote tool by its server-side name. Abstracting it keeps the proxy's
/// argument/result mapping unit-testable without a real subprocess.
/// </summary>
public interface IMcpToolInvoker
{
    ValueTask<CallToolResult> CallAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct);
}
