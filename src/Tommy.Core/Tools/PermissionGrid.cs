namespace Tommy.Core.Tools;

/// <summary>The three autonomy presets over the permission tiers.</summary>
public enum AutonomyLevel
{
    /// <summary>Everything but reads asks first.</summary>
    ReviewEach,

    /// <summary>Only destructive ops ask; writes/exec/network auto-allow.</summary>
    ReviewDestructive,

    /// <summary>Only destructive ops ask (they always do); the rest auto-allow.</summary>
    Auto,

    /// <summary>
    /// Like Auto, but each command/network action is first reviewed by the model:
    /// safe ones run, risky ones (networking, path escape, sensitive data, destructive
    /// intent) are escalated to the user. Destructive still always asks.
    /// </summary>
    AutoReviewed
}

/// <summary>What happens when a tool of a given tier is invoked.</summary>
public enum PermissionAction
{
    /// <summary>Run without prompting.</summary>
    Allow,

    /// <summary>Prompt the user first (writes show a diff).</summary>
    Ask,

    /// <summary>Have the model review the command first, then allow it or escalate to Ask.</summary>
    Review
}

/// <summary>
/// Maps (tier, autonomy) to an action — the single grid every tool inherits.
/// Destructive always asks, regardless of autonomy. A per-tool override can
/// force a specific action for the exceptions.
/// </summary>
public static class PermissionGrid
{
    public static PermissionAction Resolve(
        PermissionTier tier,
        AutonomyLevel autonomy,
        PermissionAction? toolOverride = null)
    {
        if (toolOverride is { } forced)
            return forced;

        return tier switch
        {
            PermissionTier.Read => PermissionAction.Allow,
            // Destructive always asks — a hard floor the reviewed mode can't lower.
            PermissionTier.Destructive => PermissionAction.Ask,
            // Writes stay in the workspace sandbox; reviewed mode treats them like Auto.
            PermissionTier.Write =>
                autonomy == AutonomyLevel.ReviewEach ? PermissionAction.Ask : PermissionAction.Allow,
            // Exec (arbitrary code) and Network are the reviewed tiers: AutoReviewed
            // sends them to the model reviewer; the other presets keep their behavior.
            PermissionTier.Network or PermissionTier.Exec => autonomy switch
            {
                AutonomyLevel.ReviewEach => PermissionAction.Ask,
                AutonomyLevel.AutoReviewed => PermissionAction.Review,
                _ => PermissionAction.Allow
            },
            _ => PermissionAction.Ask
        };
    }
}
