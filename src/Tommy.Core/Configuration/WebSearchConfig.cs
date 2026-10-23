namespace Tommy.Core.Configuration;

/// <summary>
/// Web-search backend settings. When <see cref="SearxngUrl"/> is set, the
/// <c>web_research</c> query path goes to that SearXNG instance's JSON API; otherwise
/// Tommy falls back to the bundled web-search provider (or degrades if none is present).
/// The JS-rendering path (<c>web_fetch_js</c>) is unaffected either way.
/// </summary>
public sealed class WebSearchConfig
{
    /// <summary>
    /// Base URL of a SearXNG instance (e.g. <c>http://127.0.0.1:8082</c>), with or without a
    /// trailing <c>/search</c>. When set it overrides the managed instance. Null/blank = use the
    /// managed instance (if installed) or the bundled fallback. The instance must have the JSON
    /// output format enabled; public instances usually block it (403 / bot challenge), so point
    /// this at your own instance.
    /// </summary>
    public string? SearxngUrl { get; set; }

    /// <summary>
    /// When true and no <see cref="SearxngUrl"/> is set, web_research auto-prefers a Tommy-managed
    /// SearXNG that Tommy downloads (from <see cref="ManagedReleaseUrl"/>), builds against the
    /// bundled Python, and runs on localhost. Falls back to the bundled MCP until it is installed.
    /// </summary>
    public bool ManagedEnabled { get; set; } = true;

    /// <summary>
    /// URL of the managed-SearXNG release archive (a <c>.zip</c> of the patched source + kit). Null
    /// until the user points it at their build; managed search stays unavailable until it is set and
    /// installed.
    /// </summary>
    public string? ManagedReleaseUrl { get; set; }

    /// <summary>Localhost port the managed SearXNG binds to.</summary>
    public int ManagedPort { get; set; } = 8888;
}
