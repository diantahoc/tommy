using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tommy.Core.Research;

namespace Tommy.Engine.Research;

/// <summary>
/// Queries a SearXNG instance's JSON API directly (<c>GET {base}/search?q=…&amp;format=json</c>)
/// and maps its results to Tommy's search hits — no subprocess, just HTTP. The hits still flow
/// through the isolated summarize + tagged-data boundary in <see cref="WebResearchOrchestrator"/>
/// like any other provider.
///
/// The instance must have the JSON output format enabled (<c>search.formats</c> includes
/// <c>json</c> in its <c>settings.yml</c>). Public instances usually block the JSON API with a
/// 403 or a bot challenge, so this is meant for a self-hosted or user-provided instance.
/// </summary>
public sealed class SearxngSearchProvider : IWebSearchProvider
{
    private static readonly HttpClient Shared = CreateClient();

    private readonly string _searchUrl;
    private readonly HttpClient _http;

    /// <param name="baseUrl">SearXNG instance root or full .../search URL.</param>
    /// <param name="http">Override the shared HttpClient (tests inject a fake handler).</param>
    public SearxngSearchProvider(string baseUrl, HttpClient? http = null)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        // Accept either the instance root or a full .../search URL.
        _searchUrl = trimmed.EndsWith("/search", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/search";
        _http = http ?? Shared;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tommy", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<WebSearchResult>();

        var url = $"{_searchUrl}?q={Uri.EscapeDataString(query)}&format=json";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResults(json, maxResults);
    }

    /// <summary>
    /// Maps a SearXNG JSON response to search hits (kept separate so it is unit-testable without
    /// a live instance). Tolerant of an empty body or malformed JSON — returns no hits rather
    /// than throwing.
    /// </summary>
    public static IReadOnlyList<WebSearchResult> ParseResults(string json, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<WebSearchResult>();

        SearxngResponse? parsed;
        try { parsed = JsonSerializer.Deserialize(json, SearxngJson.Default.SearxngResponse); }
        catch (JsonException) { return Array.Empty<WebSearchResult>(); }

        var results = parsed?.Results;
        if (results is null || results.Count == 0) return Array.Empty<WebSearchResult>();

        var limit = maxResults <= 0 ? 10 : maxResults;
        return results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .Select(r => new WebSearchResult(
                string.IsNullOrWhiteSpace(r.Title) ? r.Url! : r.Title!,
                r.Url!,
                r.Content ?? string.Empty))
            .Take(limit)
            .ToList();
    }
}

/// <summary>Shape of the SearXNG <c>format=json</c> response (only the fields we use).</summary>
internal sealed class SearxngResponse
{
    [JsonPropertyName("results")]
    public List<SearxngResult>? Results { get; set; }
}

internal sealed class SearxngResult
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SearxngResponse))]
internal partial class SearxngJson : JsonSerializerContext
{
}
