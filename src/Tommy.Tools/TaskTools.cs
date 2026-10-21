using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Agent;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Lets the agent record and update its task state — a one-line objective, an ordered
/// checklist, and key findings. The host persists each call to the chat's event log and
/// re-injects the latest state into context, so the agent remembers what it was doing
/// across steps, a mid-work pause, and an app restart. Each call carries the COMPLETE
/// current state and replaces the previous one.
/// </summary>
[Tool]
public sealed class TaskStateTool : ITool
{
    public string Name => "update_task_state";

    public string Description =>
        "Record or update your task state so you remember what you're doing across steps, pauses, and restarts. " +
        "Call it when you start a task of more than a couple of steps, and again whenever a step's status changes. " +
        "Pass the COMPLETE current state each time — it replaces the previous one: a one-line objective, the full " +
        "ordered checklist (each step with a status), and key findings/paths/values to carry forward. " +
        "Keep exactly one step 'active'. Send an empty state to clear it when the task is done.";

    // Pure bookkeeping — no workspace side effects — so it never needs approval.
    public PermissionTier Tier => PermissionTier.Read;

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("objective", ToolSchema.Str("One line: what you're trying to accomplish."), false),
        ("steps", StepsSchema(), false),
        ("notes", ToolSchema.StrArray("Key findings, paths, and values to carry forward — the working set."), false));

    private static JsonObject StepsSchema() => new()
    {
        ["type"] = "array",
        ["description"] = "The ordered checklist, each item { text, status }.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject { ["type"] = "string" },
                ["status"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("pending", "active", "done"),
                    ["description"] = "pending | active | done (keep exactly one active)."
                }
            },
            ["required"] = new JsonArray("text")
        }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var state = Parse(args);
        ctx.RecordTaskState?.Invoke(state);
        var body = state.IsEmpty ? "Task state cleared." : "Task state updated:\n" + state.Render();
        return Task.FromResult(ToolResult.Ok(body, ToolResultKind.Text, "task state"));
    }

    /// <summary>Builds a <see cref="TaskState"/> from the (lenient) tool arguments.</summary>
    public static TaskState Parse(JsonElement args)
    {
        var state = new TaskState { Objective = (Args.Str(args, "objective") ?? string.Empty).Trim() };

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("steps", out var steps)
            && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in steps.EnumerateArray())
            {
                var text = StepText(item);
                if (string.IsNullOrWhiteSpace(text)) continue;
                state.Steps.Add(new TaskStep { Text = text!.Trim(), Status = StepStatus(item) });
            }
        }

        foreach (var note in Args.StrList(args, "notes"))
            state.Notes.Add(note.Trim());

        return state;
    }

    private static string? StepText(JsonElement item) => item.ValueKind switch
    {
        // Accept both { text, status } objects and a bare string step.
        JsonValueKind.Object when item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString(),
        JsonValueKind.String => item.GetString(),
        _ => null
    };

    private static TaskStepStatus StepStatus(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("status", out var s)
        && s.ValueKind == JsonValueKind.String
            ? ParseStatus(s.GetString())
            : TaskStepStatus.Pending;

    private static TaskStepStatus ParseStatus(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "active" or "in_progress" or "in-progress" or "doing" or "current" => TaskStepStatus.Active,
        "done" or "complete" or "completed" or "finished" => TaskStepStatus.Done,
        _ => TaskStepStatus.Pending
    };
}
