using System.Text.Json.Nodes;

namespace Tommy.Core.Tools;

/// <summary>Small helper to build JSON Schema objects for tool parameters.</summary>
public static class ToolSchema
{
    public static JsonObject Object(params (string name, JsonObject prop, bool required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, prop, req) in props)
        {
            properties[name] = prop;
            if (req) required.Add(name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    public static JsonObject Str(string? description = null) => Prop("string", description);
    public static JsonObject Int(string? description = null) => Prop("integer", description);
    public static JsonObject Bool(string? description = null) => Prop("boolean", description);

    /// <summary>An array of strings parameter.</summary>
    public static JsonObject StrArray(string? description = null)
    {
        var o = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Prop(string type, string? description)
    {
        var o = new JsonObject { ["type"] = type };
        if (description is not null) o["description"] = description;
        return o;
    }
}
