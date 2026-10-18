using System.Text.Json.Serialization;

namespace Tommy.Engine.Dto;

/// <summary>Wire model for GET /metrics (subset of fields Tommy surfaces).</summary>
public sealed class MetricsResponse
{
    [JsonPropertyName("engine")]
    public MetricsEngineDto? Engine { get; set; }

    [JsonPropertyName("live")]
    public MetricsLiveDto? Live { get; set; }

    [JsonPropertyName("hardware")]
    public MetricsHardwareDto? Hardware { get; set; }

    [JsonPropertyName("hardware_static")]
    public MetricsHardwareStaticDto? HardwareStatic { get; set; }
}

public sealed class MetricsEngineDto
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("max_context")]
    public int MaxContext { get; set; }

    [JsonPropertyName("context")]
    public int Context { get; set; }

    [JsonPropertyName("images")]
    public bool Images { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class MetricsLiveDto
{
    /// <summary>e.g. "idle" or "busy".</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("queued")]
    public int Queued { get; set; }

    [JsonPropertyName("tok_s")]
    public double? TokS { get; set; }
}

public sealed class MetricsHardwareDto
{
    [JsonPropertyName("gpu_util")]
    public double GpuUtil { get; set; }

    [JsonPropertyName("gpu_mem_used")]
    public long GpuMemUsed { get; set; }

    [JsonPropertyName("gpu_mem_total")]
    public long GpuMemTotal { get; set; }

    [JsonPropertyName("gpu_temp")]
    public double GpuTemp { get; set; }

    [JsonPropertyName("gpu_power")]
    public double GpuPower { get; set; }

    [JsonPropertyName("gpu_power_limit")]
    public double GpuPowerLimit { get; set; }

    [JsonPropertyName("cpu")]
    public double Cpu { get; set; }

    [JsonPropertyName("ram_used")]
    public long RamUsed { get; set; }

    [JsonPropertyName("ram_total")]
    public long RamTotal { get; set; }
}

public sealed class MetricsHardwareStaticDto
{
    [JsonPropertyName("gpu_name")]
    public string? GpuName { get; set; }

    [JsonPropertyName("gpu_count")]
    public int GpuCount { get; set; }

    [JsonPropertyName("cpu_name")]
    public string? CpuName { get; set; }

    [JsonPropertyName("cores")]
    public int Cores { get; set; }

    [JsonPropertyName("threads")]
    public int Threads { get; set; }
}
