using System.Text;

namespace Tommy.Core.Agent;

/// <summary>Where a single checklist step stands.</summary>
public enum TaskStepStatus
{
    Pending,
    Active,
    Done
}

/// <summary>One step in the agent's plan.</summary>
public sealed class TaskStep
{
    public string Text { get; set; } = string.Empty;
    public TaskStepStatus Status { get; set; } = TaskStepStatus.Pending;
}

/// <summary>
/// The agent's durable record of what it is doing: a one-line objective, an ordered
/// checklist, and a working set of key findings. Written as a full snapshot on every
/// update (a <c>TaskStateEvent</c> in the chat's event log) and re-injected into the
/// system prompt, so the agent remembers the task across steps, a mid-work pause, and
/// an app restart — the log, not the live context window, is the source of truth.
/// </summary>
public sealed class TaskState
{
    public string Objective { get; set; } = string.Empty;

    public List<TaskStep> Steps { get; set; } = new();

    /// <summary>Key findings, paths, and values worth carrying forward — the working set.</summary>
    public List<string> Notes { get; set; } = new();

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Objective) && Steps.Count == 0 && Notes.Count == 0;

    /// <summary>Compact markdown for the system prompt and the UI.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Objective))
            sb.Append("Objective: ").AppendLine(Objective.Trim());

        if (Steps.Count > 0)
        {
            sb.AppendLine("Plan:");
            foreach (var s in Steps)
                sb.Append(Mark(s.Status)).Append(' ').AppendLine(s.Text.Trim());
        }

        if (Notes.Count > 0)
        {
            sb.AppendLine("Findings so far:");
            foreach (var n in Notes)
                sb.Append("- ").AppendLine(n.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    private static string Mark(TaskStepStatus status) => status switch
    {
        TaskStepStatus.Done => "[x]",
        TaskStepStatus.Active => "[>]",
        _ => "[ ]"
    };
}
