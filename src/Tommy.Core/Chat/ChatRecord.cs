namespace Tommy.Core.Chat;

/// <summary>
/// The persisted chat header (<c>chat.json</c>): kind, params, system prompt,
/// and optional project / workspace / read-only-folder references.
/// </summary>
public sealed class ChatRecord
{
    public required string Id { get; set; }
    public ChatKind Kind { get; set; }
    public string Title { get; set; } = "New chat";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Project this chat belongs to, if any.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Workspace (code chat) this chat binds to, if any.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>A chat-section read-only attached folder, if any (no shell).</summary>
    public string? ReadOnlyFolder { get; set; }

    public ChatParameters Parameters { get; set; } = new();
}
