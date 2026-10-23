namespace Tommy.Core.Configuration;

/// <summary>
/// Root global configuration, persisted to <c>~/.tommy/config.json</c>. Holds
/// the saved engine launch profiles, which one is active, the electricity rate
/// for the cost meter, and global defaults new chats start from.
/// </summary>
public sealed class TommyConfig
{
    /// <summary>Saved engine profiles (e.g. different quant / --ctx, or remote).</summary>
    public List<EngineProfile> EngineProfiles { get; set; } = new();

    /// <summary>Name of the active profile; falls back to the first if unset.</summary>
    public string? ActiveProfileName { get; set; }

    /// <summary>Currency per kWh, used by the electricity-cost estimate.</summary>
    public double ElectricityRatePerKwh { get; set; }

    /// <summary>Defaults new chats inherit (before project/chat overrides).</summary>
    public GlobalDefaults Defaults { get; set; } = new();

    /// <summary>User-defined MCP servers, reconnected on startup.</summary>
    public List<McpServerEntry> McpServers { get; set; } = new();

    /// <summary>Web-search backend settings (the search provider for web_research).</summary>
    public WebSearchConfig WebSearch { get; set; } = new();

    /// <summary>
    /// When true, office_extract may run LibreOffice <b>unsandboxed</b> if no sandbox backend is
    /// available (AppContainer on Windows / bwrap on Linux). Off by default: untrusted documents
    /// are only parsed inside a sandbox, and the tool refuses otherwise.
    /// </summary>
    public bool AllowUnsandboxedOffice { get; set; }

    /// <summary>The active profile, or the first available, or null.</summary>
    public EngineProfile? ActiveProfile =>
        EngineProfiles.FirstOrDefault(p => p.Name == ActiveProfileName)
        ?? EngineProfiles.FirstOrDefault();

    /// <summary>True when no engine profile exists yet — first-run setup is needed.</summary>
    public bool NeedsFirstRun => EngineProfiles.Count == 0;
}
