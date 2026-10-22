using System.Net.Sockets;
using System.Text;
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
    private IStrataClient _client;
    private readonly ManagedEngineProcess? _managed;
    private readonly SingleModelQueue _queue = new();
    private EngineState _state = EngineState.Stopped;

    public EngineService(EngineProfile profile, HttpClient? http = null)
    {
        Profile = profile;
        _ownsHttp = http is null;
        _http = http ?? CreateResilientHttpClient();
        _client = new StrataClient(_http, profile.BaseUrl, profile.MetricsUrl, profile.ModelId, profile.AuthToken);

        if (profile.Mode == EngineMode.Managed &&
            !string.IsNullOrWhiteSpace(profile.InstallDir) &&
            !string.IsNullOrWhiteSpace(profile.RunScript))
        {
            _managed = new ManagedEngineProcess(profile.InstallDir!, profile.RunScript!);
        }

        _queue.StateChanged += (_, _) => UpdateBusyState();
    }

    public EngineProfile Profile { get; private set; }

    /// <summary>
    /// The default HttpClient for talking to the engine, tuned so a dropped connection
    /// is detected quickly instead of hanging. Over a flaky VPN the engine can finish
    /// generating but the reply never arrives; without this the client would wait out the
    /// whole 10-minute timeout while the UI still shows "busy". OS-level TCP keep-alive
    /// probes a silent peer within ~tens of seconds (and HTTP/2 PINGs do the same when the
    /// endpoint speaks HTTP/2), so the request fails fast and the queue's gate is released.
    /// The overall timeout stays generous so a legitimately long generation isn't cut off.
    /// </summary>
    private static HttpClient CreateResilientHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            ConnectCallback = static async (ctx, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                // Probe a silent peer: first probe after 20s idle, then every 10s, give up after 3.
                // A dead connection surfaces as a read error in ~50s instead of the full timeout.
                TrySetTcpKeepAlive(socket, idleSeconds: 20, intervalSeconds: 10, retryCount: 3);
                try
                {
                    await socket.ConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Apply the TCP keep-alive tunables where the platform supports them.</summary>
    private static void TrySetTcpKeepAlive(Socket socket, int idleSeconds, int intervalSeconds, int retryCount)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, idleSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, intervalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, retryCount);
        }
        catch (SocketException) { /* some platforms don't expose the per-socket tunables; keep-alive is still on */ }
        catch (PlatformNotSupportedException) { /* ditto */ }
    }

    /// <summary>True when Tommy owns the engine process and can start/stop it.</summary>
    public bool CanManage => _managed is not null;

    /// <summary>
    /// Replace the endpoint auth token at runtime, rebuilding the low-level client so
    /// the next request carries it (on the same shared <see cref="HttpClient"/>). The
    /// managed process, if any, is untouched — only the header on new requests changes.
    /// </summary>
    public void SetAuthToken(string? token)
    {
        var normalized = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        Profile = Profile with { AuthToken = normalized };
        _client = new StrataClient(_http, Profile.BaseUrl, Profile.MetricsUrl, Profile.ModelId, normalized);
    }

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

    /// <summary>
    /// Run a completion while streaming its reasoning/content deltas to <paramref name="onToken"/>
    /// as they arrive, then return the assembled result (content, reasoning, tool calls, finish,
    /// usage, timings). With no sink it falls back to the non-streaming path.
    /// </summary>
    public async Task<ChatTurnResult> CompleteAsync(
        ChatCompletionRequest request, Action<ChatStreamEvent>? onToken, CancellationToken ct = default)
    {
        if (onToken is null)
            return await CompleteAsync(request, ct).ConfigureAwait(false);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var finish = FinishReason.Other;
        UsageDto? usage = null;
        TimingsDto? timings = null;
        IReadOnlyList<ToolCall> toolCalls = Array.Empty<ToolCall>();

        await foreach (var ev in StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (ev.Kind)
            {
                case ChatStreamEventKind.ReasoningDelta when !string.IsNullOrEmpty(ev.Text):
                    reasoning.Append(ev.Text);
                    onToken(ev);
                    break;
                case ChatStreamEventKind.ContentDelta when !string.IsNullOrEmpty(ev.Text):
                    content.Append(ev.Text);
                    onToken(ev);
                    break;
                case ChatStreamEventKind.Completed:
                    finish = ev.FinishReason;
                    usage = ev.Usage;
                    timings = ev.Timings;
                    if (ev.ToolCalls is { Count: > 0 }) toolCalls = ev.ToolCalls;
                    break;
            }
        }

        return new ChatTurnResult(
            Content: content.ToString(),
            Reasoning: reasoning.Length == 0 ? null : reasoning.ToString(),
            FinishReason: finish,
            Usage: usage,
            Timings: timings,
            ToolCalls: toolCalls);
    }

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
