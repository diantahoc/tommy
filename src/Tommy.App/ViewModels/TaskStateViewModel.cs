using System.Collections.Generic;
using System.Linq;
using Tommy.Core.Agent;

namespace Tommy.App.ViewModels;

/// <summary>
/// Read-only view of the agent's current <see cref="TaskState"/> for the task pane:
/// the objective, the checklist, and the findings it's carrying forward. The agent
/// owns the state (via the update_task_state tool); the pane only displays it.
/// </summary>
public sealed class TaskStateViewModel
{
    public TaskStateViewModel(TaskState state)
    {
        Objective = state.Objective.Trim();
        Steps = state.Steps.Select(s => new TaskStepViewModel(s)).ToList();
        Notes = state.Notes.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();

        var done = Steps.Count(s => s.IsDone);
        ProgressLabel = Steps.Count > 0 ? $"{done}/{Steps.Count} done" : string.Empty;
    }

    public string Objective { get; }
    public bool HasObjective => Objective.Length > 0;

    public IReadOnlyList<TaskStepViewModel> Steps { get; }
    public bool HasSteps => Steps.Count > 0;

    public IReadOnlyList<string> Notes { get; }
    public bool HasNotes => Notes.Count > 0;

    public string ProgressLabel { get; }
    public bool HasProgress => ProgressLabel.Length > 0;
}

/// <summary>One checklist row. Status drives the glyph and the row styling in XAML.</summary>
public sealed class TaskStepViewModel
{
    public TaskStepViewModel(TaskStep step)
    {
        Text = step.Text.Trim();
        Status = step.Status;
    }

    public string Text { get; }
    public TaskStepStatus Status { get; }

    public bool IsPending => Status == TaskStepStatus.Pending;
    public bool IsActive => Status == TaskStepStatus.Active;
    public bool IsDone => Status == TaskStepStatus.Done;

    public string Glyph => Status switch
    {
        TaskStepStatus.Done => "✓",    // ✓
        TaskStepStatus.Active => "▸",  // ▸
        _ => "○"                        // ○
    };
}
