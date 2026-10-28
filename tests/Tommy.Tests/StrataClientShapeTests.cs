using System.Net;
using Tommy.Engine;

namespace Tommy.Tests;

/// <summary>
/// The Strata-specific JSON shapes (/v1/models, /metrics) are the parts most
/// likely to drift across engine versions. These guard the graceful-degrade
/// path: a missing field surfaces as a benign default (and a one-shot log
/// warning), never an exception that blanks the UI.
/// </summary>
public class StrataClientShapeTests
{
    private sealed class CannedGetHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
    }

    [Fact]
    public async Task Capabilities_without_n_ctx_report_a_zero_context_window()
    {
        using var http = new HttpClient(new CannedGetHandler("""{"data":[{"id":"m"}]}"""));
        var client = new StrataClient(http, "http://127.0.0.1:8080/v1");

        var caps = await client.GetCapabilitiesAsync();

        // Degrades to 0 (which disables proactive budgeting) rather than throwing.
        Assert.Equal("m", caps.ModelId);
        Assert.Equal(0, caps.MaxContextTokens);
    }

    [Fact]
    public async Task Telemetry_of_an_unknown_shape_degrades_without_throwing()
    {
        using var http = new HttpClient(new CannedGetHandler("{}"));
        var client = new StrataClient(http, "http://127.0.0.1:8080/v1");

        var t = await client.GetTelemetryAsync();

        Assert.Equal("unknown", t.LiveState);
    }
}
