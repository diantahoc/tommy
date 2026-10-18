using System.Text.Json.Serialization;

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

/// <summary>An uploaded file referenced from an event by content hash.</summary>
public sealed class Attachment
{
    public string BlobHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Mime { get; set; } = string.Empty;
}

/// <summary>A tool call recorded on an assistant event.</summary>
public sealed class ToolCallRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = string.Empty;
}
