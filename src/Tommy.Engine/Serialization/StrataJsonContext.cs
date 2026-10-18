using System.Text.Json.Serialization;
using Tommy.Engine.Dto;

namespace Tommy.Engine.Serialization;

/// <summary>
/// Source-generated serialization context for the OpenAI-compatible payloads,
/// per the spec (System.Text.Json with source generators). Nulls are omitted on
/// write so we never send empty optional fields (e.g. reasoning_content).
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ModelsResponse))]
[JsonSerializable(typeof(MetricsResponse))]
public partial class StrataJsonContext : JsonSerializerContext
{
}
