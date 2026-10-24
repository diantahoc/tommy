using Tommy.Core.Tools;

namespace Tommy.Engine.Agent;

/// <summary>What the model reviewer is shown about a command it must judge.</summary>
public sealed record CommandReviewRequest(
    string ToolName,
    string ArgumentsJson,
    PermissionTier Tier,
    ToolPreview? Preview);

/// <summary>
/// The reviewer's verdict: <see cref="Allow"/> runs the command silently; otherwise
/// it is escalated to the user with <see cref="Reason"/> shown on the prompt.
/// </summary>
public sealed record CommandReviewVerdict(bool Allow, string? Reason)
{
    /// <summary>
    /// Parse the model's one-line verdict ("ALLOW" or "ESCALATE: &lt;reason&gt;").
    /// Anything empty or unexpected escalates — the safe default.
    /// </summary>
    public static CommandReviewVerdict Parse(string? reply)
    {
        var line = reply?.Trim();
        if (string.IsNullOrEmpty(line))
            return new CommandReviewVerdict(false, "the safety review returned nothing");

        // First non-empty line only; models sometimes add trailing prose.
        var brk = line.IndexOfAny(new[] { '\r', '\n' });
        if (brk >= 0) line = line[..brk].Trim();

        if (line.StartsWith("ALLOW", StringComparison.OrdinalIgnoreCase))
            return new CommandReviewVerdict(true, null);

        if (line.StartsWith("ESCALATE", StringComparison.OrdinalIgnoreCase))
        {
            var colon = line.IndexOf(':');
            var reason = colon >= 0 && colon + 1 < line.Length ? line[(colon + 1)..].Trim() : null;
            return new CommandReviewVerdict(false, string.IsNullOrWhiteSpace(reason) ? null : reason);
        }

        return new CommandReviewVerdict(false, "the safety review was inconclusive");
    }
}

/// <summary>
/// Reviews a command before it runs under the AutoReviewed autonomy level. A null
/// reviewer (or any failure) escalates — the safe default.
/// </summary>
public delegate Task<CommandReviewVerdict> CommandReviewer(CommandReviewRequest request, CancellationToken ct);
