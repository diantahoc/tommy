using Tommy.Core.Chat;
using Tommy.Core.Tools;
using Tommy.Engine.Dto;

namespace Tommy.Engine.Agent;

/// <summary>Budget and behavior knobs for a run.</summary>
public sealed class AgentOptions
{
    /// <summary>Max engine calls before pausing to ask the user.</summary>
    public int MaxSteps { get; set; } = 500;

    public int MaxTokens { get; set; } = 4096;

    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// The model's context window in tokens, used to budget the prompt <b>before</b>
    /// sending so the conversation is compacted ahead of the wall instead of waiting
    /// for the engine to reject (or silently truncate) an over-long request. 0 = unknown,
    /// which disables proactive budgeting (the reactive overflow retry still applies).
    /// </summary>
    public int MaxContextTokens { get; set; }

    /// <summary>
    /// Headroom (tokens) kept free below <see cref="MaxContextTokens"/> when budgeting a
    /// prompt proactively — covers the gap between the ≈chars/4 estimate and the model's
    /// real tokenizer, plus room for the response to grow.
    /// </summary>
    public int ContextSafetyMargin { get; set; } = 1024;

    /// <summary>
    /// How many times a single answer may auto-continue past the output-token cap
    /// before the run pauses for an explicit user "continue". Each continuation is a
    /// fresh step appended to the same answer, so this bounds a runaway generation.
    /// </summary>
    public int MaxAutoContinues { get; set; } = 8;

    /// <summary>
    /// How many times to nudge the model onward after it returns an empty turn — no
    /// content and no tool calls, a premature end-of-turn that heavily quantized models
    /// fire after a tool result, quitting mid-task. Bounded so a model that genuinely has
    /// nothing to add still ends the run instead of being nudged forever.
    /// </summary>
    public int MaxEmptyNudges { get; set; } = 3;

    /// <summary>Identical consecutive tool-call sets that trip loop detection.</summary>
    public int LoopRepeatThreshold { get; set; } = 3;
}

/// <summary>
/// One run: a user message driving a sequence of engine calls until the model
/// answers in prose. Carries everything the loop needs for prompt assembly,
/// tool execution, and permission resolution.
/// </summary>
public sealed class AgentRun
{
    public required string Model { get; init; }

    public required string SystemPrompt { get; init; }

    /// <summary>Conversation history including the new user message, oldest first.</summary>
    public required List<ChatMessageDto> History { get; init; }

    public required ToolContext ToolContext { get; init; }

    public required AutonomyLevel Autonomy { get; init; }

    /// <summary>
    /// Live autonomy source, read at each permission decision so that if the user
    /// changes the level mid-run it applies to the next tool call (like
    /// <see cref="EnabledTools"/>, which is a shared mutable set). Falls back to the
    /// <see cref="Autonomy"/> snapshot when not supplied.
    /// </summary>
    public Func<AutonomyLevel>? AutonomyProvider { get; init; }

    /// <summary>The autonomy to apply right now (live if a provider was given).</summary>
    public AutonomyLevel CurrentAutonomy => AutonomyProvider?.Invoke() ?? Autonomy;

    /// <summary>Resolved reasoning effort for this run (mapped to the engine hint).</summary>
    public ThinkingEffort ThinkingEffort { get; init; } = ThinkingEffort.Off;

    /// <summary>Enabled tool names, or null for all registered tools.</summary>
    public ISet<string>? EnabledTools { get; init; }

    /// <summary>Called when a gated call needs approval; null denies all gated calls.</summary>
    public ApprovalHandler? Approve { get; init; }

    /// <summary>
    /// Reviews an Exec/Network command under the AutoReviewed level. When null the
    /// loop uses its built-in model reviewer; either way a failure escalates.
    /// </summary>
    public CommandReviewer? Reviewer { get; init; }

    /// <summary>
    /// Drains messages the user queued while the run was working; the loop appends them
    /// as user turns at the next step boundary (and before it would answer). Must be
    /// thread-safe — the loop may call it off the UI thread.
    /// </summary>
    public Func<IReadOnlyList<string>>? DrainInjected { get; init; }

    /// <summary>"Send now" interrupt: cancels just the current generation so the queued
    /// message is delivered immediately. Separate from the Stop token.</summary>
    public RunInterrupt? Interrupt { get; init; }

    /// <summary>
    /// Optional sink for streaming reasoning/content deltas as they arrive, so the UI can
    /// render the answer live. Null = no streaming (the result still arrives whole at step end).
    /// </summary>
    public Action<ChatStreamEvent>? OnToken { get; init; }

    /// <summary>
    /// Called before a step is regenerated (an output-cap or compaction retry) so the UI can
    /// drop the partial stream it showed for the discarded attempt.
    /// </summary>
    public Action? OnStreamReset { get; init; }
}
