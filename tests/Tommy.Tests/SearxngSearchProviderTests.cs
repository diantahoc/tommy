using System.Net;
using System.Text;
using Tommy.Engine.Research;

namespace Tommy.Tests;

/// <summary>
/// Mapping a SearXNG <c>format=json</c> response to Tommy's search hits (url/title/snippet),
/// tolerating the empty/garbage bodies a blocked or misconfigured instance returns.
/// </summary>
public class SearxngSearchProviderTests
{
    private const string Sample = """
    {"query":"openai","number_of_results":2,"results":[
      {"url":"https://openai.com/","title":"OpenAI","content":"AI research company","engine":"google"},
      {"url":"https://en.wikipedia.org/wiki/OpenAI","title":"OpenAI - Wikipedia","content":"OpenAI is…","engine":"wikipedia"}
    ]}
    """;

    [Fact]
    public void ParseResults_maps_url_title_content()
    {
        var hits = SearxngSearchProvider.ParseResults(Sample, 5);

        Assert.Equal(2, hits.Count);
        Assert.Equal("OpenAI", hits[0].Title);
        Assert.Equal("https://openai.com/", hits[0].Url);
        Assert.Equal("AI research company", hits[0].Snippet);
    }

    [Fact]
    public void ParseResults_respects_maxResults()
    {
        Assert.Single(SearxngSearchProvider.ParseResults(Sample, 1));
    }

    [Fact]
    public void ParseResults_tolerates_garbage_and_empties()
    {
        Assert.Empty(SearxngSearchProvider.ParseResults("", 5));
        Assert.Empty(SearxngSearchProvider.ParseResults("not json", 5));
        Assert.Empty(SearxngSearchProvider.ParseResults("""{"results":[]}""", 5));
        Assert.Empty(SearxngSearchProvider.ParseResults("""{"foo":1}""", 5));
    }

    [Fact]
    public void ParseResults_skips_entries_without_url_and_defaults_title()
    {
        const string json = """
        {"results":[
          {"title":"no url here"},
          {"url":"https://x.example","content":"c"}
        ]}
        """;

        var hits = SearxngSearchProvider.ParseResults(json, 5);

        Assert.Single(hits);
        Assert.Equal("https://x.example", hits[0].Url);
        Assert.Equal("https://x.example", hits[0].Title); // title falls back to the url
    }

    // --- HTTP path (fake handler, no network) ------------------------------

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly string _body;
        public string? LastUrl;
        public CannedHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task SearchAsync_requests_the_json_api_and_parses()
    {
        var handler = new CannedHandler(Sample);
        var provider = new SearxngSearchProvider("http://localhost:8088", new HttpClient(handler));

        var hits = await provider.SearchAsync("openai", 5, default);

        Assert.Equal("http://localhost:8088/search?q=openai&format=json", handler.LastUrl);
        Assert.Equal(2, hits.Count);
        Assert.Equal("https://openai.com/", hits[0].Url);
    }

    [Fact]
    public async Task SearchAsync_appends_search_path_only_once()
    {
        var handler = new CannedHandler("""{"results":[]}""");
        var provider = new SearxngSearchProvider("http://localhost:8088/search/", new HttpClient(handler));

        await provider.SearchAsync("q", 5, default);

        Assert.Equal("http://localhost:8088/search?q=q&format=json", handler.LastUrl);
    }
}
