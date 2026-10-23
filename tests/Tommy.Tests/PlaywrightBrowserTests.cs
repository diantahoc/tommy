using Tommy.Engine.Research;

namespace Tommy.Tests;

public class PlaywrightBrowserTests
{
    [Fact]
    public void IsAvailable_probes_without_throwing()
    {
        // Whether or not Chromium is provisioned on this machine, the probe returns a bool
        // (graceful-degrade for web_fetch_js), never throws.
        var available = new PlaywrightBrowser().IsAvailable;
        Assert.True(available || !available);
    }
}
