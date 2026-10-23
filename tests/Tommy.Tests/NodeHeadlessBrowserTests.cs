using Tommy.Engine.Research;

namespace Tommy.Tests;

public class NodeHeadlessBrowserTests
{
    [Fact]
    public void Parses_full_payload()
    {
        var json = """{"url":"https://final.example/x","title":"Title","content":"Hello body"}""";
        var doc = NodeHeadlessBrowser.Parse("https://req.example", json);

        Assert.Equal("https://final.example/x", doc.Url);
        Assert.Equal("Title", doc.Title);
        Assert.Equal("Hello body", doc.Text);
    }

    [Fact]
    public void Falls_back_to_request_url_and_empty_content()
    {
        var doc = NodeHeadlessBrowser.Parse("https://req.example", """{"content":null}""");
        Assert.Equal("https://req.example", doc.Url);
        Assert.Equal("https://req.example", doc.Title); // title defaults to the url
        Assert.Equal(string.Empty, doc.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void Empty_or_malformed_throws(string json)
    {
        Assert.Throws<InvalidOperationException>(() => NodeHeadlessBrowser.Parse("https://req.example", json));
    }

    [Fact]
    public void IsAvailable_probes_without_throwing()
    {
        // Whether or not the runtime is provisioned on this machine, the probe must
        // return a bool (graceful-degrade), never throw.
        var available = new NodeHeadlessBrowser().IsAvailable;
        Assert.True(available || !available);
    }
}
