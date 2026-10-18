namespace Tommy.Core.Engine;

/// <summary>Overall readiness of the engine.</summary>
public enum HealthStatus
{
    /// <summary>Endpoint unreachable (and, in managed mode, the process is not alive).</summary>
    Down,

    /// <summary>Process/endpoint reachable, but no model is loaded yet (warming).</summary>
    NoModel,

    /// <summary>Endpoint reachable and a model is loaded.</summary>
    Ready
}

/// <summary>
/// Two-layer health result. In managed mode both layers apply (is my process
/// alive, is the endpoint responding with a model loaded); in remote mode only
/// endpoint reachability is meaningful and <see cref="ProcessAlive"/> is left true.
/// </summary>
public sealed record EngineHealth(
    HealthStatus Status,
    bool ProcessAlive,
    bool EndpointReachable,
    bool ModelLoaded,
    string? ModelId,
    string? Detail)
{
    public static EngineHealth Down(string? detail = null) =>
        new(HealthStatus.Down, ProcessAlive: false, EndpointReachable: false,
            ModelLoaded: false, ModelId: null, Detail: detail);
}
