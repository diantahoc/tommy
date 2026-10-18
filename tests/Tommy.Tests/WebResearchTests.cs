using System.Text.Json;
using Tommy.Core.Research;
using Tommy.Core.Tools;
using Tommy.Engine;
using Tommy.Engine.Dto;
using Tommy.Engine.Research;

namespace Tommy.Tests;

public class WebResearchTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly ToolContext Ctx = new(".");

    private sealed class FakeFetcher : IWebFetcher
    {
        private readonly Dictionary<string, WebDocument> _docs = new();
        public FakeFetcher With(string url, string title, string text) { _docs[url] = new WebDocument(url, title, text); return this; }
        public Task<WebDocument> FetchAsync(string url, CancellationToken ct) =>
            _docs.TryGetValue(url, out var d) ? Task.FromResult(d) : throw new HttpRequestException("404");
    }

    private sealed class FakeSummarizer : ISummarizer
    {
        public string? LastGoal;
        public Task<string> SummarizeAsync(string goal, WebDocument document, CancellationToken ct)
        {
            LastGoal = goal;
            return Task.FromResult($"[distilled for '{goal}'] {document.Text}");
        }
    }

    private sealed class FakeSearch : IWebSearchProvider
    {
        private readonly WebSearchResult[] _results;
        public FakeSearch(params WebSearchResult[] results) => _results = results;
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WebSearchResult>>(_results.Take(maxResults).ToList());
    }

    // --- Orchestrator -------------------------------------------------------

    [Fact]
    public async Task Research_url_fetches_distills_and_tags()
    {
        var fetcher = new FakeFetcher().With("https://z.dev/doc", "Z docs", "parseConfig(path) -> Config");
        var orch = new WebResearchOrchestrator(fetcher, new FakeSummarizer());

        var text = await orch.ResearchAsync("signature of parseConfig", query: null, url: "https://z.dev/doc", 3, default);

        Assert.Contains("WEB DATA", text);                 // tagged as untrusted
        Assert.Contains("parseConfig(path)", text);        // distilled content
        Assert.Contains("https://z.dev/doc", text);        // source retained
    }

    [Fact]
    public async Task Research_query_searches_and_lists_sources()
    {
        var fetcher = new FakeFetcher()
            .With("https://a.dev", "A", "alpha facts")
            .With("https://b.dev", "B", "beta facts");
        var search = new FakeSearch(
            new WebSearchResult("A", "https://a.dev", "..."),
            new WebSearchResult("B", "https://b.dev", "..."));
        var orch = new WebResearchOrchestrator(fetcher, new FakeSummarizer(), search);

        var text = await orch.ResearchAsync("goal", query: "x", url: null, 5, default);

        Assert.Contains("alpha facts", text);
        Assert.Contains("beta facts", text);
        Assert.Contains("https://a.dev", text);
        Assert.Contains("https://b.dev", text);
    }

    [Fact]
    public async Task Research_query_without_provider_degrades()
    {
        var orch = new WebResearchOrchestrator(new FakeFetcher(), new FakeSummarizer());
        var text = await orch.ResearchAsync("goal", query: "x", url: null, 3, default);
        Assert.Contains("No web-search provider", text);
        Assert.False(orch.HasSearch);
    }

    [Fact]
    public async Task Research_without_url_or_query_explains()
    {
        var orch = new WebResearchOrchestrator(new FakeFetcher(), new FakeSummarizer());
        var text = await orch.ResearchAsync("goal", null, null, 3, default);
        Assert.Contains("needs either a url or a query", text);
    }

    [Fact]
    public async Task Research_url_fetch_failure_reports_cleanly()
    {
        var orch = new WebResearchOrchestrator(new FakeFetcher(), new FakeSummarizer());
        var text = await orch.ResearchAsync("goal", null, "https://missing.dev", 3, default);
        Assert.Contains("could not fetch", text);
    }

    // --- Tools --------------------------------------------------------------

    [Fact]
    public async Task WebFetch_tool_wraps_result_as_data()
    {
        var fetcher = new FakeFetcher().With("https://p.dev", "P", "hello page");
        var tool = new WebFetchTool(fetcher, new FakeSummarizer());
        var result = await tool.ExecuteAsync(Args("""{"url":"https://p.dev"}"""), Ctx, default);
        Assert.True(result.Success);
        Assert.Contains("WEB DATA", result.Content);
        Assert.Contains("hello page", result.Content);
    }

    [Fact]
    public async Task WebFetch_tool_distills_when_goal_given()
    {
        var fetcher = new FakeFetcher().With("https://p.dev", "P", "hello page");
        var summarizer = new FakeSummarizer();
        var tool = new WebFetchTool(fetcher, summarizer);
        await tool.ExecuteAsync(Args("""{"url":"https://p.dev","goal":"greeting"}"""), Ctx, default);
        Assert.Equal("greeting", summarizer.LastGoal);
    }

    [Fact]
    public async Task WebFetch_requires_url()
    {
        var tool = new WebFetchTool(new FakeFetcher(), new FakeSummarizer());
        var result = await tool.ExecuteAsync(Args("{}"), Ctx, default);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task WebResearch_requires_goal()
    {
        var tool = new WebResearchTool(new WebResearchOrchestrator(new FakeFetcher(), new FakeSummarizer()));
        var result = await tool.ExecuteAsync(Args("""{"query":"x"}"""), Ctx, default);
        Assert.False(result.Success);
    }

    [Fact]
    public void Web_tools_are_network_tier_and_web_safe()
    {
        Assert.Equal(PermissionTier.Network, new WebFetchTool(new FakeFetcher(), new FakeSummarizer()).Tier);
        Assert.Equal(PermissionTier.Network, new WebResearchTool(new WebResearchOrchestrator(new FakeFetcher(), new FakeSummarizer())).Tier);
        Assert.True(WebCapabilities.IsAllowed("web_fetch"));
        Assert.True(WebCapabilities.IsAllowed("web_research") is false); // only fetch/search/browser cross the boundary
    }

    // --- HTML reduction -----------------------------------------------------

    [Fact]
    public void HtmlToText_strips_markup_and_scripts()
    {
        const string html = "<html><head><title>T</title><style>.a{}</style></head>" +
                            "<body><script>evil()</script><p>Hello&nbsp;&amp; welcome</p><p>Line</p></body></html>";
        var text = HttpWebFetcher.HtmlToText(html);
        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("<p>", text);
        Assert.Contains("Hello", text);
        Assert.Contains("& welcome", text);  // entity decoded
    }

    // --- Summarizer isolation ----------------------------------------------

    private sealed class CapturingCompleter : IChatCompleter
    {
        public ChatCompletionRequest? Last;
        public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new ChatTurnResult("distilled", null, FinishReason.Stop, null, null, Array.Empty<ToolCall>()));
        }
    }

    [Fact]
    public async Task Summarizer_runs_with_no_tools()
    {
        var completer = new CapturingCompleter();
        var summarizer = new LlmSummarizer(completer, () => "test-model");
        var summary = await summarizer.SummarizeAsync("goal", new WebDocument("https://u", "T", "body"), default);

        Assert.Equal("distilled", summary);
        Assert.Null(completer.Last!.Tools);          // isolation: no tools array
        Assert.Null(completer.Last!.ToolChoice);
        Assert.Equal("test-model", completer.Last!.Model);
    }
}
