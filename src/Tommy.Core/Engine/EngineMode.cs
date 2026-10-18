namespace Tommy.Core.Engine;

/// <summary>How Tommy relates to the Strata engine process.</summary>
public enum EngineMode
{
    /// <summary>
    /// Tommy owns the engine process: launches it via Strata's run script and
    /// offers start / stop / restart. Health check has two layers (process
    /// alive, endpoint responding with a model loaded).
    /// </summary>
    Managed,

    /// <summary>
    /// The engine runs elsewhere; Tommy is only a client to its endpoint.
    /// No lifecycle controls; health check is endpoint-reachability only.
    /// </summary>
    Remote
}
