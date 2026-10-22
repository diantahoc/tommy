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
    /// trailing <c>/search</c>. Null/blank = use the fallback provider. The instance must have
    /// the JSON output format enabled; public instances usually block it (403 / bot challenge),
    /// so point this at your own instance.
    /// </summary>
    public string? SearxngUrl { get; set; }
}
