using System.Text.Json;
using ModelContextProtocol.Protocol;
using Tommy.Core.Diagnostics;
using Tommy.Core.Mcp;
using Tommy.Core.Research;
using Tommy.Core.Runtime;

namespace Tommy.Mcp.WebSearch;

/// <summary>
/// Tightly-integrated backend for web research, built on the bundled
/// <c>web-search-mcp</c> server. It is deliberately <b>not</b> registered as a
/// generic MCP (its raw tools never reach the model): instead it satisfies the
/// harness-owned <see cref="IWebSearchProvider"/> and <see cref="IHeadlessBrowser"/>
/// seams, so search hits and JS-rendered pages still flow through the isolated
/// summarize + tagged-data boundary in <c>WebResearchOrchestrator</c>. The MCP is
/// the hands; the C# orchestration stays the containment.
///
/// The server scrapes search engines and renders pages through Playwright
/// Chromium, so it is available only once the bundled Node + server build +
/// Chromium are present (produced by build-and-copy.ps1). When they aren't,
/// <see cref="IsAvailable"/> is false and the web_research query path / web_fetch_js
/// degrade with a clear message instead of failing.
/// </summary>
public sealed class WebSearchMcp : IWebSearchProvider, IHeadlessBrowser, IAsyncDisposable
{
    private const string SummariesTool = "get-web-search-summaries";
    private const string PageContentTool = "get-single-web-page-content";

    private readonly Func<CancellationToken, Task<IMcpToolInvoker>> _connect;
    private readonly bool _available;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMcpToolInvoker? _conn;
    private IAsyncDisposable? _owned;

    /// <param name="connectionFactory">
    /// Test seam: supplies the invoker instead of launching the real subprocess.
    /// When provided, the backend is always considered available.
    /// </param>
    public WebSearchMcp(Func<CancellationToken, Task<IMcpToolInvoker>>? connectionFactory = null)
    {
        if (connectionFactory is not null)
        {
            _connect = connectionFactory;
            _available = true;
        }
        else
        {
            _connect = OpenRealAsync;
            _available = Probe();
        }
    }

    public bool IsAvailable => _available;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var json = await CallAsync(SummariesTool, new Dictionary<string, object?>
        {
            ["query"] = query,
            ["limit"] = maxResults
        }, ct).ConfigureAwait(false);

        return WebSearchParsing.ParseSummaries(json);
    }

    public async Task<WebDocument> RenderAsync(string url, CancellationToken ct)
    {
        var json = await CallAsync(PageContentTool, new Dictionary<string, object?>
        {
            ["url"] = url
        }, ct).ConfigureAwait(false);

        return WebSearchParsing.ParsePage(url, json);
    }

    private async Task<string> CallAsync(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var conn = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var result = await conn.CallAsync(tool, args, ct).ConfigureAwait(false);
        var text = ExtractText(result);
        if (result.IsError == true)
            throw new InvalidOperationException(text.Length == 0 ? $"{tool} failed" : text);
        return text;
    }

    private async Task<IMcpToolInvoker> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_conn is not null) return _conn;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _conn ??= await _connect(ct).ConfigureAwait(false);
            _owned = _conn as IAsyncDisposable;
            return _conn;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<IMcpToolInvoker> OpenRealAsync(CancellationToken ct)
    {
        var conn = await McpConnection.OpenAsync(BuildConfig(), ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>Launch config: bundled Node runs the server's dist, Playwright pointed at the bundled Chromium.</summary>
    private static McpServerConfig BuildConfig()
    {
        var node = IntegratedTools.FindNode() ?? "node";
        var serverDir = IntegratedTools.FindMcpServer("web-search-mcp")
            ?? throw new InvalidOperationException("web-search-mcp is not bundled.");
        var dist = Path.Combine(serverDir, "dist", "index.js");

        return new McpServerConfig(
            Id: "web-search",
            DisplayName: "Web Search",
            Transport: McpTransportKind.Stdio,
            Command: node,
            Args: new[] { dist },
            Env: new Dictionary<string, string>
            {
                ["PLAYWRIGHT_BROWSERS_PATH"] = IntegratedTools.Browsers(),
                ["BROWSER_HEADLESS"] = "true"
            });
    }

    private static bool Probe() =>
        IntegratedTools.FindNode() is not null
        && IntegratedTools.FindMcpServer("web-search-mcp") is { } dir
        && File.Exists(Path.Combine(dir, "dist", "index.js"))
        && IntegratedTools.HasChromium();

    private static string ExtractText(CallToolResult result)
    {
        var blocks = result.Content ?? new List<ContentBlock>();
        var parts = blocks.OfType<TextContentBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", parts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned is not null)
        {
            try { await _owned.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"web-search dispose: {ex.Message}"); }
        }
        _gate.Dispose();
    }
}

/// <summary>
/// Parses the JSON text blocks the web-search-mcp tools return into Tommy's
/// research models. Kept separate so it is unit-testable without a live browser.
/// </summary>
public static class WebSearchParsing
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<WebSearchResult> ParseSummaries(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<WebSearchResult>();

        SummaryOutput? parsed;
        try { parsed = JsonSerializer.Deserialize<SummaryOutput>(json, Options); }
        catch (JsonException) { return Array.Empty<WebSearchResult>(); }

        var results = parsed?.Results;
        if (results is null || results.Count == 0) return Array.Empty<WebSearchResult>();

        return results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .Select(r => new WebSearchResult(r.Title ?? r.Url!, r.Url!, r.Description ?? string.Empty))
            .ToList();
    }

    public static WebDocument ParsePage(string url, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("empty page content response.");

        PageOutput? parsed;
        try { parsed = JsonSerializer.Deserialize<PageOutput>(json, Options); }
        catch (JsonException ex) { throw new InvalidOperationException($"could not parse page content: {ex.Message}"); }

        if (parsed is null)
            throw new InvalidOperationException("could not parse page content.");
        if (parsed.FetchStatus == "error")
            throw new InvalidOperationException(parsed.Error ?? "page fetch failed.");

        return new WebDocument(parsed.Url ?? url, parsed.Title ?? parsed.Url ?? url, parsed.Content ?? string.Empty);
    }

    private sealed class SummaryOutput
    {
        public List<SummaryItem>? Results { get; set; }
    }

    private sealed class SummaryItem
    {
        public string? Title { get; set; }
        public string? Url { get; set; }
        public string? Description { get; set; }
    }

    private sealed class PageOutput
    {
        public string? Url { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public string? FetchStatus { get; set; }
        public string? Error { get; set; }
    }
}
