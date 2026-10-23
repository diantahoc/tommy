namespace Tommy.Core.Configuration;

/// <summary>
/// Web-search backend settings. When <see cref="SearxngUrl"/> is set, the
/// <c>web_research</c> query path goes to that SearXNG instance's JSON API; otherwise
/// Tommy uses the managed SearXNG it provisions (when installed), and degrades if none is
/// present. The JS-rendering path (<c>web_fetch_js</c>) is unaffected either way.
/// </summary>
public sealed class WebSearchConfig
{
    /// <summary>
    /// Base URL of a SearXNG instance (e.g. <c>http://127.0.0.1:8082</c>), with or without a
    /// trailing <c>/search</c>. When set it overrides the managed instance. Null/blank = use the
    /// managed instance (if installed). The instance must have the JSON output format enabled;
    /// public instances usually block it (403 / bot challenge), so point this at your own instance.
    /// </summary>
    public string? SearxngUrl { get; set; }

    /// <summary>
    /// When true and no <see cref="SearxngUrl"/> is set, web_research auto-prefers the Tommy-managed
    /// SearXNG — provisioned at startup (cloned at the manifest's pinned commit, built against the
    /// bundled Python, run on localhost). web_research degrades until it is installed.
    /// </summary>
    public bool ManagedEnabled { get; set; } = true;

    /// <summary>Localhost port the managed SearXNG binds to.</summary>
    public int ManagedPort { get; set; } = 8888;
}
