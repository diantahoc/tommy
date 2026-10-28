using System.Text.Json.Serialization;
using Tommy.Core.Agent;

namespace Tommy.Core.Events;

/// <summary>
/// One record in a chat's append-only event log (<c>events.jsonl</c>). Polymorphic
/// on an <c>event</c> discriminator so each line round-trips faithfully.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(UserEvent), "user")]
[JsonDerivedType(typeof(AssistantEvent), "assistant")]
[JsonDerivedType(typeof(ToolResultEvent), "tool_result")]
[JsonDerivedType(typeof(MetaEvent), "meta")]
[JsonDerivedType(typeof(TaskStateEvent), "task_state")]
[JsonDerivedType(typeof(CompactionEvent), "compaction")]
public abstract class ChatEvent
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A user turn, with any uploaded files referenced by blob hash.</summary>
public sealed class UserEvent : ChatEvent
{
    public string Text { get; set; } = string.Empty;
    public List<Attachment> Attachments { get; set; } = new();
}

/// <summary>
/// An assistant turn. Reasoning is stored but flagged — it is never re-sent into
/// the next turn's context.
/// </summary>
public sealed class AssistantEvent : ChatEvent
{
    public string Content { get; set; } = string.Empty;
    public string? Reasoning { get; set; }
    public List<ToolCallRecord> ToolCalls { get; set; } = new();
}

/// <summary>A tool result, inline or (when large) referenced by blob.</summary>
public sealed class ToolResultEvent : ChatEvent
{
    public string CallId { get; set; } = string.Empty;
    public string Tool { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? BlobRef { get; set; }
}

/// <summary>Run start/end, interrupt, compaction, budget pause, errors.</summary>
public sealed class MetaEvent : ChatEvent
{
    public string Kind { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

/// <summary>
/// A full snapshot of the agent's <see cref="TaskState"/> (objective, checklist,
/// findings), written whenever it updates its plan. Updates are whole snapshots, so
/// the most recent one is the current state.
/// </summary>
public sealed class TaskStateEvent : ChatEvent
{
    public TaskState State { get; set; } = new();
}

/// <summary>
/// A compaction checkpoint written by the /compact command: every turn logged before
/// this event is represented by <see cref="Summary"/>. On replay the earlier turns are
/// dropped and this summary stands in for them, so a long chat's history — and the next
/// run's prompt — shrinks while the thread of what happened is kept.
/// </summary>
public sealed class CompactionEvent : ChatEvent
{
    public string Summary { get; set; } = string.Empty;
}

/// <summary>An uploaded file referenced from an event by content hash.</summary>
public sealed class Attachment
{
    public string BlobHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Mime { get; set; } = string.Empty;

    /// <summary>
    /// Path (relative to the chat's working directory) where the file was copied
    /// so the model's sandboxed file tools can read it. Null for blob-only uploads.
    /// </summary>
    public string? RelativePath { get; set; }
}

/// <summary>A tool call recorded on an assistant event.</summary>
public sealed class ToolCallRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = string.Empty;
}
