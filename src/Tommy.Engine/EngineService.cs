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
public sealed class EngineService : IChatCompleter, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IStrataClient _client;
    private readonly ManagedEngineProcess? _managed;
    private readonly SingleModelQueue _queue = new();
    private EngineState _state = EngineState.Stopped;

    public EngineService(EngineProfile profile, HttpClient? http = null)
    {
        Profile = profile;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _client = new StrataClient(_http, profile.BaseUrl, profile.MetricsUrl, profile.ModelId);

        if (profile.Mode == EngineMode.Managed &&
            !string.IsNullOrWhiteSpace(profile.InstallDir) &&
            !string.IsNullOrWhiteSpace(profile.RunScript))
        {
            _managed = new ManagedEngineProcess(profile.InstallDir!, profile.RunScript!);
        }

        _queue.StateChanged += (_, _) => UpdateBusyState();
    }

    public EngineProfile Profile { get; }

    /// <summary>True when Tommy owns the engine process and can start/stop it.</summary>
    public bool CanManage => _managed is not null;

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

    /// <summary>
    /// Managed mode: launch the engine process, then poll health until a model
    /// is loaded or the timeout elapses. In remote mode this just connects.
    /// </summary>
    public async Task<EngineHealth> StartAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_managed is null)
            return await ConnectAsync(ct).ConfigureAwait(false);

        if (!_managed.IsAlive)
        {
            _managed.Start();
            SetState(EngineState.Warming);
        }

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));
        var health = EngineHealth.Down("starting");
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!_managed.IsAlive)
            {
                SetState(EngineState.Stopped);
                return EngineHealth.Down("engine process exited during startup");
            }

            health = await ConnectAsync(ct).ConfigureAwait(false);
            if (health.Status == HealthStatus.Ready)
                return health;

            // Endpoint not up yet but the process lives — still warming.
            SetState(EngineState.Warming);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        return health;
    }

    /// <summary>Managed mode: stop the engine process. No-op in remote mode.</summary>
    public Task StopAsync()
    {
        if (_managed is not null)
        {
            _managed.Stop();
            SetState(EngineState.Stopped);
        }
        return Task.CompletedTask;
    }

    /// <summary>Managed mode: stop then start the engine.</summary>
    public async Task<EngineHealth> RestartAsync(CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        return await StartAsync(ct: ct).ConfigureAwait(false);
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
        _managed?.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
