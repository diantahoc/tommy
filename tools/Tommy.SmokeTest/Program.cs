using System.Diagnostics;
using Tommy.Core.Configuration;
using Tommy.Core.Engine;
using Tommy.Engine;
using Tommy.Engine.Dto;

// Smoke test for Tommy.Engine against a live Strata endpoint.
// Usage: dotnet run --project tools/Tommy.SmokeTest [baseUrl]
// Default baseUrl: http://127.0.0.1:8080/v1

var baseUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:8080/v1";

var profile = new EngineProfile
{
    Name = "dev",
    Mode = EngineMode.Remote,
    BaseUrl = baseUrl
};

using var engine = new EngineService(profile);
engine.StateChanged += (_, s) => Console.WriteLine($"   [state] -> {s}");

Section("1. Connect + health + capability detection");
var health = await engine.ConnectAsync();
Console.WriteLine($"   health: {health.Status} (reachable={health.EndpointReachable}, loaded={health.ModelLoaded}, model={health.ModelId})");
if (health.Status != HealthStatus.Ready)
{
    Console.WriteLine("   engine not ready — aborting. Is Strata running?");
    return 1;
}
var caps = engine.Capabilities!;
Console.WriteLine($"   model: {caps.ModelId}");
Console.WriteLine($"   max context: {caps.MaxContextTokens:N0} tokens");
Console.WriteLine($"   input modalities: [{string.Join(", ", caps.InputModalities)}]");
Console.WriteLine($"   vision: {(caps.SupportsVision ? "ON" : "OFF")}");

Section("2. Telemetry (/metrics)");
var t = await engine.GetTelemetryAsync();
Console.WriteLine($"   live state: {t.LiveState}, queued: {t.Queued}");
Console.WriteLine($"   GPU: {t.Static.GpuName} | util {t.Hardware.GpuUtil:0}% | temp {t.Hardware.GpuTempC:0}C | power {t.Hardware.GpuPowerWatts:0}/{t.Hardware.GpuPowerLimitWatts:0}W");
Console.WriteLine($"   VRAM: {Gib(t.Hardware.GpuMemUsedBytes):0.0}/{Gib(t.Hardware.GpuMemTotalBytes):0.0} GiB");
Console.WriteLine($"   CPU: {t.Static.CpuName} | {t.Hardware.CpuPercent:0.0}% | RAM {Gib(t.Hardware.RamUsedBytes):0.0}/{Gib(t.Hardware.RamTotalBytes):0.0} GiB");

Section("3. Non-streaming completion (reasoning split)");
var result = await engine.CompleteAsync(Request("In one short sentence, what is a compiler?", maxTokens: 512));
Console.WriteLine($"   reasoning: {Trunc(result.Reasoning, 160)}");
Console.WriteLine($"   content:   {result.Content}");
Console.WriteLine($"   finish:    {result.FinishReason}");
if (result.Usage is { } u)
    Console.WriteLine($"   usage:     prompt={u.PromptTokens} completion={u.CompletionTokens} total={u.TotalTokens} cached={u.PromptTokensDetails?.CachedTokens ?? 0}");
if (result.Timings is { } ti)
    Console.WriteLine($"   stats:     {ti.PredictedPerSecond:0.0} tok/s, ttft {ti.PromptMs:0} ms");

Section("4. Streaming completion (live reasoning then answer)");
Console.Write("   [reasoning] ");
var inReasoning = true;
await foreach (var ev in engine.StreamAsync(Request("Name one primary color. One word.", maxTokens: 256)))
{
    switch (ev.Kind)
    {
        case ChatStreamEventKind.ReasoningDelta:
            Console.Write(ev.Text);
            break;
        case ChatStreamEventKind.ContentDelta:
            if (inReasoning) { Console.Write("\n   [answer]    "); inReasoning = false; }
            Console.Write(ev.Text);
            break;
        case ChatStreamEventKind.Completed:
            Console.WriteLine($"\n   finish: {ev.FinishReason}");
            break;
    }
}

Section("5. Request serialization (single-model queue)");
Console.WriteLine("   firing 3 completions concurrently; they must run one at a time...");
var sw = Stopwatch.StartNew();
var order = new List<string>();
var tasks = Enumerable.Range(1, 3).Select(async i =>
{
    var r = await engine.CompleteAsync(Request($"Reply with just the number {i}.", maxTokens: 64));
    lock (order) order.Add($"#{i} done @ {sw.ElapsedMilliseconds} ms");
}).ToArray();
await Task.WhenAll(tasks);
foreach (var line in order) Console.WriteLine($"   {line}");
Console.WriteLine("   (completion times are staggered => serialized, not parallel)");

Section("6. finish_reason=length with empty content (recoverable)");
var starved = await engine.CompleteAsync(Request("Explain quantum entanglement in detail.", maxTokens: 8));
Console.WriteLine($"   finish: {starved.FinishReason}, content empty: {string.IsNullOrEmpty(starved.Content)}");
Console.WriteLine($"   IsRecoverableLength: {starved.IsRecoverableLength}  (would retry with more budget / lower reasoning)");

Console.WriteLine("\nAll checks completed.");
return 0;

ChatCompletionRequest Request(string prompt, int maxTokens) => new()
{
    Model = engine.ModelId,
    Messages = new List<ChatMessageDto> { new() { Role = "user", Content = prompt } },
    MaxTokens = maxTokens,
    Temperature = 0.7
};

static void Section(string title) => Console.WriteLine($"\n=== {title} ===");
static double Gib(long bytes) => bytes / 1024.0 / 1024.0 / 1024.0;
static string Trunc(string? s, int n) =>
    string.IsNullOrEmpty(s) ? "(none)" : (s.Length <= n ? s : s[..n] + "...");
