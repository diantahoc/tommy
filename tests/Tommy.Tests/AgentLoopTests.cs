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

    /// <summary>One tool round, then an output-token cutoff on every later call.</summary>
    private sealed class ToolThenLength : IChatCompleter
    {
        private bool _first = true;
        public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            if (_first) { _first = false; return Task.FromResult(Call(("c1", "line_count", "{\"path\":\"hello.txt\"}"))); }
            return Task.FromResult(Length("partial…"));
        }
    }

    [Fact]
    public async Task Budget_pause_hands_back_the_working_set_to_continue()
    {
        // A tool round, then a persistent output-limit cutoff. The pause must carry the
        // run's working set — the tool call, its result, and the partial answer — minus
        // the system prompt, so a continuation resumes instead of redoing the tool call.
        var completer = new ToolThenLength();
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        var done = Assert.IsType<RunFinished>(events[^1]);
        Assert.Equal(RunEndReason.Budget, done.Reason);
        Assert.NotNull(done.ResumeState);
        Assert.DoesNotContain(done.ResumeState!, m => m.Role == "system");
        Assert.Contains(done.ResumeState!, m => m.ToolCalls is { Count: > 0 }
            && m.ToolCalls[0].Function?.Name == "line_count");
        Assert.Contains(done.ResumeState!, m => m.Role == "tool" && m.Content == "3");
        Assert.Contains(done.ResumeState!, m => m.Role == "assistant" && m.Content == "partial…");
    }

    [Fact]
    public async Task Answered_run_carries_no_resume_state()
    {
        var completer = new FakeCompleter(Stop("done"));
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        Assert.Null(Assert.IsType<RunFinished>(events[^1]).ResumeState);
    }

    // --- Context window: tool-output cap + overflow compaction ---------------

    private sealed class ScriptedCompleter : IChatCompleter
    {
        private readonly Queue<ChatTurnResult> _responses;
        private readonly int _throwOverflowOnCall; // 1-based (counts step calls only); 0 = never
        private readonly string _summaryContent;
        private int _calls;
        public List<ChatCompletionRequest> Seen { get; } = new();

        /// <param name="summaryContent">What the summarize-on-compact call returns; "" makes it fall back to structural compaction.</param>
        public ScriptedCompleter(int throwOverflowOnCall, string summaryContent, params ChatTurnResult[] responses)
        {
            _throwOverflowOnCall = throwOverflowOnCall;
            _summaryContent = summaryContent;
            _responses = new Queue<ChatTurnResult>(responses);
        }

        public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Seen.Add(request);
            // The summarize-on-compact call is identified by its system prompt; it does
            // not consume a scripted step response or count toward the overflow trigger.
            var isSummary = request.Messages.Count > 0
                && (request.Messages[0].Content?.Contains("compress", StringComparison.OrdinalIgnoreCase) ?? false);
            if (isSummary)
                return Task.FromResult(new ChatTurnResult(_summaryContent, null, FinishReason.Stop, null, null, Array.Empty<ToolCall>()));

            if (++_calls == _throwOverflowOnCall)
                throw StrataRequestException.From(System.Net.HttpStatusCode.BadRequest,
                    "request exceeds the context window: prompt (134983 tokens) + max tokens (2048) exceeds the context (131072)");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    [Fact]
    public async Task Large_tool_output_is_capped_before_the_model_sees_it()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', 25_000));
        var completer = new ScriptedCompleter(0, "unused",
            Call(("c1", "read_file", "{\"path\":\"big.txt\"}")),
            Stop("done"));
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        // The UI still sees the full result…
        var finished = Assert.Single(events.OfType<ToolFinished>());
        Assert.True(finished.Result.Content.Length >= 25_000);
        // …but the copy fed back to the model on the next request is capped.
        var toolMsg = completer.Seen[1].Messages.Last(m => m.Role == "tool");
        Assert.True(toolMsg.Content!.Length < 21_000, $"expected capped, got {toolMsg.Content!.Length}");
        Assert.Contains("truncated", toolMsg.Content);
    }

    [Fact]
    public async Task Context_overflow_summarizes_older_steps_and_continues()
    {
        // Four tool rounds accumulate, then the engine rejects the 5th request for
        // overflow; the loop summarizes older steps with the model and the retry succeeds.
        var completer = new ScriptedCompleter(5, "Earlier: inspected files and ran queries.",
            Call(("c1", "line_count", "{\"path\":\"hello.txt\"}")),
            Call(("c2", "list_dir", "{}")),
            Call(("c3", "line_count", "{\"path\":\"hello.txt\"}")),
            Call(("c4", "list_dir", "{}")),
            Stop("done")); // dequeued by the retry after the overflow
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        var compacted = Assert.Single(events.OfType<ContextCompacted>());
        Assert.Equal("summarized earlier steps", compacted.Note);
        Assert.Equal(RunEndReason.Answered, Assert.IsType<RunFinished>(events[^1]).Reason);
        // The retried request carries the model's summary in place of the older steps.
        Assert.Contains(completer.Seen[^1].Messages,
            m => m.Content is not null && m.Content.Contains("Summary of earlier steps"));
    }

    [Fact]
    public async Task Context_overflow_falls_back_to_structural_drop_when_summary_fails()
    {
        // The summarize call returns nothing (e.g. it overflowed too); the loop falls
        // back to the deterministic drop of older tool output and still continues.
        var completer = new ScriptedCompleter(5, "", // empty summary → fall back
            Call(("c1", "line_count", "{\"path\":\"hello.txt\"}")),
            Call(("c2", "list_dir", "{}")),
            Call(("c3", "line_count", "{\"path\":\"hello.txt\"}")),
            Call(("c4", "list_dir", "{}")),
            Stop("done"));
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        var compacted = Assert.Single(events.OfType<ContextCompacted>());
        Assert.Equal("dropped older tool output", compacted.Note);
        Assert.Equal(RunEndReason.Answered, Assert.IsType<RunFinished>(events[^1]).Reason);
        Assert.Contains(completer.Seen[^1].Messages,
            m => m.Content is not null && m.Content.Contains("dropped to fit the context window"));
    }

    [Fact]
    public async Task Context_overflow_with_nothing_to_compact_errors_cleanly()
    {
        // Overflow on the very first request: no tool output exists to shrink, so the
        // run ends with a clear error rather than throwing a raw HTTP exception.
        var completer = new ScriptedCompleter(1, "unused", Stop("unused"));
        var loop = new AgentLoop(completer, _registry);

        var events = await Collect(loop, Run(completer, AutonomyLevel.Auto, null));

        Assert.Equal(RunEndReason.Error, Assert.IsType<RunFinished>(events[^1]).Reason);
    }
}
