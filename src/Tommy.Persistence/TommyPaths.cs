namespace Tommy.Persistence;

/// <summary>
/// Canonical on-disk locations. Defaults to <c>~/.tommy</c>; the
/// <c>TOMMY_HOME</c> environment variable overrides the root (used by tests and
/// for a portable install).
/// </summary>
public static class TommyPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string SecretsFile => Path.Combine(Root, "secrets.json");
    public static string SkillsDir => Path.Combine(Root, "skills");
    public static string ProjectsDir => Path.Combine(Root, "projects");
    public static string ChatsDir => Path.Combine(Root, "chats");
    public static string WorkspacesDir => Path.Combine(Root, "workspaces");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("TOMMY_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".tommy");
    }
}
