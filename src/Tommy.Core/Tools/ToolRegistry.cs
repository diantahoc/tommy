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

    public IReadOnlyCollection<ITool> Tools => _tools.Values;

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public ITool? Find(string name) => _tools.GetValueOrDefault(name);

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
            if (enabled is not null && !enabled.Contains(tool.Name)) continue;

            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParameterSchema.DeepClone()
                }
            });
        }
        return arr;
    }
}
