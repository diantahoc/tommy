using Tommy.Core.Events;
using Tommy.Engine.Agent;

namespace Tommy.Tests;

/// <summary>
/// Reconstructing a continuation's history from the persisted event log, so a run that
/// paused mid-work can resume after a restart. The resumable region (turns since the
/// last non-Budget run end) keeps its tool scaffold; earlier turns stay prose-only.
/// </summary>
public class ResumeHistoryBuilderTests
{
    private static AssistantEvent WithCall(string id, string name, string args = "{}") =>
        new() { ToolCalls = { new ToolCallRecord { Id = id, Name = name, ArgumentsJson = args } } };

    [Fact]
    public void Reconstructs_tool_scaffold_when_the_tail_is_a_budget_pause()
    {
        var events = new List<ChatEvent>
        {
            new UserEvent { Text = "go" },
            WithCall("c1", "line_count"),
            new ToolResultEvent { CallId = "c1", Tool = "line_count", Success = true, Result = "3" },
            new AssistantEvent { Content = "partial answer" },
            new MetaEvent { Kind = "Budget" }
        };

        var (history, resumable) = ResumeHistoryBuilder.Build(events);

        Assert.True(resumable);
        Assert.Contains(history, m => m.Role == "user" && m.Content == "go");
        Assert.Contains(history, m => m.Role == "assistant"
            && m.ToolCalls is { Count: 1 } && m.ToolCalls[0].Function!.Name == "line_count");
        Assert.Contains(history, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Content == "3");
        Assert.Contains(history, m => m.Role == "assistant" && m.Content == "partial answer");
    }

    [Fact]
    public void Not_resumable_when_the_last_run_answered()
    {
        var events = new List<ChatEvent>
        {
            new UserEvent { Text = "go" },
            new AssistantEvent { Content = "done" },
            new MetaEvent { Kind = "Answered" }
        };

        var (_, resumable) = ResumeHistoryBuilder.Build(events);

        Assert.False(resumable);
    }

    [Fact]
    public void Earlier_answered_turns_stay_prose_only()
    {
        var events = new List<ChatEvent>
        {
            new UserEvent { Text = "first" },
            WithCall("a1", "list_dir"),
            new ToolResultEvent { CallId = "a1", Tool = "list_dir", Success = true, Result = "files" },
            new AssistantEvent { Content = "the answer" },
            new MetaEvent { Kind = "Answered" },
            new UserEvent { Text = "second" },
            WithCall("b1", "line_count"),
            new ToolResultEvent { CallId = "b1", Tool = "line_count", Success = true, Result = "3" },
            new AssistantEvent { Content = "partial" },
            new MetaEvent { Kind = "Budget" }
        };

        var (history, resumable) = ResumeHistoryBuilder.Build(events);

        Assert.True(resumable);
        // The answered turn's tool scaffold is not re-sent…
        Assert.DoesNotContain(history, m => m.Role == "tool" && m.ToolCallId == "a1");
        Assert.DoesNotContain(history, m => m.ToolCalls is { Count: > 0 } && m.ToolCalls[0].Id == "a1");
        // …but its prose answer is kept, and the budget run's scaffold is full-fidelity.
        Assert.Contains(history, m => m.Role == "assistant" && m.Content == "the answer");
        Assert.Contains(history, m => m.Role == "tool" && m.ToolCallId == "b1" && m.Content == "3");
    }

    [Fact]
    public void Drops_tool_calls_with_no_result()
    {
        var events = new List<ChatEvent>
        {
            new UserEvent { Text = "go" },
            WithCall("c1", "line_count"), // cut off before the result was recorded
            new MetaEvent { Kind = "Budget" }
        };

        var (history, resumable) = ResumeHistoryBuilder.Build(events);

        Assert.True(resumable);
        Assert.DoesNotContain(history, m => m.ToolCalls is { Count: > 0 });
        Assert.DoesNotContain(history, m => m.Role == "assistant"); // now-empty message dropped
    }

    [Fact]
    public void Caps_large_tool_results()
    {
        var events = new List<ChatEvent>
        {
            new UserEvent { Text = "go" },
            WithCall("c1", "read_file"),
            new ToolResultEvent { CallId = "c1", Tool = "read_file", Success = true, Result = new string('x', 25_000) },
            new AssistantEvent { Content = "partial" },
            new MetaEvent { Kind = "Budget" }
        };

        var (history, _) = ResumeHistoryBuilder.Build(events);

        var toolMsg = history.Single(m => m.Role == "tool");
        Assert.True(toolMsg.Content!.Length < 21_000, $"expected capped, got {toolMsg.Content!.Length}");
        Assert.Contains("truncated", toolMsg.Content);
    }
}
