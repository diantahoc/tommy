using System.Text;
using Tommy.Core.Diagnostics;
using Tommy.Core.Research;

namespace Tommy.Engine.Research;

/// <summary>
/// Harness-owned web research: the containment the spec says can't be delegated
/// to a server. It gives the fetch/search capability to an isolated pipeline —
/// search, fetch, goal-directed summarize — and returns the distilled result as
/// <b>tagged data</b>, so a poisoned page can hijack at most the summarizer
/// (which has no tools) and the main loop never executes what the web said.
/// </summary>
public sealed class WebResearchOrchestrator
{
    private readonly IWebFetcher _fetcher;
    private readonly ISummarizer _summarizer;
    private volatile IWebSearchProvider? _search;

    public WebResearchOrchestrator(IWebFetcher fetcher, ISummarizer summarizer, IWebSearchProvider? search = null)
    {
        _fetcher = fetcher;
        _summarizer = summarizer;
        _search = search;
    }

    public bool HasSearch => _search is not null;

    /// <summary>
    /// Swap the search provider at runtime (e.g. when the user sets a SearXNG URL in Settings),
    /// so the change takes effect on the next query without rebuilding the tool. <c>null</c>
    /// returns to the no-search-configured degrade. The field is volatile — a run reads it on a
    /// background thread while the UI writes it.
    /// </summary>
    public void SetSearchProvider(IWebSearchProvider? search) => _search = search;

    /// <summary>
    /// Research a goal. With a URL, fetch and distill that page; with a query and
    /// a configured search provider, search, fetch the top results, and distill
    /// each against the goal. Always returns a single tagged-data string.
    /// </summary>
    public async Task<string> ResearchAsync(string goal, string? query, string? url, int maxResults, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(url))
            return await ResearchUrlAsync(goal, url!, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(query))
            return "web_research needs either a url or a query.";

        if (_search is null)
            return "No web-search provider is configured. Pass a specific url to research instead, or enable a search provider.";

        IReadOnlyList<WebSearchResult> hits;
        try
        {
            hits = await _search.SearchAsync(query!, Math.Clamp(maxResults, 1, 5), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("web search failed", ex);
            return $"web search failed: {ex.Message}";
        }

        if (hits.Count == 0)
            return TaggedData.Wrap($"No results for \"{query}\".", Array.Empty<string>());

        var distilled = new StringBuilder();
        var sources = new List<string>();
        foreach (var hit in hits)
        {
            ct.ThrowIfCancellationRequested();
            var slice = await DistillAsync(goal, hit.Url, ct).ConfigureAwait(false);
            if (slice is null) continue;
            distilled.Append("### ").AppendLine(hit.Title);
            distilled.AppendLine(slice);
            distilled.AppendLine();
            sources.Add(hit.Url);
        }

        if (sources.Count == 0)
            return TaggedData.Wrap($"Found results for \"{query}\" but none could be fetched.", hits.Select(h => h.Url));

        return TaggedData.Wrap(distilled.ToString().Trim(), sources);
    }

    private async Task<string> ResearchUrlAsync(string goal, string url, CancellationToken ct)
    {
        var slice = await DistillAsync(goal, url, ct).ConfigureAwait(false);
        return slice is null
            ? $"could not fetch {url}."
            : TaggedData.Wrap(slice, url);
    }

    /// <summary>Fetch one URL and distill it against the goal; null if the fetch failed.</summary>
    private async Task<string?> DistillAsync(string goal, string url, CancellationToken ct)
    {
        WebDocument doc;
        try
        {
            doc = await _fetcher.FetchAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"web fetch failed for {url}: {ex.Message}");
            return null;
        }

        return await _summarizer.SummarizeAsync(goal, doc, ct).ConfigureAwait(false);
    }
}
