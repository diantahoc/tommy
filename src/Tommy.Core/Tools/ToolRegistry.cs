using System.Reflection;
using System.Text.Json.Nodes;

namespace Tommy.Core.Tools;

/// <summary>
/// Collects tools and hands the agent loop one flat, native <c>tools</c> array.
/// Native and MCP tools register through the same registry, so the loop treats
/// them uniformly. Tools are discovered by the <see cref="ToolAttribute"/> marker.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ITool> Tools => _tools.Values;

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
        foreach (var alias in tool.Aliases)
            _aliases[alias] = tool.Name;
    }

    /// <summary>
    /// Remove a tool (and its aliases) by name. Used for MCP lifecycle — a server
    /// that disconnects withdraws its tools from the model's flat list.
    /// </summary>
    public bool Unregister(string name)
    {
        if (!_tools.Remove(name, out var tool)) return false;
        foreach (var alias in tool.Aliases)
            _aliases.Remove(alias);
        return true;
    }

    /// <summary>Resolve a tool by exact name, then alias, then case-insensitive name.</summary>
    public ITool? Find(string name)
    {
        if (_tools.TryGetValue(name, out var tool)) return tool;
        if (_aliases.TryGetValue(name, out var canonical) && _tools.TryGetValue(canonical, out tool)) return tool;
        return _tools.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>All tool names, for error messages that help the model self-correct.</summary>
    public string NamesForError() => string.Join(", ", _tools.Keys.OrderBy(n => n));

    /// <summary>Discover and register attributed <see cref="ITool"/> types in the given assemblies.</summary>
    public int DiscoverFrom(params Assembly[] assemblies)
    {
        var count = 0;
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(ITool).IsAssignableFrom(type)) continue;
                if (type.GetCustomAttribute<ToolAttribute>() is null) continue;

                if (Activator.CreateInstance(type) is ITool tool)
                {
                    Register(tool);
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Build the native OpenAI <c>tools</c> array, optionally filtered to the
    /// per-chat enabled set.
    /// </summary>
    public JsonArray BuildToolsArray(ISet<string>? enabled = null)
    {
        var arr = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            // Null enabled set = "all tools": advertise every native tool, but keep
            // external (MCP) tools opt-in. An explicit set gates both kinds by name.
            if (enabled is null) { if (tool.IsExternal) continue; }
            else if (!enabled.Contains(tool.Name)) continue;

            arr.Add(Function(tool.Name, tool.Description, tool.ParameterSchema));

            // Advertise each alias as its own declared function. A strict engine (Strata
            // ≥ v0.1.50) validates the name in a <tool_call> against the declared set and
            // bounces an undeclared one back as plain text — so a short name the model
            // reaches for (e.g. "read_range" for read_file_range) would never execute.
            // Declaring the alias lets the engine emit a structured call; Find() then maps
            // it to the canonical tool. The description points at the canonical name so the
            // model treats the pair as one capability rather than two.
            foreach (var alias in tool.Aliases)
                arr.Add(Function(alias, $"Alias of `{tool.Name}`.", tool.ParameterSchema));
        }
        return arr;
    }

    private static JsonObject Function(string name, string description, JsonObject schema) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = schema.DeepClone()
        }
    };
}
