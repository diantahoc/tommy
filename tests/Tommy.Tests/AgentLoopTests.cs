using Tommy.Core.Tools;
using Tommy.Engine;
using Tommy.Engine.Agent;
using Tommy.Engine.Dto;
using Tommy.Tools;

namespace Tommy.Tests;

public class AgentLoopTests : IDisposable
{
    private readonly string _root;
    private readonly ToolRegistry _registry = new();

    public AgentLoopTests()
    {
        _root = Directory.CreateTempSubdirectory("tommy-loop-").FullName;
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "a\nb\nc\n");
        File.WriteAllText(Path.Combine(_root, "edit.txt"), "hello");
        _registry.DiscoverFrom(typeof(ReadFileTool).Assembly);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeCompleter : IChatCompleter
    {
        private readonly Queue<ChatTurnResult> _responses;
        public FakeCompleter(params ChatTurnResult[] responses) => _responses = new Queue<ChatTurnResult>(responses);
        public ChatTurnResult? Always { get; init; }
        public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Always ?? _responses.Dequeue());
    }

    private static ChatTurnResult Call(params (string id, string name, string args)[] calls) =>
        new("", null, FinishReason.ToolCalls, null, null,
            calls.Select(c => new ToolCall(c.id, c.name, c.args)).ToList());

    private static ChatTurnResult Stop(string content) =>
        new(content, null, FinishReason.Stop, null, null, Array.Empty<ToolCall>());

    private static ChatTurnResult Length(string content) =>
        new(content, null, FinishReason.Length, null, null, Array.Empty<ToolCall>());

    private AgentRun Run(IChatCompleter _, AutonomyLevel autonomy, ApprovalHandler? approve) => new()
    {
        Model = "m",
        SystemPrompt = "sys",
        History = new List<ChatMessageDto> { new() { Role = "user", Content = "go" } },
        ToolContext = new ToolContext(_root),
        Autonomy = autonomy,
        Approve = approve
    };

    private static async Task<List<AgentEvent>> Collect(AgentLoop loop, AgentRun run)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in loop.RunAsync(run))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task Executes_read_tool_then_answers()
    {
        var completer = new FakeCompleter(
            Call(("c1", "line_count", "{\"path\":\"hello.txt\"}")),
            Stop("The file has 3 lines."));
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        var finished = Assert.Single(events.OfType<ToolFinished>());
        Assert.True(finished.Result.Success);
        Assert.Equal("3", finished.Result.Content);
        Assert.Equal(RunEndReason.Answered, Assert.IsType<RunFinished>(events[^1]).Reason);
    }

    [Fact]
    public async Task Announces_tool_started_before_it_finishes()
    {
        // The UI activity hint depends on ToolStarted arriving before the tool runs;
        // if it only arrives bundled with ToolFinished, a slow/hung tool shows nothing.
        var completer = new FakeCompleter(
            Call(("c1", "line_count", "{\"path\":\"hello.txt\"}")),
            Stop("done"));
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        var startedAt = events.FindIndex(e => e is ToolStarted s && s.CallId == "c1");
        var finishedAt = events.FindIndex(e => e is ToolFinished f && f.CallId == "c1");
        Assert.True(startedAt >= 0, "expected a ToolStarted event");
        Assert.True(finishedAt >= 0, "expected a ToolFinished event");
        Assert.True(startedAt < finishedAt, "ToolStarted must precede ToolFinished");
    }

    [Fact]
    public async Task Runs_multiple_reads_in_one_step()
    {
        var completer = new FakeCompleter(
            Call(("c1", "line_count", "{\"path\":\"hello.txt\"}"),
                 ("c2", "list_dir", "{}")),
            Stop("done"));
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        Assert.Equal(2, events.OfType<ToolFinished>().Count());
        Assert.All(events.OfType<ToolFinished>(), f => Assert.True(f.Result.Success));
    }

    [Fact]
    public async Task Denied_write_feeds_denial_and_leaves_file_untouched()
    {
        var completer = new FakeCompleter(
            Call(("c1", "edit_file", "{\"path\":\"edit.txt\",\"old_text\":\"hello\",\"new_text\":\"bye\"}")),
            Stop("Understood, leaving it."));
        var loop = new AgentLoop(completer, _registry);

        var denyAll = new ApprovalHandler((_, _) => Task.FromResult(new ApprovalDecision(false)));
        var events = await Collect(loop, Run(completer, AutonomyLevel.ReviewEach, denyAll));

        var finished = Assert.Single(events.OfType<ToolFinished>());
        Assert.False(finished.Result.Success);
        Assert.Contains("denied", finished.Result.Content);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "edit.txt")));
        // A denial stops the whole run rather than letting the model try another way.
        Assert.Equal(RunEndReason.Stopped, Assert.IsType<RunFinished>(events[^1]).Reason);
    }

    [Fact]
    public async Task Denial_reason_is_fed_back_and_stops_the_run()
    {
        // The model would make a second tool call if it continued; it must not.
        var completer = new FakeCompleter(
            Call(("c1", "edit_file", "{\"path\":\"edit.txt\",\"old_text\":\"hello\",\"new_text\":\"bye\"}")),
            Call(("c2", "edit_file", "{\"path\":\"edit.txt\",\"old_text\":\"hello\",\"new_text\":\"other\"}")));
        var loop = new AgentLoop(completer, _registry);

        var deny = new ApprovalHandler((_, _) => Task.FromResult(new ApprovalDecision(false, "use the API instead")));
        var events = await Collect(loop, Run(completer, AutonomyLevel.ReviewEach, deny));

        var finished = Assert.Single(events.OfType<ToolFinished>());
        Assert.Contains("use the API instead", finished.Result.Content);
        var done = Assert.IsType<RunFinished>(events[^1]);
        Assert.Equal(RunEndReason.Stopped, done.Reason);
        Assert.Equal("use the API instead", done.Note);
    }

    [Fact]
    public async Task Approved_write_shows_preview_and_applies()
    {
        var completer = new FakeCompleter(
            Call(("c1", "edit_file", "{\"path\":\"edit.txt\",\"old_text\":\"hello\",\"new_text\":\"bye\"}")),
            Stop("Done."));
        var loop = new AgentLoop(completer, _registry);

        ApprovalRequest? seen = null;
        var approve = new ApprovalHandler((req, _) => { seen = req; return Task.FromResult(new ApprovalDecision(true)); });
        var events = await Collect(loop, Run(completer, AutonomyLevel.ReviewEach, approve));

        Assert.NotNull(seen);
        Assert.Equal(PermissionTier.Write, seen!.Tier);
        Assert.Contains("+ bye", seen.Preview?.Diff);
        Assert.Equal("bye", File.ReadAllText(Path.Combine(_root, "edit.txt")));
    }

    [Fact]
    public async Task Length_truncation_retries_rather_than_ending()
    {
        // The model is cut off (finish=Length) with prose but no tool calls; the loop
        // must regenerate the step (at a higher cap) instead of treating it as answered.
        var completer = new FakeCompleter(
            Length("…Doing both now."),   // truncated: would be a false "answer"
            Stop("All done."));           // the retry completes cleanly
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        var done = Assert.IsType<RunFinished>(events[^1]);
        Assert.Equal(RunEndReason.Answered, done.Reason);
        // The last assistant step is the retried (complete) one, not the fragment.
        Assert.Equal("All done.", events.OfType<AssistantStep>().Last().Content);
    }

    [Fact]
    public async Task Persistent_length_truncation_pauses_for_budget()
    {
        // Always truncated: after growing to the ceiling it must pause (Budget), not
        // report the fragment as a finished answer.
        var completer = new FakeCompleter { Always = Length("partial…") };
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        Assert.Equal(RunEndReason.Budget, Assert.IsType<RunFinished>(events[^1]).Reason);
    }

    [Fact]
    public async Task Loop_detection_pauses_on_repeated_calls()
    {
        var completer = new FakeCompleter { Always = Call(("c", "list_dir", "{}")) };
        var loop = new AgentLoop(completer, _registry, new AgentOptions { LoopRepeatThreshold = 3, MaxSteps = 20 });

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        Assert.Equal(RunEndReason.LoopDetected, Assert.IsType<RunFinished>(events[^1]).Reason);
    }

    [Fact]
    public async Task Step_limit_pauses_for_budget()
    {
        // Each step makes a distinct call so loop detection doesn't fire first.
        var i = 0;
        var completer = new SequentialCompleter(() => Call(($"c{i}", "line_count", $"{{\"path\":\"hello.txt\"}}{new string(' ', i++)}")));
        var loop = new AgentLoop(completer, _registry, new AgentOptions { MaxSteps = 3 });

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        Assert.Equal(RunEndReason.Budget, Assert.IsType<RunFinished>(events[^1]).Reason);
    }

    private sealed class SequentialCompleter : IChatCompleter
    {
        private readonly Func<ChatTurnResult> _next;
        public SequentialCompleter(Func<ChatTurnResult> next) => _next = next;
        public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default) =>
            Task.FromResult(_next());
    }
}
