using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
            return ModelCapabilities.Unknown;

        return new ModelCapabilities(
            ModelId: model.Id ?? "unknown",
            InputModalities: model.Architecture?.InputModalities ?? new List<string>(),
            OutputModalities: model.Architecture?.OutputModalities ?? new List<string>(),
            MaxContextTokens: model.Meta?.NCtx ?? 0);
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

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var m = await JsonSerializer.DeserializeAsync(
            stream, StrataJsonContext.Default.MetricsResponse, ct).ConfigureAwait(false);

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

        var finish = FinishReason.Other;
        UsageDto? usage = null;
        TimingsDto? timings = null;
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

            if (choice.FinishReason is not null)
                finish = ChatTurnResult.MapFinishReason(choice.FinishReason);
        }

        if (debug)
            await Console.Error.WriteLineAsync($"[stream] raw lines read: {lineCount}, finish: {finish}");

        yield return new ChatStreamEvent(
            ChatStreamEventKind.Completed, Text: null, FinishReason: finish, Usage: usage, Timings: timings);
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
