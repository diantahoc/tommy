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
    public async Task SetSearchProvider_swaps_the_backend_live()
    {
        var fetcher = new FakeFetcher().With("https://a.dev", "A", "alpha facts");
        var orch = new WebResearchOrchestrator(fetcher, new FakeSummarizer());

        // No provider → degrade.
        Assert.False(orch.HasSearch);
        Assert.Contains("No web-search provider", await orch.ResearchAsync("goal", "x", null, 3, default));

        // Swap one in at runtime → it now searches.
        orch.SetSearchProvider(new FakeSearch(new WebSearchResult("A", "https://a.dev", "…")));
        Assert.True(orch.HasSearch);
        Assert.Contains("alpha facts", await orch.ResearchAsync("goal", "x", null, 3, default));

        // Clear it → back to degrade.
        orch.SetSearchProvider(null);
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

    // --- Summarizer budget / reasoning cut-off -----------------------------

    private sealed class ScriptedCompleter : IChatCompleter
    {
        private readonly Queue<ChatTurnResult> _results;
        public readonly List<ChatCompletionRequest> Requests = new();
        public ScriptedCompleter(params ChatTurnResult[] results) => _results = new(results);
        public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            // Last scripted result repeats once the queue is down to one.
            return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
        }
    }

    private static ChatTurnResult Empty(FinishReason reason) =>
        new("", null, reason, null, null, Array.Empty<ToolCall>());
    private static ChatTurnResult Answer(string s) =>
        new(s, null, FinishReason.Stop, null, null, Array.Empty<ToolCall>());

    [Fact]
    public async Task Summarizer_asks_for_low_reasoning_effort()
    {
        var completer = new ScriptedCompleter(Answer("distilled"));
        var summarizer = new LlmSummarizer(completer, () => "m");
        await summarizer.SummarizeAsync("goal", new WebDocument("u", "T", "body"), default);
        Assert.Equal("low", completer.Requests[0].ReasoningEffort);
    }

    [Fact]
    public async Task Summarizer_retries_with_more_budget_when_thinking_ate_the_answer()
    {
        // First call: whole budget spent thinking (empty + length). Second: a real answer.
        var completer = new ScriptedCompleter(Empty(FinishReason.Length), Answer("the summary"));
        var summarizer = new LlmSummarizer(completer, () => "m");

        var summary = await summarizer.SummarizeAsync("goal", new WebDocument("u", "T", "body"), default);

        Assert.Equal("the summary", summary);
        Assert.Equal(2, completer.Requests.Count);
        Assert.True(completer.Requests[1].MaxTokens > completer.Requests[0].MaxTokens); // budget grew
    }

    [Fact]
    public async Task Summarizer_falls_back_to_raw_page_when_it_stays_empty()
    {
        // Every call comes back empty on length — even after growing to the ceiling.
        var completer = new ScriptedCompleter(Empty(FinishReason.Length));
        var summarizer = new LlmSummarizer(completer, () => "m");

        var summary = await summarizer.SummarizeAsync("goal", new WebDocument("u", "T", "the raw page body"), default);

        Assert.Contains("the raw page body", summary);               // fetch not wasted
        Assert.DoesNotContain("summarizer returned nothing", summary);
        Assert.True(completer.Requests.Count > 1);                   // it did retry before giving up
    }

    // --- GitHub blob → raw URL normalization -------------------------------

    [Theory]
    [InlineData("https://github.com/lvgl/lv_font_conv/blob/master/doc/font_spec.md",
                "https://raw.githubusercontent.com/lvgl/lv_font_conv/master/doc/font_spec.md")]
    [InlineData("https://github.com/o/r/blob/refs/heads/main/a/b.md",
                "https://raw.githubusercontent.com/o/r/refs/heads/main/a/b.md")]
    public void NormalizeUrl_rewrites_github_blob_to_raw(string input, string expected) =>
        Assert.Equal(expected, HttpWebFetcher.NormalizeUrl(input));

    [Theory]
    [InlineData("https://raw.githubusercontent.com/o/r/master/f.md")]
    [InlineData("https://example.com/page")]
    [InlineData("https://github.com/o/r/releases")]              // not a blob page
    public void NormalizeUrl_leaves_other_urls_unchanged(string url) =>
        Assert.Equal(url, HttpWebFetcher.NormalizeUrl(url));
}
