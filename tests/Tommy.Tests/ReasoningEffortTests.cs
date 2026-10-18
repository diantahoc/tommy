using Tommy.Core.Chat;
using Tommy.Core.Tools;
using Tommy.Engine;
using Tommy.Engine.Agent;
using Tommy.Engine.Dto;

namespace Tommy.Tests;

public class ReasoningEffortTests
{
    private sealed class CapturingCompleter : IChatCompleter
    {
        public ChatCompletionRequest? Last;
        public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new ChatTurnResult("done", null, FinishReason.Stop, null, null, Array.Empty<ToolCall>()));
        }
    }

    private static async Task<ChatCompletionRequest> RunWith(ThinkingEffort effort)
    {
        var completer = new CapturingCompleter();
        var loop = new AgentLoop(completer, new ToolRegistry());
        var run = new AgentRun
        {
            Model = "m",
            SystemPrompt = "s",
            History = new List<ChatMessageDto> { new() { Role = "user", Content = "hi" } },
            ToolContext = new ToolContext("."),
            Autonomy = AutonomyLevel.Auto,
            ThinkingEffort = effort
        };
        await foreach (var _ in loop.RunAsync(run)) { }
        return completer.Last!;
    }

    [Theory]
    [InlineData(ThinkingEffort.Low, "low")]
    [InlineData(ThinkingEffort.Medium, "medium")]
    [InlineData(ThinkingEffort.High, "high")]
    public async Task Effort_maps_to_reasoning_effort(ThinkingEffort effort, string expected)
    {
        var request = await RunWith(effort);
        Assert.Equal(expected, request.ReasoningEffort);
    }

    [Fact]
    public async Task Off_omits_reasoning_effort()
    {
        var request = await RunWith(ThinkingEffort.Off);
        Assert.Null(request.ReasoningEffort);
    }
}
