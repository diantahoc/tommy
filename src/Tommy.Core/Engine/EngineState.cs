namespace Tommy.Core.Engine;

/// <summary>
/// Visible lifecycle state of the Strata engine, surfaced in the UI.
/// A single local model serializes work, so <see cref="Busy"/> means a
/// generation is in flight and further requests queue behind it.
/// </summary>
public enum EngineState
{
    /// <summary>Not running (managed mode, process stopped) or unreachable.</summary>
    Stopped,

    /// <summary>Process/endpoint up but the model is still loading.</summary>
    Warming,

    /// <summary>Model loaded and idle, ready to accept a request.</summary>
    Loaded,

    /// <summary>A generation is currently running; new requests queue.</summary>
    Busy
}
