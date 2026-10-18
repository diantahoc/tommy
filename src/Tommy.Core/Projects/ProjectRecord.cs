using Tommy.Core.Chat;

namespace Tommy.Core.Projects;

/// <summary>
/// An in-app project bundle (<c>project.json</c>): a default parameter set new
/// chats inherit, plus (later) shared MCP instances, context and memory. Projects
/// have no disk folder of their own and are orthogonal to workspaces.
/// </summary>
public sealed class ProjectRecord
{
    public required string Id { get; set; }
    public string Name { get; set; } = "Project";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Default parameters chats in this project inherit.</summary>
    public ChatParameters Defaults { get; set; } = new();
}
