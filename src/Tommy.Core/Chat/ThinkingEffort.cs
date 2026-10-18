namespace Tommy.Core.Chat;

/// <summary>
/// Per-chat reasoning effort. Stored abstractly (this enum) and resolved to
/// engine specifics at request time — <c>reasoning_effort</c> if the endpoint
/// accepts it, otherwise a system-prompt nudge plus a reasoning-token cap — so
/// the parameter survives a model or engine swap.
/// </summary>
public enum ThinkingEffort
{
    Off,
    Low,
    Medium,
    High
}
