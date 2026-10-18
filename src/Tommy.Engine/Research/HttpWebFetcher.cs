using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Tommy.Core.Research;

namespace Tommy.Engine.Research;

/// <summary>
/// Fetches a URL over HTTP and reduces the HTML to readable text with no browser
/// engine — strips script/style, tags, and collapses whitespace. Enough for
/// static pages; JS-interactive pages are the headless browser's job.
/// </summary>
public sealed class HttpWebFetcher : IWebFetcher
{
    private const int MaxBytes = 2 * 1024 * 1024;   // don't pull unbounded pages
    private const int MaxTextChars = 40_000;        // context economy — distilled later

    private static readonly HttpClient Http = CreateClient();

    private static readonly Regex ScriptStyle =
        new("<(script|style|noscript)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new("[ \\t\\f\\v]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new("\\n{3,}", RegexOptions.Compiled);
    private static readonly Regex TitleTag = new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tommy", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    public async Task<WebDocument> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("URL must be an absolute http(s) address.");

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBytes];
        var total = 0;
        int read;
        while (total < MaxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, MaxBytes - total), ct).ConfigureAwait(false)) > 0)
            total += read;

        var html = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        var title = ExtractTitle(html) ?? uri.Host;
        var text = HtmlToText(html);
        return new WebDocument(url, title, text);
    }

    private static string? ExtractTitle(string html)
    {
        var m = TitleTag.Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    public static string HtmlToText(string html)
    {
        var text = ScriptStyle.Replace(html, " ");
        text = Tags.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Whitespace.Replace(text, " ");
        text = string.Join('\n', text.Split('\n').Select(l => l.Trim()));
        text = BlankLines.Replace(text, "\n\n").Trim();
        return text.Length > MaxTextChars ? text[..MaxTextChars] + "\n… (truncated)" : text;
    }
}
