using Tommy.Core.Chat;

namespace Tommy.Core.Configuration;

/// <summary>
/// Global defaults a new chat starts from. Resolution order is
/// chat override → project default → these global defaults.
/// </summary>
public sealed class GlobalDefaults
{
    public int MaxTokens { get; set; } = 4096;

    public double Temperature { get; set; } = 0.7;

    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.Medium;

    /// <summary>Electricity price per kWh, for the local-power cost estimate (0 = off).</summary>
    public double ElectricityRate { get; set; } = 0.0;

    /// <summary>Currency symbol shown next to the cost estimate.</summary>
    public string Currency { get; set; } = "$";
}
