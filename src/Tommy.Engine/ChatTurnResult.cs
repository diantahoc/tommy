using Tommy.Engine.Dto;

namespace Tommy.Engine;

/// <summary>Normalized finish reason, mapped from the wire string.</summary>
public enum FinishReason
{
    /// <summary>Model answered in prose — the run can end.</summary>
    Stop,

    /// <summary>Model requested tool calls — continue the loop.</summary>
    ToolCalls,

    /// <summary>Hit the token budget. With empty content this is recoverable.</summary>
    Length,

    /// <summary>
    /// The engine ended the response without reporting a terminal reason — the stream
    /// closed before a <c>finish_reason</c> arrived (the context ceiling was hit
    /// mid-generation, or the connection dropped). Whatever content arrived is a
    /// partial fragment, not a finished answer, and must be surfaced as a cut-off
    /// rather than passed off as the model's answer.
    /// </summary>
    Incomplete,

    /// <summary>Anything else / unrecognized.</summary>
    Other
}

/// <summary>
/// A completed (non-streaming) engine response with <c>reasoning_content</c>
/// already split out from the answer. The reasoning is kept for display only
/// and must never be fed back into the next turn's context.
/// </summary>
/// <summary>A tool call the model requested, normalized from the wire DTO.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatTurnResult(
    string Content,
    string? Reasoning,
    FinishReason FinishReason,
    UsageDto? Usage,
    TimingsDto? Timings,
    IReadOnlyList<ToolCall> ToolCalls)
{
    /// <summary>
    /// The model spent its whole budget thinking and emitted no answer — a
    /// recoverable error to retry with more budget / lower reasoning.
    /// </summary>
    public bool IsRecoverableLength =>
        FinishReason == FinishReason.Length && string.IsNullOrEmpty(Content);

    public static FinishReason MapFinishReason(string? wire) => wire switch
    {
        "stop" => FinishReason.Stop,
        "tool_calls" => FinishReason.ToolCalls,
        "length" => FinishReason.Length,
        // No reason at all means the response ended without the engine saying why — an
        // early/abnormal close, not a clean stop.
        null => FinishReason.Incomplete,
        _ => FinishReason.Other
    };
}

/// <summary>Kind of incremental event emitted while streaming a response.</summary>
public enum ChatStreamEventKind
{
    /// <summary>A chunk of <c>reasoning_content</c> (route to the thinking bubble).</summary>
    ReasoningDelta,

    /// <summary>A chunk of answer <c>content</c>.</summary>
    ContentDelta,

    /// <summary>Terminal event carrying the finish reason and usage.</summary>
    Completed
}

/// <summary>
/// One event in a streamed response. <see cref="ToolCalls"/> is set only on the terminal
/// <see cref="ChatStreamEventKind.Completed"/> event, carrying the tool calls assembled
/// from the streamed deltas.
/// </summary>
public sealed record ChatStreamEvent(
    ChatStreamEventKind Kind,
    string? Text = null,
    FinishReason FinishReason = FinishReason.Other,
    UsageDto? Usage = null,
    TimingsDto? Timings = null,
    IReadOnlyList<ToolCall>? ToolCalls = null);
