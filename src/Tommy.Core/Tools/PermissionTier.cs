namespace Tommy.Core.Tools;

/// <summary>
/// The permission taxonomy, defined once so every tool inherits it instead of
/// scattering rules. Each tool declares a tier; the chat's autonomy level maps
/// each tier to an action (see <see cref="PermissionGrid"/>).
/// </summary>
public enum PermissionTier
{
    /// <summary>read file, list dir, git log, search.</summary>
    Read,

    /// <summary>edit file, create dir, commit.</summary>
    Write,

    /// <summary>delete dir, hard delete, git rollback.</summary>
    Destructive,

    /// <summary>web search, headless browser, git push/pull.</summary>
    Network,

    /// <summary>shell command, scratchpad run.</summary>
    Exec
}
