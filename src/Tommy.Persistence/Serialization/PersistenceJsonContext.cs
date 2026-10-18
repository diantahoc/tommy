using System.Text.Json.Serialization;
using Tommy.Core.Configuration;

namespace Tommy.Persistence.Serialization;

/// <summary>
/// Source-generated serialization for on-disk config. Enums are written as
/// strings and the output is indented so <c>config.json</c> stays human-editable.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TommyConfig))]
[JsonSerializable(typeof(McpServerEntry))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class PersistenceJsonContext : JsonSerializerContext
{
}
