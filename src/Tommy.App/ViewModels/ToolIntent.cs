using System.Text.Json;

namespace Tommy.App.ViewModels;

/// <summary>
/// Turns a tool call into a short human intent line ("Read notes.txt",
/// "Search TODO") shown when the call is collapsed, instead of raw name(args).
/// </summary>
public static class ToolIntent
{
    public static string Describe(string toolName, string argsJson)
    {
        var a = Parse(argsJson);
        string? P(string k) => a.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        // verb + optional argument; falls back to a generic phrase when the arg is absent (e.g. on reload).
        static string L(string verb, string? arg, string generic) => arg is null ? generic : $"{verb} {arg}";

        return toolName switch
        {
            "read_file" => L("Read", P("path"), "Read a file"),
            "read_file_range" => P("path") is { } rp ? $"Read {rp} lines {P("start_line")}–{P("end_line")}" : "Read file lines",
            "line_count" => L("Count lines in", P("path"), "Count lines"),
            "list_dir" => $"List {P("path") ?? "."}",
            "find_file" => L("Find", P("pattern"), "Find files"),
            "search" => P("pattern") is { } sp ? $"Search \"{sp}\"" : "Search files",
            "edit_file" => L("Edit", P("path"), "Edit a file"),
            "create_file" => L("Create", P("path"), "Create a file"),
            "create_dir" => L("Create dir", P("path"), "Create a directory"),
            "delete_file" => L("Delete", P("path"), "Delete a file"),
            "skill_search" => L("Search skills:", P("query"), "Search skills"),
            "skill_load" => L("Load skill", P("name"), "Load a skill"),
            "memory_recall" => L("Recall memory:", P("key"), "Recall memory"),
            "memory_search" => L("Search memory:", P("query"), "Search memory"),
            "memory_write" => L("Write memory:", P("topic"), "Write memory"),
            _ => toolName
        };
    }

    private static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in doc.RootElement.EnumerateObject())
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? ""
                    : p.Value.ToString();
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
