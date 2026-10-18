using Tommy.Core.Chat;
using Tommy.Core.Events;

namespace Tommy.Persistence.Storage;

/// <summary>A search match: which chat, and a snippet of the matched text.</summary>
public sealed record SearchHit(string ChatId, string ChatTitle, ChatKind Kind, string Snippet);

/// <summary>
/// Plain-text search across chats (titles + event text). v1 is a substring scan
/// over the JSONL logs; semantic search is deferred with the memory decision.
/// </summary>
public sealed class ChatSearch
{
    private readonly ChatRepository _chats;

    public ChatSearch(ChatRepository chats) => _chats = chats;

    /// <param name="query">Case-insensitive substring to find.</param>
    /// <param name="kind">Optional section filter (Chats vs Code).</param>
    public IReadOnlyList<SearchHit> Search(string query, ChatKind? kind = null, int maxHits = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchHit>();

        var hits = new List<SearchHit>();
        foreach (var record in _chats.List())
        {
            if (kind is { } k && record.Kind != k) continue;

            if (Contains(record.Title, query))
            {
                hits.Add(new SearchHit(record.Id, record.Title, record.Kind, record.Title));
                if (hits.Count >= maxHits) break;
                continue;
            }

            var handle = _chats.Open(record);
            string? snippet = null;
            foreach (var ev in handle.Events.Read())
            {
                var text = TextOf(ev);
                if (text is null) continue;
                var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                snippet = Snippet(text, idx, query.Length);
                break;
            }

            if (snippet is not null)
            {
                hits.Add(new SearchHit(record.Id, record.Title, record.Kind, snippet));
                if (hits.Count >= maxHits) break;
            }
        }

        return hits;
    }

    private static bool Contains(string? s, string q) =>
        s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static string? TextOf(ChatEvent e) => e switch
    {
        UserEvent u => u.Text,
        AssistantEvent a => a.Content,
        ToolResultEvent t => t.Result,
        MetaEvent m => m.Detail,
        _ => null
    };

    private static string Snippet(string text, int idx, int matchLen)
    {
        const int pad = 40;
        var start = Math.Max(0, idx - pad);
        var end = Math.Min(text.Length, idx + matchLen + pad);
        var slice = text[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < text.Length ? "…" : string.Empty;
        return prefix + slice + suffix;
    }
}
