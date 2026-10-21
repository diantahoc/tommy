using System.Net;
using Tommy.Engine;

namespace Tommy.Tests;

public class StrataRequestExceptionTests
{
    [Fact]
    public void Detects_context_window_overflow()
    {
        var ex = StrataRequestException.From(HttpStatusCode.BadRequest,
            "400 invalid request: request exceeds the context window: prompt (134983 tokens) + " +
            "max tokens (2048) exceeds the context (131072); requests are never truncated.");
        Assert.True(ex.IsContextOverflow);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("131072", ex.Message); // the server's own body is surfaced
    }

    [Fact]
    public void Other_400s_are_not_overflow()
    {
        var ex = StrataRequestException.From(HttpStatusCode.BadRequest, "invalid 'messages': role is missing");
        Assert.False(ex.IsContextOverflow);
        Assert.Contains("role is missing", ex.Message);
    }

    [Fact]
    public void Empty_body_is_handled()
    {
        var ex = StrataRequestException.From(HttpStatusCode.BadRequest, null);
        Assert.False(ex.IsContextOverflow);
        Assert.Contains("no response body", ex.Message);
    }
}
