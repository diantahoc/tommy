using System.Text.Json;
using Tommy.Core.Agent;
using Tommy.Core.Events;
using Tommy.Core.Tools;
using Tommy.Engine.Agent;
using Tommy.Persistence.Storage;
using Tommy.Tools;

namespace Tommy.Tests;

/// <summary>
/// The durable task-state record: the agent writes a full snapshot (objective + checklist
/// + findings) via update_task_state, the host logs it, and the latest snapshot folds back
/// into context — so the agent remembers what it was doing across pauses and restarts.
/// </summary>
public class TaskStateTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // --- Projection (fold the log to the current state) --------------------

    [Fact]
    public void Latest_returns_the_most_recent_snapshot()
    {
        var events = new List<ChatEvent>
        {
            new TaskStateEvent { State = new TaskState { Objective = "first" } },
            new AssistantEvent { Content = "working" },
            new TaskStateEvent { State = new TaskState { Objective = "second" } }
        };

        Assert.Equal("second", TaskStateProjection.Latest(events)!.Objective);
    }

    [Fact]
    public void Latest_is_null_when_nothing_was_recorded()
    {
        var events = new List<ChatEvent> { new UserEvent { Text = "hi" }, new AssistantEvent { Content = "hello" } };
        Assert.Null(TaskStateProjection.Latest(events));
    }

    [Fact]
    public void An_empty_final_snapshot_clears_the_task()
    {
        var events = new List<ChatEvent>
        {
            new TaskStateEvent { State = new TaskState { Objective = "doing a thing" } },
            new TaskStateEvent { State = new TaskState() } // model cleared it when finished
        };

        Assert.Null(TaskStateProjection.Latest(events));
    }

    // --- Tool argument parsing ---------------------------------------------

    [Fact]
    public void Tool_parses_objective_steps_and_notes()
    {
        var args = Args("""
            {"objective":"Fix bug","steps":[
                {"text":"repro","status":"done"},
                {"text":"patch","status":"active"},
                {"text":"test"}],
             "notes":["bug is in Foo.cs","repro value 42"]}
        """);

        var state = TaskStateTool.Parse(args);

        Assert.Equal("Fix bug", state.Objective);
        Assert.Equal(3, state.Steps.Count);
        Assert.Equal(TaskStepStatus.Done, state.Steps[0].Status);
        Assert.Equal(TaskStepStatus.Active, state.Steps[1].Status);
        Assert.Equal(TaskStepStatus.Pending, state.Steps[2].Status);   // status omitted -> pending
        Assert.Equal(2, state.Notes.Count);
    }

    [Fact]
    public void Tool_accepts_bare_string_steps()
    {
        var state = TaskStateTool.Parse(Args("""{"objective":"o","steps":["a","b"]}"""));

        Assert.Equal(2, state.Steps.Count);
        Assert.All(state.Steps, s => Assert.Equal(TaskStepStatus.Pending, s.Status));
    }

    [Fact]
    public async Task Tool_records_state_through_the_sink()
    {
        TaskState? captured = null;
        var ctx = new ToolContext(".", RecordTaskState: s => captured = s);
        var tool = new TaskStateTool();

        var result = await tool.ExecuteAsync(
            Args("""{"objective":"ship it","steps":[{"text":"do","status":"active"}]}"""), ctx, default);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("ship it", captured!.Objective);
        Assert.Equal(TaskStepStatus.Active, captured.Steps.Single().Status);
    }

    [Fact]
    public void Tool_is_read_tier_so_it_never_prompts()
    {
        Assert.Equal(PermissionTier.Read, new TaskStateTool().Tier);
    }

    // --- Render ------------------------------------------------------------

    [Fact]
    public void Render_marks_each_step_status()
    {
        var state = new TaskState
        {
            Objective = "Obj",
            Steps =
            {
                new TaskStep { Text = "a", Status = TaskStepStatus.Done },
                new TaskStep { Text = "b", Status = TaskStepStatus.Active },
                new TaskStep { Text = "c" }
            },
            Notes = { "note1" }
        };

        var r = state.Render();

        Assert.Contains("Objective: Obj", r);
        Assert.Contains("[x] a", r);
        Assert.Contains("[>] b", r);
        Assert.Contains("[ ] c", r);
        Assert.Contains("- note1", r);
    }

    // --- Persistence round-trip --------------------------------------------

    [Fact]
    public void TaskStateEvent_round_trips_through_the_event_log()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tommy-taskstate-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new EventStore(path);
            store.Append(new TaskStateEvent
            {
                State = new TaskState
                {
                    Objective = "persist me",
                    Steps = { new TaskStep { Text = "step", Status = TaskStepStatus.Done } },
                    Notes = { "a finding" }
                }
            });

            var read = store.Read().ToList();
            var e = Assert.IsType<TaskStateEvent>(Assert.Single(read));
            Assert.Equal("persist me", e.State.Objective);
            Assert.Equal(TaskStepStatus.Done, e.State.Steps.Single().Status);  // enum survives as a string
            Assert.Equal("a finding", e.State.Notes.Single());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
