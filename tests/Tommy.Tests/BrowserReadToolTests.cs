using System.Text.Json;
using Tommy.Core.Research;
using Tommy.Core.Tools;
using Tommy.Engine.Research;

namespace Tommy.Tests;

public class BrowserReadToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly ToolContext Ctx = new(".");

    private sealed class FakeSummarizer : ISummarizer
    {
        public string? LastGoal;
        public Task<string> SummarizeAsync(string goal, WebDocument document, CancellationToken ct)
        {
            LastGoal = goal;
            return Task.FromResult($"[distilled] {document.Text}");
        }
    }

    private sealed class FakeBrowser : IHeadlessBrowser
    {
        public bool IsAvailable { get; init; } = true;
        public Task<WebDocument> RenderAsync(string url, CancellationToken ct) =>
            Task.FromResult(new WebDocument(url, "Rendered", "dynamic content"));
    }

    [Fact]
    public async Task Degrades_when_no_backend()
    {
        var tool = new BrowserReadTool(browser: null, new FakeSummarizer());
        var result = await tool.ExecuteAsync(Args("""{"url":"https://x.dev"}"""), Ctx, default);
        Assert.False(result.Success);
        Assert.Contains("web_fetch", result.Content);
    }

    [Fact]
    public async Task Degrades_when_backend_unavailable()
    {
        var tool = new BrowserReadTool(new FakeBrowser { IsAvailable = false }, new FakeSummarizer());
        var result = await tool.ExecuteAsync(Args("""{"url":"https://x.dev"}"""), Ctx, default);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Renders_and_tags_when_available()
    {
        var tool = new BrowserReadTool(new FakeBrowser(), new FakeSummarizer());
        var result = await tool.ExecuteAsync(Args("""{"url":"https://x.dev"}"""), Ctx, default);
        Assert.True(result.Success);
        Assert.Contains("WEB DATA", result.Content);
        Assert.Contains("dynamic content", result.Content);
    }

    [Fact]
    public async Task Distills_when_goal_given()
    {
        var summarizer = new FakeSummarizer();
        var tool = new BrowserReadTool(new FakeBrowser(), summarizer);
        await tool.ExecuteAsync(Args("""{"url":"https://x.dev","goal":"the price"}"""), Ctx, default);
        Assert.Equal("the price", summarizer.LastGoal);
    }

    [Fact]
    public async Task Requires_url()
    {
        var tool = new BrowserReadTool(new FakeBrowser(), new FakeSummarizer());
        var result = await tool.ExecuteAsync(Args("{}"), Ctx, default);
        Assert.False(result.Success);
    }

    [Fact]
    public void Is_network_tier_and_web_safe()
    {
        Assert.Equal(PermissionTier.Network, new BrowserReadTool(null, new FakeSummarizer()).Tier);
        Assert.True(WebCapabilities.IsAllowed("browser_read"));
    }
}
