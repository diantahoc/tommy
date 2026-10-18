using Tommy.Core.Chat;

namespace Tommy.Core.Configuration;

/// <summary>
/// Global defaults a new chat starts from. Resolution order is
/// chat override → project default → these global defaults.
/// </summary>
public sealed class GlobalDefaults
{
    public int MaxTokens { get; set; } = 2048;

    public double Temperature { get; set; } = 0.7;

    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.Medium;
}
