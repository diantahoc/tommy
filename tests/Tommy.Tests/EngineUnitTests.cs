using Tommy.Core.Engine;
using Tommy.Engine;

namespace Tommy.Tests;

public class FinishReasonTests
{
    [Theory]
    [InlineData("stop", FinishReason.Stop)]
    [InlineData("tool_calls", FinishReason.ToolCalls)]
    [InlineData("length", FinishReason.Length)]
    [InlineData("weird", FinishReason.Other)]
    // No reason at all = an early/abnormal end, not a recognized-but-unknown one.
    [InlineData(null, FinishReason.Incomplete)]
    public void MapFinishReason_maps_wire_strings(string? wire, FinishReason expected)
    {
        Assert.Equal(expected, ChatTurnResult.MapFinishReason(wire));
    }

    [Fact]
    public void Length_with_empty_content_is_recoverable()
    {
        var starved = new ChatTurnResult("", "all budget went to thinking", FinishReason.Length, null, null, Array.Empty<ToolCall>());
        Assert.True(starved.IsRecoverableLength);
    }

    [Fact]
    public void Length_with_content_is_not_recoverable()
    {
        var ok = new ChatTurnResult("done", null, FinishReason.Length, null, null, Array.Empty<ToolCall>());
        Assert.False(ok.IsRecoverableLength);
    }

    [Fact]
    public void Stop_is_not_recoverable_length()
    {
        var ok = new ChatTurnResult("", null, FinishReason.Stop, null, null, Array.Empty<ToolCall>());
        Assert.False(ok.IsRecoverableLength);
    }
}

public class ModelCapabilitiesTests
{
    [Fact]
    public void Vision_off_when_only_text_modality()
    {
        var caps = new ModelCapabilities("m", new[] { "text" }, new[] { "text" }, 131072);
        Assert.False(caps.SupportsVision);
    }

    [Fact]
    public void Vision_on_when_image_modality_present()
    {
        var caps = new ModelCapabilities("m", new[] { "text", "image" }, new[] { "text" }, 131072);
        Assert.True(caps.SupportsVision);
    }
}

public class SingleModelQueueTests
{
    [Fact]
    public async Task Runs_work_one_at_a_time()
    {
        var queue = new SingleModelQueue();
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();

        async Task<int> Work(CancellationToken ct)
        {
            lock (gate) { concurrent++; maxConcurrent = Math.Max(maxConcurrent, concurrent); }
            await Task.Delay(25, ct);
            lock (gate) { concurrent--; }
            return 0;
        }

        var tasks = Enumerable.Range(0, 5).Select(_ => queue.RunAsync(Work)).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(0, queue.Depth);
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task Releases_slot_when_work_throws()
    {
        var queue = new SingleModelQueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.RunAsync<int>(_ => throw new InvalidOperationException()));

        Assert.False(queue.IsBusy);
        Assert.Equal(0, queue.Depth);
    }
}
