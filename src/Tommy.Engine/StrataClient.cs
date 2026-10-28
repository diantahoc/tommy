using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Tommy.Core.Diagnostics;
using Tommy.Core.Engine;
using Tommy.Engine.Dto;
using Tommy.Engine.Serialization;

namespace Tommy.Engine;

/// <summary>
/// HTTP implementation of <see cref="IStrataClient"/> against a Strata
/// OpenAI-compatible endpoint and its /metrics feed.
/// </summary>
public sealed class StrataClient : IStrataClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _metricsUrl;
    private readonly string? _preferredModelId;
    private readonly string? _authToken;

    // One-shot guards so a drifted engine shape logs once, not on every connect/poll.
    private bool _capabilityShapeWarned;
    private bool _telemetryShapeWarned;

    /// <param name="http">Shared HttpClient (its timeout governs non-stream calls).</param>
    /// <param name="baseUrl">OpenAI-compatible base, e.g. http://127.0.0.1:8080/v1</param>
    /// <param name="metricsUrl">Metrics URL; derived from baseUrl origin when null.</param>
    /// <param name="preferredModelId">Model id to prefer; null = first reported.</param>
    /// <param name="authToken">Bearer token sent as <c>Authorization</c>; null/blank = none.</param>
    public StrataClient(
        HttpClient http, string baseUrl, string? metricsUrl = null,
        string? preferredModelId = null, string? authToken = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _metricsUrl = metricsUrl ?? DeriveMetricsUrl(_baseUrl);
        _preferredModelId = preferredModelId;
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();
    }

    /// <summary>
    /// Builds a request to the endpoint, attaching <c>Authorization: Bearer</c> when a
    /// token is configured. The header is set per-request (not on the shared
    /// <see cref="HttpClient"/>) so it never leaks to other callers of that client.
    /// </summary>
    private HttpRequestMessage NewRequest(HttpMethod method, string url, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (content is not null) req.Content = content;
        if (_authToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        return req;
    }

    private static string DeriveMetricsUrl(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        return $"{uri.Scheme}://{uri.Authority}/metrics";
    }

    public async Task<ModelCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, $"{_baseUrl}/models");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var models = await JsonSerializer.DeserializeAsync(
            stream, StrataJsonContext.Default.ModelsResponse, ct).ConfigureAwait(false);

        var model = SelectModel(models);
        if (model is null)
        {
            if (!_capabilityShapeWarned)
            {
                _capabilityShapeWarned = true;
                Log.Warn($"/v1/models returned no usable model entry from {_baseUrl}/models; capabilities " +
                         "are unknown. The engine's /v1/models shape may have changed.");
            }
            return ModelCapabilities.Unknown;
        }

        var caps = new ModelCapabilities(
            ModelId: model.Id ?? "unknown",
            InputModalities: model.Architecture?.InputModalities ?? new List<string>(),
            OutputModalities: model.Architecture?.OutputModalities ?? new List<string>(),
            MaxContextTokens: model.Meta?.NCtx ?? 0);

        // A missing n_ctx silently disables proactive context budgeting (the cut-off guard),
        // so make the degradation visible rather than letting it pass as "unknown window".
        if (!_capabilityShapeWarned && caps.MaxContextTokens <= 0)
        {
            _capabilityShapeWarned = true;
            Log.Warn($"/v1/models reported no context window (meta.n_ctx) for model '{caps.ModelId}'; " +
                     "proactive context budgeting is disabled. The engine's /v1/models shape may have changed.");
        }

        return caps;
    }

    public async Task<EngineHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Get, $"{_baseUrl}/models");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new EngineHealth(HealthStatus.Down, true, true, false, null,
                    $"HTTP {(int)resp.StatusCode}");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var models = await JsonSerializer.DeserializeAsync(
                stream, StrataJsonContext.Default.ModelsResponse, ct).ConfigureAwait(false);

            var model = SelectModel(models);
            if (model is null)
                return new EngineHealth(HealthStatus.NoModel, true, true, false, null, "no models reported");

            var loaded = string.Equals(model.Status?.Value, "loaded", StringComparison.OrdinalIgnoreCase);
            return new EngineHealth(
                loaded ? HealthStatus.Ready : HealthStatus.NoModel,
                ProcessAlive: true, EndpointReachable: true, ModelLoaded: loaded,
                ModelId: model.Id, Detail: loaded ? null : $"status={model.Status?.Value}");
        }
        catch (Exception ex)
        {
            return EngineHealth.Down(ex.Message);
        }
    }

    public async Task<EngineTelemetry> GetTelemetryAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, _metricsUrl);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        MetricsResponse? m;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            m = await JsonSerializer.DeserializeAsync(
                stream, StrataJsonContext.Default.MetricsResponse, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // /metrics replied 200 but not with JSON — e.g. a switch to Prometheus text.
            if (!_telemetryShapeWarned)
            {
                _telemetryShapeWarned = true;
                Log.Warn($"/metrics did not parse as JSON from {_metricsUrl} — telemetry is unavailable. The " +
                         $"engine's /metrics format may have changed (e.g. to Prometheus text). {ex.Message}");
            }
            throw;
        }

        // Parsed as JSON but none of the fields Tommy reads are present — a shape drift.
        if (!_telemetryShapeWarned && m is not null && m.Engine is null && m.Live is null && m.Hardware is null)
        {
            _telemetryShapeWarned = true;
            Log.Warn($"/metrics parsed but carried no engine/live/hardware fields from {_metricsUrl}; telemetry " +
                     "will be blank. The engine's /metrics shape may have changed.");
        }

        var hw = m?.Hardware;
        var hardware = new HardwareSnapshot(
            GpuUtil: hw?.GpuUtil ?? 0,
            GpuMemUsedBytes: hw?.GpuMemUsed ?? 0,
            GpuMemTotalBytes: hw?.GpuMemTotal ?? 0,
            GpuTempC: hw?.GpuTemp ?? 0,
            GpuPowerWatts: hw?.GpuPower ?? 0,
            GpuPowerLimitWatts: hw?.GpuPowerLimit ?? 0,
            CpuPercent: hw?.Cpu ?? 0,
            RamUsedBytes: hw?.RamUsed ?? 0,
            RamTotalBytes: hw?.RamTotal ?? 0);

        var hs = m?.HardwareStatic;
        var stat = new HardwareStatic(
            GpuName: hs?.GpuName, GpuCount: hs?.GpuCount ?? 0,
            CpuName: hs?.CpuName, Cores: hs?.Cores ?? 0, Threads: hs?.Threads ?? 0);

        return new EngineTelemetry(
            ModelId: m?.Engine?.Model,
            LiveState: m?.Live?.State ?? "unknown",
            Queued: m?.Live?.Queued ?? 0,
            TokensPerSecond: m?.Live?.TokS,
            Hardware: hardware,
            Static: stat,
            SampledAt: DateTimeOffset.UtcNow);
    }

    public async Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        request.Stream = false;
        using var content = Serialize(request);
        using var httpReq = NewRequest(HttpMethod.Post, $"{_baseUrl}/chat/completions", content);
        using var resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var parsed = await JsonSerializer.DeserializeAsync(
            stream, StrataJsonContext.Default.ChatCompletionResponse, ct).ConfigureAwait(false);

        var choice = parsed?.Choices is { Count: > 0 } ? parsed.Choices[0] : null;
        var msg = choice?.Message;

        var toolCalls = msg?.ToolCalls?
            .Where(tc => tc.Function?.Name is not null)
            .Select(tc => new ToolCall(
                tc.Id ?? Guid.NewGuid().ToString("N"),
                tc.Function!.Name!,
                string.IsNullOrWhiteSpace(tc.Function.Arguments) ? "{}" : tc.Function.Arguments!))
            .ToList() ?? new List<ToolCall>();

        return new ChatTurnResult(
            Content: msg?.Content ?? string.Empty,
            Reasoning: msg?.ReasoningContent,
            FinishReason: ChatTurnResult.MapFinishReason(choice?.FinishReason),
            Usage: parsed?.Usage,
            Timings: parsed?.Timings,
            ToolCalls: toolCalls);
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        request.Stream = true;
        using var content = Serialize(request);
        using var httpReq = NewRequest(HttpMethod.Post, $"{_baseUrl}/chat/completions", content);

        using var resp = await _http.SendAsync(
            httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Start as Incomplete: if the stream ends (socket close, or [DONE]) before any
        // chunk carried a terminal finish_reason, that is a cut-off, not a clean answer.
        // A real terminal reason overrides this as soon as one arrives.
        var finish = FinishReason.Incomplete;
        UsageDto? usage = null;
        TimingsDto? timings = null;
        // Tool calls stream in fragments keyed by index (id/name once, arguments across chunks).
        var toolAcc = new SortedDictionary<int, ToolCallAccumulator>();
        var debug = Environment.GetEnvironmentVariable("TOMMY_STREAM_DEBUG") == "1";
        var lineCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;                                   // end of stream
            lineCount++;
            if (line.Length == 0 || line[0] == ':') continue;          // blank or keep-alive comment
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload == "[DONE]") break;

            ChatCompletionResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(payload, StrataJsonContext.Default.ChatCompletionResponse);
            }
            catch (JsonException ex)
            {
                if (debug) await Console.Error.WriteLineAsync($"[stream] parse fail: {ex.Message} | payload: {payload[..Math.Min(80, payload.Length)]}");
                continue; // tolerate a malformed/partial line rather than aborting the stream
            }

            if (chunk?.Usage is not null) usage = chunk.Usage;
            if (chunk?.Timings is not null) timings = chunk.Timings;

            var choice = chunk?.Choices is { Count: > 0 } ? chunk.Choices[0] : null;
            if (choice is null) continue;

            var delta = choice.Delta;
            if (!string.IsNullOrEmpty(delta?.ReasoningContent))
                yield return new ChatStreamEvent(ChatStreamEventKind.ReasoningDelta, delta!.ReasoningContent);
            if (!string.IsNullOrEmpty(delta?.Content))
                yield return new ChatStreamEvent(ChatStreamEventKind.ContentDelta, delta!.Content);

            if (delta?.ToolCalls is { Count: > 0 })
                AccumulateToolCalls(toolAcc, delta.ToolCalls);

            if (choice.FinishReason is not null)
                finish = ChatTurnResult.MapFinishReason(choice.FinishReason);
        }

        if (debug)
            await Console.Error.WriteLineAsync($"[stream] raw lines read: {lineCount}, finish: {finish}, tools: {toolAcc.Count}");

        yield return new ChatStreamEvent(
            ChatStreamEventKind.Completed, Text: null, FinishReason: finish, Usage: usage, Timings: timings,
            ToolCalls: BuildToolCalls(toolAcc));
    }

    /// <summary>Merges a chunk's tool-call fragments into the per-index accumulators.</summary>
    private static void AccumulateToolCalls(SortedDictionary<int, ToolCallAccumulator> acc, List<ToolCallDto> deltas)
    {
        for (var i = 0; i < deltas.Count; i++)
        {
            var tc = deltas[i];
            var index = tc.Index ?? i;
            if (!acc.TryGetValue(index, out var slot))
            {
                slot = new ToolCallAccumulator();
                acc[index] = slot;
            }
            if (tc.Id is not null) slot.Id = tc.Id;
            if (tc.Function?.Name is not null) slot.Name = tc.Function.Name;
            if (tc.Function?.Arguments is not null) slot.Arguments.Append(tc.Function.Arguments);
        }
    }

    /// <summary>Finalizes the accumulated fragments into tool calls (null when there are none).</summary>
    private static IReadOnlyList<ToolCall>? BuildToolCalls(SortedDictionary<int, ToolCallAccumulator> acc)
    {
        if (acc.Count == 0) return null;
        var list = new List<ToolCall>(acc.Count);
        foreach (var slot in acc.Values)
        {
            if (slot.Name is null) continue; // an id with no function is not a usable call
            var args = slot.Arguments.Length == 0 ? "{}" : slot.Arguments.ToString();
            list.Add(new ToolCall(slot.Id ?? Guid.NewGuid().ToString("N"), slot.Name, args));
        }
        return list.Count == 0 ? null : list;
    }

    private sealed class ToolCallAccumulator
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Arguments = new();
    }

    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, but reads the
    /// response body first and throws a <see cref="StrataRequestException"/> that
    /// carries it — so a 400 (e.g. a context-window overflow) surfaces the engine's
    /// own message instead of a bare "400 (Bad Request)".
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string body;
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { body = string.Empty; }
        throw StrataRequestException.From(resp.StatusCode, body);
    }

    private ModelInfoDto? SelectModel(ModelsResponse? models)
    {
        var list = models?.Data;
        if (list is null || list.Count == 0) return null;
        if (_preferredModelId is not null)
        {
            var match = list.FirstOrDefault(m =>
                string.Equals(m.Id, _preferredModelId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return list[0];
    }

    private static StringContent Serialize(ChatCompletionRequest request)
    {
        var json = JsonSerializer.Serialize(request, StrataJsonContext.Default.ChatCompletionRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}
