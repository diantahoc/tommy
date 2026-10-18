using Tommy.Core.Configuration;
using Tommy.Core.Engine;
using Tommy.Engine.Dto;

namespace Tommy.Engine;

/// <summary>
/// Orchestrates a Strata engine: wraps the low-level client with the
/// single-model queue, tracks a visible <see cref="EngineState"/>, caches
/// detected capabilities, and exposes health + telemetry reads. The agent loop
/// talks to this, never to <see cref="StrataClient"/> directly, so every
/// generation serializes through one queue.
/// </summary>
public sealed class EngineService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IStrataClient _client;
    private readonly SingleModelQueue _queue = new();
    private EngineState _state = EngineState.Stopped;

    public EngineService(EngineProfile profile, HttpClient? http = null)
    {
        Profile = profile;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _client = new StrataClient(_http, profile.BaseUrl, profile.MetricsUrl, profile.ModelId);
        _queue.StateChanged += (_, _) => UpdateBusyState();
    }

    public EngineProfile Profile { get; }

    /// <summary>Capabilities detected at <see cref="ConnectAsync"/>; null until then.</summary>
    public ModelCapabilities? Capabilities { get; private set; }

    public EngineState State => _state;

    public SingleModelQueue Queue => _queue;

    /// <summary>Model id to send on requests (profile override, else detected).</summary>
    public string ModelId => Profile.ModelId ?? Capabilities?.ModelId ?? "unknown";

    public event EventHandler<EngineState>? StateChanged;

    /// <summary>Check health and, if a model is loaded, detect and cache capabilities.</summary>
    public async Task<EngineHealth> ConnectAsync(CancellationToken ct = default)
    {
        var health = await _client.CheckHealthAsync(ct).ConfigureAwait(false);
        switch (health.Status)
        {
            case HealthStatus.Ready:
                Capabilities = await _client.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                SetState(EngineState.Loaded);
                break;
            case HealthStatus.NoModel:
                SetState(EngineState.Warming);
                break;
            default:
                SetState(EngineState.Stopped);
                break;
        }
        return health;
    }

    public Task<EngineHealth> CheckHealthAsync(CancellationToken ct = default) =>
        _client.CheckHealthAsync(ct);

    public Task<EngineTelemetry> GetTelemetryAsync(CancellationToken ct = default) =>
        _client.GetTelemetryAsync(ct);

    /// <summary>Run a non-streaming completion through the single-model queue.</summary>
    public Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default) =>
        _queue.RunAsync(token => _client.CompleteAsync(request, token), ct);

    /// <summary>Stream a completion through the single-model queue.</summary>
    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatCompletionRequest request, CancellationToken ct = default) =>
        _queue.StreamAsync(token => _client.StreamAsync(request, token), ct);

    private void UpdateBusyState()
    {
        if (_queue.IsBusy)
            SetState(EngineState.Busy);
        else if (_state == EngineState.Busy)
            SetState(EngineState.Loaded);
    }

    private void SetState(EngineState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
