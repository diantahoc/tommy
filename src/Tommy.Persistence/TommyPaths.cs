using Tommy.Core.Runtime;

namespace Tommy.Persistence;

/// <summary>
/// Canonical on-disk locations. The root is <see cref="TommyHome.Root"/>
/// (<c>~/.tommy</c>, or the <c>TOMMY_HOME</c> override) — the single source of
/// truth shared with the Core runtime layer.
/// </summary>
public static class TommyPaths
{
    public static string Root => TommyHome.Root;

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string SecretsFile => Path.Combine(Root, "secrets.json");
    public static string SkillsDir => Path.Combine(Root, "skills");
    public static string ProjectsDir => Path.Combine(Root, "projects");
    public static string ChatsDir => Path.Combine(Root, "chats");
    public static string WorkspacesDir => Path.Combine(Root, "workspaces");

    /// <summary>Where a Tommy-managed SearXNG is downloaded, built, and run from.</summary>
    public static string SearxngDir => Path.Combine(Root, "searxng");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
