using System.Text.Json.Serialization;

namespace Tommy.Engine.Dto;

/// <summary>Wire model for GET /v1/models.</summary>
public sealed class ModelsResponse
{
    [JsonPropertyName("data")]
    public List<ModelInfoDto>? Data { get; set; }
}

public sealed class ModelInfoDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public ModelStatusDto? Status { get; set; }

    [JsonPropertyName("meta")]
    public ModelMetaDto? Meta { get; set; }

    [JsonPropertyName("architecture")]
    public ModelArchitectureDto? Architecture { get; set; }
}

public sealed class ModelStatusDto
{
    /// <summary>e.g. "loaded".</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class ModelMetaDto
{
    [JsonPropertyName("n_ctx")]
    public int NCtx { get; set; }
}

public sealed class ModelArchitectureDto
{
    [JsonPropertyName("input_modalities")]
    public List<string>? InputModalities { get; set; }

    [JsonPropertyName("output_modalities")]
    public List<string>? OutputModalities { get; set; }
}
