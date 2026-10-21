using Tommy.Core.Tools;
using Tommy.Engine.Dto;

namespace Tommy.Engine.Agent;

/// <summary>Per-response stats surfaced to the UI.</summary>
public sealed record ChatStats(double? TokensPerSecond, double? TtftMs, int? CompletionTokens, int? TotalTokens);

/// <summary>Why a run ended.</summary>
public enum RunEndReason
{
    /// <summary>Model answered in prose.</summary>
    Answered,

    /// <summary>Hit the step/token budget — pause and ask the user.</summary>
    Budget,

    /// <summary>Repeated identical tool calls — early pause.</summary>
    LoopDetected,

    /// <summary>A user declined a required approval and the model couldn't proceed.</summary>
    Stopped,

    /// <summary>An unrecoverable error.</summary>
    Error
}

/// <summary>Events the agent loop emits as it runs, for the UI to render.</summary>
public abstract record AgentEvent;

/// <summary>
/// A model step: its prose content, reasoning (reasoning is display-only), and any
/// tool calls it requested. The tool calls are recorded on the turn so a paused run
/// can be reconstructed from the event log.
/// </summary>
public sealed record AssistantStep(
    string Content, string? Reasoning, ChatStats? Stats, IReadOnlyList<ToolCall> ToolCalls) : AgentEvent;

/// <summary>A tool call is about to run (after any approval), with an optional preview.</summary>
public sealed record ToolStarted(string CallId, string ToolName, string ArgumentsJson, PermissionTier Tier, ToolPreview? Preview) : AgentEvent;

/// <summary>A tool call finished with a result.</summary>
public sealed record ToolFinished(string CallId, string ToolName, ToolResult Result) : AgentEvent;

/// <summary>
/// The in-run context was compacted (older tool output collapsed) because the
/// prompt had outgrown the model's context window; the run then continued.
/// </summary>
public sealed record ContextCompacted(string? Note = null) : AgentEvent;

/// <summary>
/// The model's answer was cut off at the output-token cap and the run auto-continued
/// it in place — a fresh step appended to the same answer — so the user needn't type
/// "continue". <paramref name="Attempt"/> is the 1-based continuation count.
/// </summary>
public sealed record AutoContinued(int Attempt) : AgentEvent;

/// <summary>The run ended.</summary>
public sealed record RunFinished(RunEndReason Reason, string? Note = null) : AgentEvent;

/// <summary>A request for the user to approve (or deny) a gated tool call.</summary>
public sealed record ApprovalRequest(string CallId, string ToolName, string ArgumentsJson, PermissionTier Tier, ToolPreview? Preview);

/// <summary>
/// The user's answer to an approval prompt. A denial carries an optional reason
/// (typed in the deny box) that is fed back to the model and shown to the user.
/// </summary>
public sealed record ApprovalDecision(bool Approved, string? Reason = null);

/// <summary>Handles approval prompts; returns the user's allow/deny decision.</summary>
public delegate Task<ApprovalDecision> ApprovalHandler(ApprovalRequest request, CancellationToken ct);
