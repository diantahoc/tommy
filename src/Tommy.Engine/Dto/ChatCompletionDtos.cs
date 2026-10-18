using System.Text.Json.Serialization;

namespace Tommy.Engine.Dto;

/// <summary>Wire model for a POST /v1/chat/completions request.</summary>
public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<ChatMessageDto> Messages { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    /// <summary>
    /// Optional native reasoning-effort hint. Sent only when the resolved
    /// thinking effort maps to a value the endpoint honors; omitted otherwise.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// A chat message, used both for request input and response output. On the way
/// out we send only <see cref="Role"/> + <see cref="Content"/>; the engine's
/// <see cref="ReasoningContent"/> is read from responses and never re-sent.
/// </summary>
public sealed class ChatMessageDto
{
    // Optional, not required: streaming delta chunks omit "role" after the first.
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

/// <summary>Wire model for a chat completion response (and streaming chunk).</summary>
public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatChoiceDto>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public UsageDto? Usage { get; set; }

    [JsonPropertyName("timings")]
    public TimingsDto? Timings { get; set; }
}

public sealed class ChatChoiceDto
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Present on non-streaming responses.</summary>
    [JsonPropertyName("message")]
    public ChatMessageDto? Message { get; set; }

    /// <summary>Present on streaming chunks.</summary>
    [JsonPropertyName("delta")]
    public ChatMessageDto? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class UsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("prompt_tokens_details")]
    public PromptTokensDetailsDto? PromptTokensDetails { get; set; }
}

public sealed class PromptTokensDetailsDto
{
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; set; }
}

/// <summary>Per-response timing stats from Strata (tok/s, time-to-first-token).</summary>
public sealed class TimingsDto
{
    [JsonPropertyName("prompt_n")]
    public int? PromptN { get; set; }

    [JsonPropertyName("prompt_ms")]
    public double? PromptMs { get; set; }

    [JsonPropertyName("prompt_per_second")]
    public double? PromptPerSecond { get; set; }

    [JsonPropertyName("predicted_n")]
    public int? PredictedN { get; set; }

    [JsonPropertyName("predicted_ms")]
    public double? PredictedMs { get; set; }

    [JsonPropertyName("predicted_per_second")]
    public double? PredictedPerSecond { get; set; }
}
