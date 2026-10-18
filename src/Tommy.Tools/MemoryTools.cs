using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tommy.Core.Memory;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>Load a memory sub-file by its index key.</summary>
[Tool]
public sealed class MemoryRecallTool : ITool
{
    public string Name => "memory_recall";
    public string Description => "Load a memory sub-file by its index key.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("key", ToolSchema.Str("The index key / topic name to load."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.MemoryDirs is not { Count: > 0 })
            return Task.FromResult(ToolResult.Fail("no memory is configured for this chat."));
        var key = Args.Str(args, "key");
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(ToolResult.Fail("key is required."));

        var file = MemoryLayout.TopicFileName(key);
        foreach (var dir in ctx.MemoryDirs)
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path))
                return Task.FromResult(ToolResult.Ok(File.ReadAllText(path), ToolResultKind.Text, key));
        }
        return Task.FromResult(ToolResult.Fail($"no memory topic '{key}'. Check the memory index."));
    }
}

/// <summary>Search across memory sub-files when the index description isn't enough.</summary>
[Tool]
public sealed class MemorySearchTool : ITool
{
    public string Name => "memory_search";
    public string Description => "Search text across all memory sub-files.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("query", ToolSchema.Str("Text or regular expression to find."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.MemoryDirs is not { Count: > 0 })
            return Task.FromResult(ToolResult.Fail("no memory is configured for this chat."));
        var query = Args.Str(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(ToolResult.Fail("query is required."));

        Regex regex;
        try { regex = new Regex(query, RegexOptions.IgnoreCase); }
        catch (ArgumentException ex) { return Task.FromResult(ToolResult.Fail($"invalid regex: {ex.Message}")); }

        var sb = new StringBuilder();
        foreach (var dir in ctx.MemoryDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.md"))
            {
                if (Path.GetFileName(path) == MemoryLayout.IndexFile) continue;
                var lineNo = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNo++;
                    if (regex.IsMatch(line))
                        sb.Append(Path.GetFileNameWithoutExtension(path)).Append(':').Append(lineNo)
                          .Append(": ").AppendLine(line.Trim());
                }
            }
        }
        return Task.FromResult(ToolResult.Ok(sb.Length == 0 ? "(no matches)" : sb.ToString().TrimEnd()));
    }
}

/// <summary>
/// Record a durable fact to memory. Gated (write tier): silent self-editing memory
/// is how an agent quietly poisons its own future context.
/// </summary>
[Tool]
public sealed class MemoryWriteTool : ITool
{
    public string Name => "memory_write";
    public string Description => "Write (or replace) a memory sub-file for a topic, and update the index.";
    public PermissionTier Tier => PermissionTier.Write;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("topic", ToolSchema.Str("Short topic key, e.g. build-commands."), true),
        ("content", ToolSchema.Str("The note to store (markdown)."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult<ToolPreview?>(new ToolPreview(
            $"Write memory topic '{Args.Str(args, "topic")}'", Args.Str(args, "content")));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.WriteMemoryDir))
            return Task.FromResult(ToolResult.Fail("this chat has no writable memory."));
        var topic = Args.Str(args, "topic");
        var content = Args.Str(args, "content");
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrEmpty(content))
            return Task.FromResult(ToolResult.Fail("topic and content are required."));

        Directory.CreateDirectory(ctx.WriteMemoryDir);
        File.WriteAllText(Path.Combine(ctx.WriteMemoryDir, MemoryLayout.TopicFileName(topic)), content);
        UpdateIndex(ctx.WriteMemoryDir, topic, FirstLine(content));

        return Task.FromResult(ToolResult.Ok($"Saved memory topic '{topic}'."));
    }

    private static string FirstLine(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var t = line.Trim().TrimStart('#').Trim();
            if (t.Length > 0) return t.Length > 80 ? t[..80] : t;
        }
        return "(note)";
    }

    private static void UpdateIndex(string dir, string topic, string description)
    {
        var indexPath = Path.Combine(dir, MemoryLayout.IndexFile);
        var key = MemoryLayout.Sanitize(topic);
        var entry = $"- {key}: {description}";

        var lines = File.Exists(indexPath)
            ? File.ReadAllLines(indexPath).ToList()
            : new List<string> { "# Memory index", "" };

        var prefix = $"- {key}:";
        var existing = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (existing >= 0) lines[existing] = entry;
        else lines.Add(entry);

        File.WriteAllText(indexPath, string.Join('\n', lines).TrimEnd() + "\n");
    }
}
