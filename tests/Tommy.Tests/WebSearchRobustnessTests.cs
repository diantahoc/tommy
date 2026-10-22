using System.Text;
using Tommy.Core.Research;
using Tommy.Engine.Research;
using Tommy.Mcp.WebSearch;

namespace Tommy.Tests;

/// <summary>
/// Robustness of the web-search path. The parsing checks always run. The <c>Live_*</c> checks
/// are opt-in — they need a real backend and network, and no-op (pass) otherwise — so they can
/// assess whether a search backend actually returns results:
/// <code>
///   # the bundled web-search-mcp v0.3.2:
///   TOMMY_WEB_SEARCH_LIVE=1 dotnet test --filter Live_webSearchMcp
///   # a SearXNG instance (your own; public ones usually 403 the JSON API):
///   TOMMY_SEARXNG_URL=http://127.0.0.1:8082 dotnet test --filter Live_searxng
/// </code>
/// </summary>
public class WebSearchRobustnessTests
{
    // --- Parsing robustness (always on) ------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"results":null}""")]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":[{"title":"no url"}]}""")]
    [InlineData("""{"results":[{"url":""}]}""")]
    [InlineData("[1,2,3]")]
    public void WebSearchMcp_summary_parsing_never_throws_and_hits_have_urls(string json)
    {
        var hits = WebSearchParsing.ParseSummaries(json);
        Assert.NotNull(hits);
        Assert.All(hits, h => Assert.False(string.IsNullOrWhiteSpace(h.Url)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"results":null}""")]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":[{"title":"no url"}]}""")]
    public void Searxng_parsing_never_throws_and_hits_have_urls(string json)
    {
        var hits = SearxngSearchProvider.ParseResults(json, 5);
        Assert.NotNull(hits);
        Assert.All(hits, h => Assert.False(string.IsNullOrWhiteSpace(h.Url)));
    }

    // --- Live assessment (opt-in) ------------------------------------------

    private static readonly string[] Queries =
        { "wikipedia", "openai", "weather", "c# async await", "github", "news today" };

    [Fact]
    public async Task Live_webSearchMcp_returns_results_for_most_queries()
    {
        if (Environment.GetEnvironmentVariable("TOMMY_WEB_SEARCH_LIVE") != "1") return; // opt-in

        var mcp = new WebSearchMcp();
        if (!mcp.IsAvailable) return; // the bundled server/Chromium isn't present in this run
        await using var _ = mcp;

        var (withResults, report) = await Assess(mcp);
        Assert.True(withResults >= Queries.Length / 2,
            $"web-search-mcp search looks broken: {withResults}/{Queries.Length} queries returned results.\n{report}");
    }

    [Fact]
    public async Task Live_searxng_returns_results_for_most_queries()
    {
        var url = Environment.GetEnvironmentVariable("TOMMY_SEARXNG_URL");
        if (string.IsNullOrWhiteSpace(url)) return; // opt-in: point at your own SearXNG base URL

        var (withResults, report) = await Assess(new SearxngSearchProvider(url));
        Assert.True(withResults >= Queries.Length / 2,
            $"SearXNG search looks broken at {url}: {withResults}/{Queries.Length} queries returned results.\n{report}");
    }

    /// <summary>Runs every probe query and reports how many returned at least one hit.</summary>
    private static async Task<(int WithResults, string Report)> Assess(IWebSearchProvider provider)
    {
        var withResults = 0;
        var report = new StringBuilder();
        foreach (var q in Queries)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var hits = await provider.SearchAsync(q, 5, cts.Token);
                if (hits.Count > 0) withResults++;
                report.AppendLine($"  [{q}] {hits.Count} results");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  [{q}] threw: {ex.Message}");
            }
        }
        return (withResults, report.ToString());
    }
}
