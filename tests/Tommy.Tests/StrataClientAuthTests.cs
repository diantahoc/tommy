using System.Net;
using Tommy.Core.Configuration;
using Tommy.Core.Engine;
using Tommy.Engine;
using Tommy.Engine.Dto;

namespace Tommy.Tests;

/// <summary>
/// The engine's auth mechanism: when a profile carries a token, every request to
/// the endpoint must carry <c>Authorization: Bearer &lt;token&gt;</c>; with no token,
/// no auth header is sent. The header is set per-request so a shared HttpClient is
/// never mutated.
/// </summary>
public class StrataClientAuthTests
{
    /// <summary>Captures the Authorization header of each request and replies with a canned completion.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string?> AuthHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""")
            });
        }
    }

    private static ChatCompletionRequest Req() => new()
    {
        Model = "test-model",
        Messages = new List<ChatMessageDto> { new() { Role = "user", Content = "hi" } }
    };

    [Fact]
    public async Task Bearer_token_is_sent_on_requests()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new StrataClient(http, "http://127.0.0.1:8080/v1", authToken: "secret-key");

        await client.CompleteAsync(Req());

        Assert.Equal("Bearer secret-key", Assert.Single(handler.AuthHeaders));
    }

    [Fact]
    public async Task No_auth_header_without_a_token()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new StrataClient(http, "http://127.0.0.1:8080/v1");

        await client.CompleteAsync(Req());

        Assert.Null(Assert.Single(handler.AuthHeaders));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_token_sends_no_auth_header(string token)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new StrataClient(http, "http://127.0.0.1:8080/v1", authToken: token);

        await client.CompleteAsync(Req());

        Assert.Null(Assert.Single(handler.AuthHeaders));
    }

    [Fact]
    public async Task Health_check_also_carries_the_token()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new StrataClient(http, "http://127.0.0.1:8080/v1", authToken: "secret-key");

        await client.CheckHealthAsync();

        Assert.Equal("Bearer secret-key", Assert.Single(handler.AuthHeaders));
    }

    [Fact]
    public async Task EngineService_applies_a_token_set_at_runtime()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = new EngineProfile { Name = "t", Mode = EngineMode.Remote, BaseUrl = "http://127.0.0.1:8080/v1" };
        using var engine = new EngineService(profile, http);

        await engine.CompleteAsync(Req());   // no token configured
        engine.SetAuthToken("live-key");     // changed from Settings
        await engine.CompleteAsync(Req());   // next request carries it

        Assert.Collection(handler.AuthHeaders,
            h => Assert.Null(h),
            h => Assert.Equal("Bearer live-key", h));
        Assert.Equal("live-key", engine.Profile.AuthToken);
    }
}
