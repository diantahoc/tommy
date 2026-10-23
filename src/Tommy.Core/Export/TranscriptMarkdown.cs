using System.Text;
using System.Text.Json;
using Tommy.Core.Chat;
using Tommy.Core.Events;

namespace Tommy.Core.Export;

/// <summary>
/// Renders a chat's event log to a self-contained Markdown transcript — user and
/// assistant turns, the assistant's reasoning, every tool call with its arguments,
/// and every tool result. Pure and I/O-free: large results that live in a blob are
/// resolved through the caller-supplied <paramref name="resolveBlobText"/> delegate,
/// so this is unit-testable without the filesystem.
/// </summary>
public static class TranscriptMarkdown
{
    /// <summary>Per-result cap, so one huge tool result can't bloat the whole file.</summary>
    private const int MaxResultChars = 50_000;

    // Run-end markers that fire after every ordinary turn — too noisy to render as notes.
    private static readonly HashSet<string> RoutineMeta =
        new(StringComparer.OrdinalIgnoreCase) { "Completed", "Answered" };

    public static string Render(ChatRecord record, IReadOnlyList<ChatEvent> events,
        Func<string, string?>? resolveBlobText = null)
    {
        var sb = new StringBuilder();

        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(record.Title) ? "Chat" : record.Title.Trim());
        sb.AppendLine();
        sb.Append("- **Kind:** ").AppendLine(record.Kind.ToString());
        sb.Append("- **Created:** ").AppendLine(Stamp(record.Created));
        sb.Append("- **Exported:** ").AppendLine(Stamp(DateTimeOffset.UtcNow));
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Map a tool call's id to its name, so a result can be labelled even if its own
        // Tool field is ever empty.
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            switch (e)
            {
                case UserEvent u: RenderUser(sb, u); break;
                case AssistantEvent a: RenderAssistant(sb, a, callNames); break;
                case ToolResultEvent r: RenderResult(sb, r, callNames, resolveBlobText); break;
                case MetaEvent m: RenderMeta(sb, m); break;
                // TaskStateEvent is an internal plan snapshot, not part of the conversation.
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void RenderUser(StringBuilder sb, UserEvent u)
    {
        sb.Append("## User · ").AppendLine(Stamp(u.At));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(u.Text))
        {
            sb.AppendLine(u.Text.Trim());
            sb.AppendLine();
        }
        if (u.Attachments.Count > 0)
        {
            sb.AppendLine("**Attachments:**");
            foreach (var a in u.Attachments)
                sb.Append("- ").Append(a.Name).Append(" (").Append(a.Mime).AppendLine(")");
            sb.AppendLine();
        }
    }

    private static void RenderAssistant(StringBuilder sb, AssistantEvent a, Dictionary<string, string> callNames)
    {
        sb.Append("## Assistant · ").AppendLine(Stamp(a.At));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(a.Reasoning))
        {
            sb.AppendLine("<details><summary>Reasoning</summary>");
            sb.AppendLine();
            sb.AppendLine(a.Reasoning.Trim());
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(a.Content))
        {
            sb.AppendLine(a.Content.Trim());
            sb.AppendLine();
        }

        foreach (var call in a.ToolCalls)
        {
            if (!string.IsNullOrEmpty(call.Id)) callNames[call.Id] = call.Name;
            sb.Append("**🔧 Tool call — `").Append(call.Name).AppendLine("`**");
            sb.AppendLine();
            sb.AppendLine(Fence(PrettyJson(call.ArgumentsJson), "json"));
        }
    }

    private static void RenderResult(StringBuilder sb, ToolResultEvent r,
        Dictionary<string, string> callNames, Func<string, string?>? resolveBlobText)
    {
        var name = !string.IsNullOrEmpty(r.Tool)
            ? r.Tool
            : callNames.TryGetValue(r.CallId, out var n) ? n : "tool";

        sb.Append("**↳ Result — `").Append(name).Append("` ").Append(r.Success ? "✓" : "✗ error").AppendLine("**");
        sb.AppendLine();

        var body = r.Result;
        if (string.IsNullOrEmpty(body) && !string.IsNullOrEmpty(r.BlobRef))
            body = resolveBlobText?.Invoke(r.BlobRef!) ?? $"(result stored in blob {r.BlobRef}, not available)";
        body ??= string.Empty;

        var truncated = body.Length > MaxResultChars;
        if (truncated) body = body[..MaxResultChars];

        sb.AppendLine(Fence(body, null));
        if (truncated)
        {
            sb.Append("_… result truncated to ").Append(MaxResultChars.ToString("N0")).AppendLine(" characters._");
            sb.AppendLine();
        }
    }

    private static void RenderMeta(StringBuilder sb, MetaEvent m)
    {
        if (string.IsNullOrWhiteSpace(m.Kind) || RoutineMeta.Contains(m.Kind)) return;
        var detail = string.IsNullOrWhiteSpace(m.Detail) ? "" : ": " + m.Detail.Trim();
        sb.Append("> _").Append(m.Kind).Append(detail).AppendLine("_");
        sb.AppendLine();
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");

    /// <summary>Pretty-print tool-call arguments; leave as-is if they aren't valid JSON.</summary>
    private static string PrettyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    /// <summary>
    /// Wrap <paramref name="content"/> in a fenced code block whose fence is always longer than
    /// any backtick run inside it, so content containing ``` can't break out of the block.
    /// </summary>
    private static string Fence(string content, string? lang)
    {
        content ??= string.Empty;
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            if (c == '`') { run++; longest = Math.Max(longest, run); }
            else run = 0;
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{lang}\n{content}\n{fence}\n";
    }
}
