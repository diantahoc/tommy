using Tommy.Core.Agent;
using Tommy.Core.Events;

namespace Tommy.Engine.Agent;

/// <summary>
/// Folds a chat's event log down to the agent's <b>current</b> task state. Updates are
/// written as full snapshots, so the fold is simply the most recent <see cref="TaskStateEvent"/>.
/// An empty final snapshot means the agent cleared the task (it finished), so the current
/// state is <c>null</c>.
/// </summary>
public static class TaskStateProjection
{
    /// <summary>The current task state, or null when none was recorded or it was cleared.</summary>
    public static TaskState? Latest(IReadOnlyList<ChatEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (events[i] is TaskStateEvent e)
                return e.State.IsEmpty ? null : e.State;
        return null;
    }
}
