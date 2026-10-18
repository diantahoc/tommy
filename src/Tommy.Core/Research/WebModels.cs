namespace Tommy.Core.Research;

/// <summary>One search hit: enough to decide what to fetch, and to cite.</summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>A fetched page reduced to readable text, with its source retained for citation.</summary>
public sealed record WebDocument(string Url, string Title, string Text);

/// <summary>
/// The raw search capability — the "hands." Any route may back it (an existing
/// stdio web-search MCP, or an HTTP API). Research orchestration gives this to an
/// isolated subagent and nothing else.
/// </summary>
public interface IWebSearchProvider
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct);
}

/// <summary>Fetches one URL and reduces it to readable text.</summary>
public interface IWebFetcher
{
    Task<WebDocument> FetchAsync(string url, CancellationToken ct);
}

/// <summary>
/// Goal-directed reducer: distills a fetched page <b>against a research goal</b>,
/// cutting nav chrome and unrelated content, keeping the relevant slice plus the
/// source. Runs as an isolated, no-tools completion, so a poisoned page can
/// hijack at most this reducer — whose only output is tagged data.
/// </summary>
public interface ISummarizer
{
    Task<string> SummarizeAsync(string goal, WebDocument document, CancellationToken ct);
}
