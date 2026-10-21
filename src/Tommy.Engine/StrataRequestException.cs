using System.Net;

namespace Tommy.Engine;

/// <summary>
/// A non-success HTTP response from the Strata endpoint, carrying the server's
/// response body (which <see cref="System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode"/>
/// discards). <see cref="IsContextOverflow"/> flags the specific 400 the engine
/// returns when the prompt no longer fits the model's context window, so the
/// agent loop can compact and retry rather than failing the run.
/// </summary>
public sealed class StrataRequestException : Exception
{
    public HttpStatusCode StatusCode { get; }

    /// <summary>The raw response body (may be empty), trimmed for logging.</summary>
    public string Body { get; }

    /// <summary>True when the body indicates the request exceeded the context window.</summary>
    public bool IsContextOverflow { get; }

    private StrataRequestException(HttpStatusCode statusCode, string body, bool isContextOverflow, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
        IsContextOverflow = isContextOverflow;
    }

    public static StrataRequestException From(HttpStatusCode statusCode, string? body)
    {
        var text = (body ?? string.Empty).Trim();
        var overflow = LooksLikeContextOverflow(text);
        var detail = text.Length == 0 ? "(no response body)" : Truncate(text, 600);
        return new StrataRequestException(statusCode, text, overflow,
            $"Strata returned {(int)statusCode} {statusCode}: {detail}");
    }

    /// <summary>
    /// Matches the engine's context-window rejection, e.g. "request exceeds the
    /// context window: prompt (… tokens) … exceeds the context (…)".
    /// </summary>
    private static bool LooksLikeContextOverflow(string body) =>
        body.Contains("context window", StringComparison.OrdinalIgnoreCase)
        || (body.Contains("context", StringComparison.OrdinalIgnoreCase)
            && body.Contains("exceed", StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
