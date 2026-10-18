namespace Tommy.Core.Workspaces;

/// <summary>
/// A workspace (<c>workspace.json</c>): a selected code folder plus the chats
/// bound to it. The id is a hash of the folder path, and the folder is
/// referenced, never copied in. Workspace memory is keyed by this folder.
/// </summary>
public sealed class WorkspaceRecord
{
    /// <summary>Hash of the normalized folder path (see WorkspaceRepository).</summary>
    public required string Id { get; set; }

    public required string FolderPath { get; set; }

    public List<string> MemberChatIds { get; set; } = new();

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}
