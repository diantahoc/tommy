using Tommy.Core.Engine;

namespace Tommy.Core.Configuration;

/// <summary>
/// A saved launch/connection profile for the Strata engine. Several profiles can
/// be saved (e.g. different quant / --ctx), persisted per machine and
/// re-pointable later. Kept platform-generic: the run script is a <c>.bat</c>
/// on Windows or a <c>.sh</c> on Linux.
/// </summary>
public sealed record EngineProfile
{
    /// <summary>Display name, e.g. "run-iq3s" or "large-context".</summary>
    public required string Name { get; init; }

    public required EngineMode Mode { get; init; }

    /// <summary>OpenAI-compatible base URL, e.g. <c>http://127.0.0.1:8080/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Model id to request. When null, the client uses whatever model the
    /// endpoint reports as loaded.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Metrics endpoint. When null it is derived from <see cref="BaseUrl"/>'s
    /// origin (e.g. <c>http://127.0.0.1:8080/metrics</c>).
    /// </summary>
    public string? MetricsUrl { get; init; }

    /// <summary>
    /// Optional API key / bearer token. When set, it is sent as
    /// <c>Authorization: Bearer &lt;token&gt;</c> on every request to the endpoint
    /// (OpenAI-compatible auth, e.g. Strata's <c>--api-key</c>). Stored in plaintext
    /// in <c>config.json</c> like the other endpoint secrets. Null/blank = no auth.
    /// </summary>
    public string? AuthToken { get; init; }

    // --- Managed mode only ---

    /// <summary>Strata install directory (managed mode).</summary>
    public string? InstallDir { get; init; }

    /// <summary>Run-script name chosen at install time, e.g. <c>run-iq3s.bat</c>.</summary>
    public string? RunScript { get; init; }
}
