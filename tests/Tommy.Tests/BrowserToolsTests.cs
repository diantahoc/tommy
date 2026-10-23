using System.Text.Json;
using Tommy.Core.Tools;
using Tommy.Engine.Research.Browser;

namespace Tommy.Tests;

public class BrowserToolsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
    private static ToolContext NoBrowser() => new("/tmp"); // OpenBrowser is null

    [Fact]
    public void Action_tools_always_ask_reads_do_not()
    {
        Assert.Equal(PermissionAction.Ask, ((ITool)new BrowserNavigateTool()).ApprovalOverride);
        Assert.Equal(PermissionAction.Ask, ((ITool)new BrowserClickTool()).ApprovalOverride);
        Assert.Equal(PermissionAction.Ask, ((ITool)new BrowserTypeTool()).ApprovalOverride);
        Assert.Null(((ITool)new BrowserPageReadTool()).ApprovalOverride);
        Assert.Null(((ITool)new BrowserScreenshotTool()).ApprovalOverride);
    }

    [Fact]
    public void All_browser_tools_are_network_tier()
    {
        Assert.Equal(PermissionTier.Network, new BrowserNavigateTool().Tier);
        Assert.Equal(PermissionTier.Network, new BrowserPageReadTool().Tier);
        Assert.Equal(PermissionTier.Network, new BrowserScreenshotTool().Tier);
    }

    [Fact]
    public async Task Navigate_requires_url()
    {
        var r = await new BrowserNavigateTool().ExecuteAsync(Args("{}"), NoBrowser(), default);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Degrades_when_no_session_is_wired()
    {
        var r = await new BrowserNavigateTool().ExecuteAsync(Args("""{"url":"https://example.com"}"""), NoBrowser(), default);
        Assert.False(r.Success);
        Assert.Contains("not available", r.Content);
    }

    [Fact]
    public async Task Click_requires_ref()
    {
        var r = await new BrowserClickTool().ExecuteAsync(Args("{}"), NoBrowser(), default);
        Assert.False(r.Success);
    }
}
