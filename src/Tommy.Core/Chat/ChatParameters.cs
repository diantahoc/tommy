using Tommy.Core.Configuration;
using Tommy.Core.Tools;

namespace Tommy.Core.Chat;

/// <summary>
/// Per-chat or per-project parameter overrides. Any null value falls through to
/// the next level in the resolution order (chat → project → global default).
/// </summary>
public sealed class ChatParameters
{
    public string? SystemPrompt { get; set; }
    public AutonomyLevel? Autonomy { get; set; }
    public ThinkingEffort? ThinkingEffort { get; set; }
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }

    /// <summary>Enabled tool names; null means "all registered tools".</summary>
    public HashSet<string>? EnabledTools { get; set; }
}

/// <summary>Fully resolved parameters a run uses.</summary>
public sealed record ResolvedParameters(
    string SystemPrompt,
    AutonomyLevel Autonomy,
    ThinkingEffort ThinkingEffort,
    int MaxTokens,
    double Temperature,
    HashSet<string>? EnabledTools);

/// <summary>
/// Resolves parameters in the settled order chat override → project default →
/// global default.
/// </summary>
public static class ParameterResolver
{
    public static ResolvedParameters Resolve(
        ChatParameters? chat,
        ChatParameters? project,
        GlobalDefaults global)
    {
        return new ResolvedParameters(
            SystemPrompt: chat?.SystemPrompt ?? project?.SystemPrompt ?? ChatDefaults.SystemPrompt,
            Autonomy: chat?.Autonomy ?? project?.Autonomy ?? AutonomyLevel.ReviewEach,
            ThinkingEffort: chat?.ThinkingEffort ?? project?.ThinkingEffort ?? global.ThinkingEffort,
            MaxTokens: chat?.MaxTokens ?? project?.MaxTokens ?? global.MaxTokens,
            Temperature: chat?.Temperature ?? project?.Temperature ?? global.Temperature,
            EnabledTools: chat?.EnabledTools ?? project?.EnabledTools);
    }
}
