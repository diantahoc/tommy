namespace Tommy.Core.Runtime;

/// <summary>
/// The canonical Tommy home directory — <c>~/.tommy</c> by default, overridden by
/// the <c>TOMMY_HOME</c> environment variable (tests, portable installs). This is
/// the single source of truth for the root; <c>Tommy.Persistence.TommyPaths</c>
/// delegates here so Core-layer code (notably <see cref="IntegratedTools"/>) can
/// resolve the same location without referencing the persistence layer.
/// </summary>
public static class TommyHome
{
    public static string Root { get; } = Resolve();

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("TOMMY_HOME");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".tommy");
    }
}
