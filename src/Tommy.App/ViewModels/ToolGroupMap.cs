namespace Tommy.App.ViewModels;

/// <summary>
/// Maps a tool name to its sidebar group. Groups are a <b>UI convenience only</b>
/// — permissions stay per tool; a group switch just flips every tool in it. The
/// Skills tools are intentionally hidden (always on), so they map to null.
/// </summary>
public static class ToolGroupMap
{
    public const string Other = "Other";

    // Display order of the groups.
    public static readonly IReadOnlyList<string> Order = new[]
    {
        "Read", "Modify", "Delete", "Archive", "Documents",
        "Git", "Shell", "Code Scratchpads", "Memory", "Web Search", Other
    };

    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["file_info"] = "Read",
        ["find_file"] = "Read",
        ["line_count"] = "Read",
        ["read_file"] = "Read",
        ["read_file_range"] = "Read",
        ["search"] = "Read",
        ["image_read"] = "Read",
        ["list_dir"] = "Read",

        ["edit_file"] = "Modify",
        ["create_file"] = "Modify",
        ["rename"] = "Modify",
        ["create_dir"] = "Modify",

        ["delete_dir"] = "Delete",
        ["delete_file"] = "Delete",

        ["archive_list"] = "Archive",
        ["archive_extract"] = "Archive",

        ["pdf_extract"] = "Documents",
        ["office_extract"] = "Documents",
        ["ocr"] = "Documents",

        ["shell"] = "Shell",

        ["python_run"] = "Code Scratchpads",
        ["js_run"] = "Code Scratchpads",

        ["memory_recall"] = "Memory",
        ["memory_search"] = "Memory",
        ["memory_write"] = "Memory",

        ["web_fetch"] = "Web Search",
        ["web_fetch_js"] = "Web Search",
        ["web_research"] = "Web Search"
    };

    // Hidden tools: always enabled, never shown in the group list.
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "skill_search", "skill_load"
    };

    /// <summary>The group for a tool, or null if it should be hidden from the list.</summary>
    public static string? GroupOf(string toolName)
    {
        if (Hidden.Contains(toolName)) return null;
        if (toolName.StartsWith("git_", StringComparison.Ordinal)) return "Git";
        return Map.TryGetValue(toolName, out var group) ? group : Other;
    }
}
