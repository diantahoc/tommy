using System.Text.Json;
using Microsoft.Playwright;
using Tommy.Core.Diagnostics;
using Tommy.Core.Research;

namespace Tommy.Engine.Research.Browser;

/// <summary>
/// A long-lived interactive browser for one chat, built on a Playwright <b>persistent
/// context</b> (so cookies/logins survive restarts) with a per-chat, isolated profile
/// directory. Lazy-started on the first operation. Backs the <c>browser_*</c> agent
/// tools and (later) the screencast pane.
/// </summary>
public sealed class ManagedBrowser : IBrowserSession, IAsyncDisposable
{
    // Runs in the page: tag visible interactive elements with data-tommy-ref and return the
    // page's readable text + the element list, as JSON.
    private const string ReadJs = @"() => {
        const sel = 'a,button,input,textarea,select,[role=button],[role=link],[role=textbox],[onclick]';
        const vis = e => e.offsetParent !== null || e.getClientRects().length > 0;
        const els = Array.from(document.querySelectorAll(sel)).filter(vis);
        els.forEach((e,i) => e.setAttribute('data-tommy-ref', String(i+1)));
        const elements = els.map((e,i) => ({
            ref: i+1,
            kind: (e.getAttribute('role') || e.tagName || '').toLowerCase(),
            name: ((e.innerText || e.value || e.getAttribute('aria-label') || e.getAttribute('placeholder') || '').trim()).slice(0,100)
        }));
        const drop = ['script','style','noscript','template','nav','header','footer','aside'];
        const pick = document.querySelector('main') || document.querySelector('article') || document.body;
        let text = '';
        if (pick) { const c = pick.cloneNode(true); for (const s of drop) c.querySelectorAll(s).forEach(n=>n.remove()); text = (c.innerText||'').replace(/\n{3,}/g,'\n\n').trim(); }
        return JSON.stringify({ url: location.href, title: document.title, text, elements });
    }";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _profileDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _pw;
    private IBrowserContext? _ctx;
    private IPage? _page;

    public ManagedBrowser(string profileDir)
    {
        _profileDir = profileDir;
        PlaywrightEnv.Configure();
    }

    public string? CurrentUrl => _page?.Url;

    /// <summary>The live page, for the screencast pane (null until started). Engine-internal.</summary>
    public IPage? Page => _page;

    public async Task NavigateAsync(string url, CancellationToken ct)
    {
        var page = await EnsureAsync().ConfigureAwait(false);
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 }).ConfigureAwait(false);
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 6_000 }).ConfigureAwait(false); }
        catch (TimeoutException) { /* fine */ }
    }

    public async Task<BrowserPage> ReadAsync(CancellationToken ct)
    {
        var page = await EnsureAsync().ConfigureAwait(false);
        var json = await page.EvaluateAsync<string>(ReadJs).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<PageDto>(json ?? "", JsonOptions) ?? new PageDto();
        var elements = (dto.Elements ?? new List<ElementDto>())
            .Select(e => new BrowserElement(e.Ref, e.Kind ?? "", e.Name ?? ""))
            .ToList();
        return new BrowserPage(dto.Url ?? page.Url, dto.Title ?? "", dto.Text ?? "", elements);
    }

    public async Task ClickAsync(int reference, CancellationToken ct)
    {
        var page = await EnsureAsync().ConfigureAwait(false);
        await page.Locator($"[data-tommy-ref='{reference}']").First.ClickAsync(new LocatorClickOptions { Timeout = 15_000 }).ConfigureAwait(false);
    }

    public async Task TypeAsync(int reference, string text, bool submit, CancellationToken ct)
    {
        var page = await EnsureAsync().ConfigureAwait(false);
        var loc = page.Locator($"[data-tommy-ref='{reference}']").First;
        await loc.FillAsync(text, new LocatorFillOptions { Timeout = 15_000 }).ConfigureAwait(false);
        if (submit) await loc.PressAsync("Enter").ConfigureAwait(false);
    }

    public async Task<byte[]> ScreenshotAsync(CancellationToken ct)
    {
        var page = await EnsureAsync().ConfigureAwait(false);
        return await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png }).ConfigureAwait(false);
    }

    private async Task<IPage> EnsureAsync()
    {
        if (_page is not null && !_page.IsClosed) return _page;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_page is not null && !_page.IsClosed) return _page;
            _pw ??= await Playwright.CreateAsync().ConfigureAwait(false);
            Directory.CreateDirectory(_profileDir);
            _ctx ??= await _pw.Chromium.LaunchPersistentContextAsync(_profileDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = true,
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            }).ConfigureAwait(false);
            _page = _ctx.Pages.FirstOrDefault() ?? await _ctx.NewPageAsync().ConfigureAwait(false);
            return _page;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_ctx is not null) await _ctx.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"browser dispose: {ex.Message}"); }
        _pw?.Dispose();
        _gate.Dispose();
    }

    private sealed class PageDto
    {
        public string? Url { get; set; }
        public string? Title { get; set; }
        public string? Text { get; set; }
        public List<ElementDto>? Elements { get; set; }
    }

    private sealed class ElementDto
    {
        public int Ref { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
    }
}
