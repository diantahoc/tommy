using Tommy.Engine.Dto;

namespace Tommy.Engine.Agent;

/// <summary>
/// Shrinks an in-run message list that has outgrown the model's context window.
/// The bulk is tool output (large query results, file reads) accumulated over many
/// steps, so this collapses the content of the <b>older</b> tool exchanges to a
/// short placeholder while preserving message structure — the tool_call ↔ tool
/// result ids stay paired, so the request stays valid — and the most recent
/// exchanges stay intact for the model to keep working from.
/// </summary>
internal static class ContextCompactor
{
    public const string DroppedToolNote = "[earlier tool output dropped to fit the context window]";

    /// <summary>
    /// Compacts <paramref name="messages"/> in place. Keeps the system message, every
    /// prose turn, and the newest <paramref name="keepRecentTools"/> tool results at
    /// full size; older tool results become a short note and older assistant
    /// tool-call turns shed their (now dangling) prose. Returns true if anything was
    /// shortened — false when there is nothing left to compact.
    /// </summary>
    public static bool Compact(List<ChatMessageDto> messages, int keepRecentTools = 3)
    {
        var toolIdx = new List<int>();
        for (var i = 0; i < messages.Count; i++)
            if (string.Equals(messages[i].Role, "tool", StringComparison.Ordinal))
                toolIdx.Add(i);

        if (toolIdx.Count == 0) return false;

        var keepFrom = Math.Max(0, toolIdx.Count - keepRecentTools);
        var changed = false;

        // Collapse the content of every tool result except the most recent few.
        for (var k = 0; k < keepFrom; k++)
        {
            var m = messages[toolIdx[k]];
            if (!string.IsNullOrEmpty(m.Content) && m.Content != DroppedToolNote)
            {
                m.Content = DroppedToolNote;
                changed = true;
            }
        }

        // Older assistant turns that carried tool calls: their prose is noise once
        // the results are gone. Drop it but keep ToolCalls so the ids still pair up.
        var firstKept = keepFrom < toolIdx.Count ? toolIdx[keepFrom] : messages.Count;
        for (var i = 0; i < firstKept; i++)
        {
            var m = messages[i];
            if (string.Equals(m.Role, "assistant", StringComparison.Ordinal)
                && m.ToolCalls is { Count: > 0 }
                && !string.IsNullOrEmpty(m.Content))
            {
                m.Content = null;
                changed = true;
            }
        }

        return changed;
    }
}
