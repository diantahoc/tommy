namespace Tommy.Core.Engine;

/// <summary>
/// A live telemetry snapshot sourced entirely from the Strata <c>/metrics</c>
/// endpoint — no OS sensors, so it works identically on Windows and Linux and,
/// in remote mode, reports the remote box's figures.
/// </summary>
public sealed record EngineTelemetry(
    string? ModelId,
    string LiveState,
    int Queued,
    double? TokensPerSecond,
    HardwareSnapshot Hardware,
    HardwareStatic Static,
    DateTimeOffset SampledAt);

/// <summary>Instantaneous hardware readouts (subset of /metrics we surface).</summary>
public sealed record HardwareSnapshot(
    double GpuUtil,
    long GpuMemUsedBytes,
    long GpuMemTotalBytes,
    double GpuTempC,
    double GpuPowerWatts,
    double GpuPowerLimitWatts,
    double CpuPercent,
    long RamUsedBytes,
    long RamTotalBytes);

/// <summary>Static hardware description (does not change between samples).</summary>
public sealed record HardwareStatic(
    string? GpuName,
    int GpuCount,
    string? CpuName,
    int Cores,
    int Threads);
