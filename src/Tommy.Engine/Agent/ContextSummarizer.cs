using System.Text;
using Tommy.Core.Diagnostics;
using Tommy.Engine.Dto;

namespace Tommy.Engine.Agent;

/// <summary>
/// Compacts an over-long in-run history by asking the model itself to summarize the
/// <b>older</b> steps into a short note, then replacing those messages with it. The
/// system prompt, the prose history, and the most recent few tool turns are kept
/// intact. Whole turns are replaced together (an assistant tool-call message and its
/// results), so no tool_call is left without its result and the request stays valid.
///
/// The summary request is itself bounded and can also overflow; when it fails the
/// caller falls back to the deterministic <see cref="ContextCompactor"/>.
/// </summary>
internal static class ContextSummarizer
{
    private const int KeepRecentTurns = 3;
    private const int SummaryMaxTokens = 640;

    /// <summary>Ceiling the budget grows to when a reasoning model spends it all thinking.</summary>
    private const int SummaryMaxTokenCeiling = 2048;

    private const string SummarySystem =
        "You compress an AI coding agent's working transcript. Produce a concise summary " +
        "(at most ~180 words) of the steps below, preserving: what the agent did, the key " +
        "findings and concrete values, file paths and identifiers, and the current state — " +
        "enough to continue the task without the original detail. Output only the summary.";

    /// <summary>
    /// Summarizes the older portion of <paramref name="messages"/> in place. Returns
    /// true on success; false when there is too little to summarize or the summary
    /// call failed (including its own overflow), leaving the list untouched.
    /// </summary>
    public static async Task<bool> TrySummarizeAsync(
        IChatCompleter completer, string model, List<ChatMessageDto> messages, CancellationToken ct)
    {
        // Assistant turns that carried tool calls mark the start of each tool round.
        var turnIdx = new List<int>();
        for (var i = 0; i < messages.Count; i++)
            if (string.Equals(messages[i].Role, "assistant", StringComparison.Ordinal)
                && messages[i].ToolCalls is { Count: > 0 })
                turnIdx.Add(i);

        if (turnIdx.Count <= KeepRecentTurns) return false;

        var summarizeStart = turnIdx[0];
        var keepStart = turnIdx[turnIdx.Count - KeepRecentTurns];
        var count = keepStart - summarizeStart;
        if (count <= 0) return false;

        var transcript = Flatten(messages.GetRange(summarizeStart, count));
        if (transcript.Length == 0) return false;

        // Low reasoning effort (a reducer needs little), and grow the budget on a
        // recoverable length cut-off so a reasoning model doesn't spend the whole budget
        // thinking and return empty — which would needlessly drop us to structural drop.
        var maxTokens = SummaryMaxTokens;
        string? summary;
        while (true)
        {
            ChatTurnResult res;
            try
            {
                res = await completer.CompleteAsync(new ChatCompletionRequest
                {
                    Model = model,
                    Messages = new List<ChatMessageDto>
                    {
                        new() { Role = "system", Content = SummarySystem },
                        new() { Role = "user", Content = transcript }
                    },
                    MaxTokens = maxTokens,
                    Temperature = 0.2,
                    Stream = false,
                    ReasoningEffort = "low"
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Overflow of the summary call itself, or any other failure: let the caller
                // fall back to the deterministic drop rather than failing the whole run.
                Log.Warn($"context summary failed; falling back to structural compaction: {ex.Message}");
                return false;
            }

            summary = res.Content?.Trim();
            if (!string.IsNullOrEmpty(summary)) break;

            if (res.IsRecoverableLength && maxTokens < SummaryMaxTokenCeiling)
            {
                maxTokens = Math.Min(maxTokens * 2, SummaryMaxTokenCeiling);
                continue;
            }
            return false; // produced nothing; caller falls back to deterministic drop
        }

        messages.RemoveRange(summarizeStart, count);
        messages.Insert(summarizeStart, new ChatMessageDto
        {
            Role = "assistant",
            Content = "[Summary of earlier steps, compacted to fit the context window]\n" + summary
        });
        return true;
    }

    /// <summary>Flattens a run of messages into a plain-text transcript for summarizing.</summary>
    private static string Flatten(List<ChatMessageDto> region)
    {
        var sb = new StringBuilder();
        foreach (var m in region)
        {
            if (string.Equals(m.Role, "assistant", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(m.Content))
                    sb.Append("Assistant: ").AppendLine(m.Content);
                if (m.ToolCalls is { Count: > 0 })
                    foreach (var tc in m.ToolCalls)
                        sb.Append("Called ").Append(tc.Function?.Name).Append('(')
                          .Append(tc.Function?.Arguments).AppendLine(")");
            }
            else if (string.Equals(m.Role, "tool", StringComparison.Ordinal))
            {
                sb.Append("Result of ").Append(m.Name ?? "tool").Append(": ").AppendLine(m.Content);
            }
            else if (!string.IsNullOrEmpty(m.Content))
            {
                sb.Append(m.Role).Append(": ").AppendLine(m.Content);
            }
        }
        return sb.ToString().Trim();
    }
}
