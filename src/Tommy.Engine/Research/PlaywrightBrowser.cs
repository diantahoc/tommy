using Microsoft.Playwright;
using Tommy.Core.Diagnostics;
using Tommy.Core.Research;
using Tommy.Core.Runtime;

namespace Tommy.Engine.Research;

/// <summary>
/// Points Playwright at the bundled Chromium (under <c>integrated-tools/browsers</c>). Set once,
/// before any Playwright instance is created.
/// </summary>
internal static class PlaywrightEnv
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0) return;
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", IntegratedTools.Browsers());
    }
}

/// <summary>
/// The headless-browser backend for <c>web_fetch_js</c>, built on Microsoft.Playwright (.NET)
/// driving the bundled Chromium — one Playwright stack for both the one-shot fetch and (later) the
/// interactive browser. Replaces the former Node <c>web-fetch-js</c> script. Renders a page and
/// returns its readable text; degrades via <see cref="IsAvailable"/> until Chromium is provisioned.
/// </summary>
public sealed class PlaywrightBrowser : IHeadlessBrowser, IAsyncDisposable
{
    // Runs in the page: prefer the main article region, strip chrome, fall back to the body.
    private const string ExtractReadableTextJs = @"() => {
        const drop = ['script','style','noscript','template','nav','header','footer','aside','form'];
        const pick = document.querySelector('article') || document.querySelector('main') || document.body;
        if (!pick) return '';
        const clone = pick.cloneNode(true);
        for (const sel of drop) clone.querySelectorAll(sel).forEach(n => n.remove());
        const text = clone.innerText || clone.textContent || '';
        return text.replace(/\n{3,}/g, '\n\n').trim();
    }";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _pw;
    private IBrowser? _browser;

    static PlaywrightBrowser() => PlaywrightEnv.Configure();

    public bool IsAvailable => IntegratedTools.HasChromium();

    public async Task<WebDocument> RenderAsync(string url, CancellationToken ct)
    {
        var browser = await EnsureBrowserAsync().ConfigureAwait(false);
        var page = await browser.NewPageAsync().ConfigureAwait(false);
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            }).ConfigureAwait(false);

            // Give client-side rendering a moment to settle; ignore if it never idles.
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 8_000 }).ConfigureAwait(false); }
            catch (TimeoutException) { /* fine */ }

            var title = await page.TitleAsync().ConfigureAwait(false);
            var content = await page.EvaluateAsync<string>(ExtractReadableTextJs).ConfigureAwait(false);
            var finalUrl = page.Url;
            return new WebDocument(finalUrl, string.IsNullOrWhiteSpace(title) ? finalUrl : title, content ?? string.Empty);
        }
        finally
        {
            try { await page.CloseAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
    }

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is { IsConnected: true }) return _browser;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true }) return _browser;
            _pw ??= await Playwright.CreateAsync().ConfigureAwait(false);
            _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_browser is not null) await _browser.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"playwright dispose: {ex.Message}"); }
        _pw?.Dispose();
        _gate.Dispose();
    }
}
