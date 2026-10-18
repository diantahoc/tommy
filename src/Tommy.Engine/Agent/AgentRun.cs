using Tommy.Core.Chat;
using Tommy.Core.Tools;
using Tommy.Engine.Dto;

namespace Tommy.Engine.Agent;

/// <summary>Budget and behavior knobs for a run.</summary>
public sealed class AgentOptions
{
    /// <summary>Max engine calls before pausing to ask the user.</summary>
    public int MaxSteps { get; set; } = 25;

    public int MaxTokens { get; set; } = 2048;

    public double Temperature { get; set; } = 0.7;

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

    /// <summary>Resolved reasoning effort for this run (mapped to the engine hint).</summary>
    public ThinkingEffort ThinkingEffort { get; init; } = ThinkingEffort.Off;

    /// <summary>Enabled tool names, or null for all registered tools.</summary>
    public ISet<string>? EnabledTools { get; init; }

    /// <summary>Called when a gated call needs approval; null denies all gated calls.</summary>
    public ApprovalHandler? Approve { get; init; }
}
