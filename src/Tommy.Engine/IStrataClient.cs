using Tommy.Core.Engine;
using Tommy.Engine.Dto;

namespace Tommy.Engine;

/// <summary>
/// Low-level client for the Strata OpenAI-compatible endpoint plus its
/// <c>/metrics</c> feed. Does not serialize requests itself — callers go through
/// <see cref="SingleModelQueue"/> (usually via <see cref="EngineService"/>).
/// </summary>
public interface IStrataClient
{
    /// <summary>Detect model capabilities (modalities, context) from /v1/models.</summary>
    Task<ModelCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>Read a live telemetry snapshot from /metrics.</summary>
    Task<EngineTelemetry> GetTelemetryAsync(CancellationToken ct = default);

    /// <summary>Check endpoint reachability and whether a model is loaded.</summary>
    Task<EngineHealth> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>Run a non-streaming completion and split out reasoning_content.</summary>
    Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default);

    /// <summary>Stream a completion as incremental reasoning/content events.</summary>
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatCompletionRequest request, CancellationToken ct = default);
}
