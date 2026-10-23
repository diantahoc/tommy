using System.Text.Json;
using Tommy.Core.Diagnostics;
using Tommy.Core.Research;
using Tommy.Core.Runtime;
using Tommy.Engine.Provisioning;

namespace Tommy.Engine.Research;

/// <summary>
/// The headless-browser backend for <c>web_fetch_js</c>, built on Tommy's own
/// Node + Playwright program (<c>integrated-tools/helpers/web-fetch-js/fetch.js</c>,
/// provisioned from the <c>scripts/</c> kit). Replaces the old web-search-mcp
/// rendering path. Spawns the Node program per fetch, pointed at the bundled
/// Chromium, and parses its JSON (<c>{ url, title, content }</c>) into a
/// <see cref="WebDocument"/>. Degrades via <see cref="IsAvailable"/> when the
/// program or Chromium isn't provisioned yet (so it's quiet while startup
/// provisioning is still running).
/// </summary>
public sealed class NodeHeadlessBrowser : IHeadlessBrowser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public bool IsAvailable =>
        IntegratedTools.FindNode() is not null
        && File.Exists(ScriptPath)
        && Directory.Exists(Path.Combine(WebFetchDir, "node_modules"))
        && IntegratedTools.HasChromium();

    private static string WebFetchDir => IntegratedTools.Helper("web-fetch-js");
    private static string ScriptPath => IntegratedTools.Helper("web-fetch-js", "fetch.js");

    public async Task<WebDocument> RenderAsync(string url, CancellationToken ct)
    {
        var node = IntegratedTools.FindNode()
            ?? throw new InvalidOperationException("bundled Node is not available.");

        var env = new Dictionary<string, string> { ["PLAYWRIGHT_BROWSERS_PATH"] = IntegratedTools.Browsers() };
        var (exit, stdout, stderr) = await ProvisionIo.RunCaptureAsync(
            node, new[] { ScriptPath, url }, WebFetchDir, TimeSpan.FromSeconds(60), ct, env).ConfigureAwait(false);

        if (exit != 0)
        {
            var msg = stderr.Trim();
            throw new InvalidOperationException(msg.Length == 0 ? "the headless browser failed." : msg);
        }

        return Parse(url, stdout);
    }

    /// <summary>Parse the Node program's JSON output into a <see cref="WebDocument"/> (test seam).</summary>
    public static WebDocument Parse(string url, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("the headless browser returned no output.");

        Payload? parsed;
        try { parsed = JsonSerializer.Deserialize<Payload>(json, JsonOptions); }
        catch (JsonException ex)
        {
            Log.Warn($"web_fetch_js: could not parse output: {ex.Message}");
            throw new InvalidOperationException("the headless browser returned malformed output.");
        }

        if (parsed is null)
            throw new InvalidOperationException("the headless browser returned malformed output.");

        var finalUrl = string.IsNullOrWhiteSpace(parsed.Url) ? url : parsed.Url!;
        return new WebDocument(finalUrl, parsed.Title ?? finalUrl, parsed.Content ?? string.Empty);
    }

    private sealed class Payload
    {
        public string? Url { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
    }
}
