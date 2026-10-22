using System.Net;
using System.Text;
using Tommy.Engine;
using Tommy.Engine.Dto;

namespace Tommy.Tests;

/// <summary>
/// Streaming assembly: content arrives as deltas and tool calls arrive in fragments
/// (id/name once, arguments across chunks, keyed by index) — StreamAsync must reassemble
/// them into a usable final result.
/// </summary>
public class StreamingTests
{
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly string _body;
        public CannedHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream")
            });
    }

    private static string Sse(params string[] chunks) =>
        string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";

    private static async Task<List<ChatStreamEvent>> Collect(string body)
    {
        var client = new StrataClient(new HttpClient(new CannedHandler(body)), "http://localhost/v1");
        var events = new List<ChatStreamEvent>();
        await foreach (var ev in client.StreamAsync(new ChatCompletionRequest { Model = "m", Messages = new() }))
            events.Add(ev);
        return events;
    }

    [Fact]
    public async Task Content_deltas_stream_then_complete()
    {
        var events = await Collect(Sse(
            """{"choices":[{"delta":{"content":"Hello"}}]}""",
            """{"choices":[{"delta":{"content":" world"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}"""));

        var content = string.Concat(events
            .Where(e => e.Kind == ChatStreamEventKind.ContentDelta)
            .Select(e => e.Text));
        Assert.Equal("Hello world", content);
        Assert.Equal(FinishReason.Stop, events.Single(e => e.Kind == ChatStreamEventKind.Completed).FinishReason);
    }

    [Fact]
    public async Task Tool_call_arguments_are_reassembled_across_chunks()
    {
        var events = await Collect(Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"line_count","arguments":"{\"pa"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"x\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""));

        var completed = events.Single(e => e.Kind == ChatStreamEventKind.Completed);
        Assert.Equal(FinishReason.ToolCalls, completed.FinishReason);
        var call = Assert.Single(completed.ToolCalls!);
        Assert.Equal("c1", call.Id);
        Assert.Equal("line_count", call.Name);
        Assert.Equal("""{"path":"x"}""", call.ArgumentsJson);
    }

    [Fact]
    public async Task Two_parallel_tool_calls_stay_separate_by_index()
    {
        var events = await Collect(Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"read_file","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"list_dir","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""));

        var calls = events.Single(e => e.Kind == ChatStreamEventKind.Completed).ToolCalls!;
        Assert.Equal(2, calls.Count);
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("list_dir", calls[1].Name);
    }

    [Fact]
    public async Task No_tool_calls_leaves_the_list_null()
    {
        var events = await Collect(Sse(
            """{"choices":[{"delta":{"content":"hi"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}"""));

        Assert.Null(events.Single(e => e.Kind == ChatStreamEventKind.Completed).ToolCalls);
    }
}
