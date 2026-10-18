using System.Text.Json;

namespace Tommy.Tools.Internal;

/// <summary>Lenient readers for tool arguments supplied as a JSON object.</summary>
internal static class Args
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int? Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static bool Bool(JsonElement e, string name, bool fallback = false) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;
}
