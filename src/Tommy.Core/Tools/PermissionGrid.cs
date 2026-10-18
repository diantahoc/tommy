namespace Tommy.Core.Tools;

/// <summary>The three autonomy presets over the permission tiers.</summary>
public enum AutonomyLevel
{
    /// <summary>Everything but reads asks first.</summary>
    ReviewEach,

    /// <summary>Only destructive ops ask; writes/exec/network auto-allow.</summary>
    ReviewDestructive,

    /// <summary>Only destructive ops ask (they always do); the rest auto-allow.</summary>
    Auto
}

/// <summary>What happens when a tool of a given tier is invoked.</summary>
public enum PermissionAction
{
    /// <summary>Run without prompting.</summary>
    Allow,

    /// <summary>Prompt the user first (writes show a diff).</summary>
    Ask
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
            PermissionTier.Destructive => PermissionAction.Ask,
            PermissionTier.Write or PermissionTier.Network or PermissionTier.Exec =>
                autonomy == AutonomyLevel.ReviewEach ? PermissionAction.Ask : PermissionAction.Allow,
            _ => PermissionAction.Ask
        };
    }
}
