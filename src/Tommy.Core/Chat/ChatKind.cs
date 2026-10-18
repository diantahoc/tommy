namespace Tommy.Core.Chat;

/// <summary>
/// The two chat sections. A <see cref="Chat"/> is standalone (optional read-only
/// folder, no shell); a <see cref="Code"/> chat binds to a workspace folder
/// (shell root, read-write).
/// </summary>
public enum ChatKind
{
    Chat,
    Code
}
