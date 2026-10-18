using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tommy.Persistence.Storage;

/// <summary>Shared JSON options for on-disk records and the event log.</summary>
internal static class StoreJson
{
    /// <summary>Indented, enum-as-string, null-skipping — for chat/project/workspace files.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Compact one-line-per-record options for events.jsonl (polymorphic).</summary>
    public static readonly JsonSerializerOptions EventOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
