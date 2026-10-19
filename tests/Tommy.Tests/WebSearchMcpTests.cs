using ModelContextProtocol.Protocol;
using Tommy.Core.Research;
using Tommy.Mcp;
using Tommy.Mcp.WebSearch;

namespace Tommy.Tests;

public class WebSearchMcpTests
{
    // A fake MCP invoker that returns a single JSON text block, like the real server.
    private sealed class FakeInvoker : IMcpToolInvoker
    {
        private readonly string _json;
        private readonly bool _isError;
        public string? LastTool;

        public FakeInvoker(string json, bool isError = false) { _json = json; _isError = isError; }

        public ValueTask<CallToolResult> CallAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            LastTool = toolName;
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = _isError,
                Content = new List<ContentBlock> { new TextContentBlock { Text = _json } }
            });
        }
    }

    [Fact]
    public void ParseSummaries_maps_results()
    {
        const string json = """
        {"query":"q","total_results":2,"results":[
          {"title":"First","url":"https://a.example/x","description":"about a"},
          {"title":"Second","url":"https://b.example/y","description":"about b"}
        ]}
        """;
        var hits = WebSearchParsing.ParseSummaries(json);
        Assert.Equal(2, hits.Count);
        Assert.Equal("First", hits[0].Title);
        Assert.Equal("https://a.example/x", hits[0].Url);
        Assert.Equal("about a", hits[0].Snippet);
    }

    [Fact]
    public void ParseSummaries_tolerates_garbage_and_empties()
    {
        Assert.Empty(WebSearchParsing.ParseSummaries(""));
        Assert.Empty(WebSearchParsing.ParseSummaries("not json"));
        Assert.Empty(WebSearchParsing.ParseSummaries("""{"results":[]}"""));
    }

    [Fact]
    public void ParsePage_returns_document()
    {
        const string json = """
        {"url":"https://a.example/x","title":"Title","content":"hello world","fetchStatus":"success"}
        """;
        var doc = WebSearchParsing.ParsePage("https://a.example/x", json);
        Assert.Equal("Title", doc.Title);
        Assert.Equal("hello world", doc.Text);
    }

    [Fact]
    public void ParsePage_throws_on_fetch_error()
    {
        const string json = """
        {"url":"https://a.example/x","fetchStatus":"error","error":"boom"}
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => WebSearchParsing.ParsePage("https://a.example/x", json));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Search_calls_summaries_tool_and_parses()
    {
        var fake = new FakeInvoker("""{"results":[{"title":"T","url":"https://u.example","description":"d"}]}""");
        var mcp = new WebSearchMcp(_ => Task.FromResult<IMcpToolInvoker>(fake));

        Assert.True(mcp.IsAvailable); // a connection factory means available
        var hits = await mcp.SearchAsync("q", 3, default);

        Assert.Equal("get-web-search-summaries", fake.LastTool);
        Assert.Single(hits);
        Assert.Equal("https://u.example", hits[0].Url);
    }

    [Fact]
    public async Task Render_calls_page_tool_and_parses()
    {
        var fake = new FakeInvoker("""{"url":"https://u.example","title":"T","content":"body","fetchStatus":"success"}""");
        var mcp = new WebSearchMcp(_ => Task.FromResult<IMcpToolInvoker>(fake));

        var doc = await ((IHeadlessBrowser)mcp).RenderAsync("https://u.example", default);

        Assert.Equal("get-single-web-page-content", fake.LastTool);
        Assert.Equal("body", doc.Text);
    }
}
