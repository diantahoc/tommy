using Tommy.Core.Events;
using Tommy.Engine.Dto;

namespace Tommy.Engine.Agent;

/// <summary>
/// Rebuilds a continuation's history from a chat's persisted event log, so a run that
/// paused mid-work (a <see cref="RunEndReason.Budget"/> pause) can resume after a
/// restart — not just within the live session.
///
/// Unlike the prose-only history a normal turn sends, this keeps the full working set
/// of the <b>resumable region</b>: the turns since the last run that did not end in a
/// Budget pause (an answer, a stop, an error) are replayed with their assistant tool
/// calls and tool results, so the model continues with its findings in hand instead of
/// redoing the work. Earlier turns stay prose-only, matching the normal history.
///
/// Tool results are capped the same way the live loop caps them, and tool calls with no
/// recorded result are dropped so the reconstructed request stays valid.
/// </summary>
public static class ResumeHistoryBuilder
{
    /// <summary>Per-tool-result cap fed back to the model; kept in sync with the live loop.</summary>
    private const int MaxToolResultChars = 20_000;

    /// <summary>The meta kind written when a run pauses on a budget (output cap or step limit).</summary>
    private const string BudgetKind = "Budget";

    /// <summary>
    /// Reconstructs the continuation history from <paramref name="events"/>. The
    /// <c>Resumable</c> flag is true only when the most recent run ended in a Budget
    /// pause — i.e. there is work to continue. When false, the returned history is the
    /// plain prose history and callers should not resume from it.
    /// </summary>
    public static (List<ChatMessageDto> History, bool Resumable) Build(IReadOnlyList<ChatEvent> events)
    {
        var lastMeta = LastMeta(events);
        var resumable = lastMeta is not null && string.Equals(lastMeta.Kind, BudgetKind, StringComparison.Ordinal);

        // Everything after the last run that ended on something other than a Budget pause
        // is the full-fidelity region; earlier turns stay prose-only.
        var boundary = -1;
        for (var i = 0; i < events.Count; i++)
            if (events[i] is MetaEvent m && !string.Equals(m.Kind, BudgetKind, StringComparison.Ordinal))
                boundary = i;

        var history = new List<ChatMessageDto>();
        var declaredCallIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < events.Count; i++)
        {
            var full = i > boundary; // full fidelity only inside the resumable region
            switch (events[i])
            {
                case UserEvent u when !string.IsNullOrEmpty(u.Text):
                    history.Add(new ChatMessageDto { Role = "user", Content = u.Text });
                    break;

                case AssistantEvent a:
                    var calls = full ? a.ToolCalls : null;
                    if (string.IsNullOrEmpty(a.Content) && (calls is null || calls.Count == 0))
                        break; // reasoning-only / empty turn — nothing to re-send

                    var msg = new ChatMessageDto
                    {
                        Role = "assistant",
                        Content = string.IsNullOrEmpty(a.Content) ? null : a.Content
                    };
                    if (calls is { Count: > 0 })
                    {
                        msg.ToolCalls = calls.Select(tc => new ToolCallDto
                        {
                            Id = tc.Id,
                            Type = "function",
                            Function = new FunctionCallDto { Name = tc.Name, Arguments = tc.ArgumentsJson }
                        }).ToList();
                        foreach (var tc in calls) declaredCallIds.Add(tc.Id);
                    }
                    history.Add(msg);
                    break;

                case ToolResultEvent t when full && declaredCallIds.Contains(t.CallId):
                    history.Add(new ChatMessageDto
                    {
                        Role = "tool",
                        ToolCallId = t.CallId,
                        Name = t.Tool,
                        Content = Cap(t.Result)
                    });
                    break;
            }
        }

        PruneDanglingToolCalls(history);
        return (history, resumable);
    }

    private static MetaEvent? LastMeta(IReadOnlyList<ChatEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (events[i] is MetaEvent m)
                return m;
        return null;
    }

    /// <summary>
    /// Removes assistant tool calls that never got a result (e.g. a run cut off between
    /// the call and its result), and drops an assistant message left with neither content
    /// nor calls — a dangling tool call would make the request invalid.
    /// </summary>
    private static void PruneDanglingToolCalls(List<ChatMessageDto> history)
    {
        var answered = history
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (m.ToolCalls is not { Count: > 0 }) continue;

            m.ToolCalls = m.ToolCalls.Where(tc => tc.Id is not null && answered.Contains(tc.Id)).ToList();
            if (m.ToolCalls.Count == 0)
            {
                m.ToolCalls = null;
                if (string.IsNullOrEmpty(m.Content)) history.RemoveAt(i);
            }
        }
    }

    private static string Cap(string? content)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= MaxToolResultChars)
            return content ?? string.Empty;
        return content[..MaxToolResultChars] +
            $"\n… [truncated: {content.Length:N0} chars total, first {MaxToolResultChars:N0} kept]";
    }
}
