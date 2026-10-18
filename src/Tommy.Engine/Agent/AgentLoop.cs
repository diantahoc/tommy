using System.Runtime.CompilerServices;
using System.Text.Json;
using Tommy.Core.Diagnostics;
using Tommy.Core.Tools;
using Tommy.Engine.Dto;

namespace Tommy.Engine.Agent;

/// <summary>
/// The turn cycle: assemble → call the engine (through the single-model queue) →
/// split reasoning → on tool_calls, permission-gate and execute (reads in
/// parallel, writes/destructive/network/exec sequentially) → thread results back
/// → loop, until the model answers in prose or a budget/loop guard fires.
/// </summary>
public sealed class AgentLoop
{
    private readonly IChatCompleter _completer;
    private readonly ToolRegistry _registry;
    private readonly AgentOptions _options;

    public AgentLoop(IChatCompleter completer, ToolRegistry registry, AgentOptions? options = null)
    {
        _completer = completer;
        _registry = registry;
        _options = options ?? new AgentOptions();
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRun run, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<ChatMessageDto>
        {
            new() { Role = "system", Content = run.SystemPrompt }
        };
        messages.AddRange(run.History);

        var maxTokens = _options.MaxTokens;
        string? lastSignature = null;
        var repeatCount = 0;

        Log.Info($"run start: model={run.Model} autonomy={run.Autonomy} history={run.History.Count}");

        for (var step = 0; step < _options.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _completer.CompleteAsync(BuildRequest(run, messages, maxTokens), ct).ConfigureAwait(false);
            Log.Info($"step {step}: finish={result.FinishReason} toolCalls={result.ToolCalls.Count} " +
                     $"contentLen={result.Content.Length} tok/s={result.Timings?.PredictedPerSecond:0.0}");

            if (result.IsRecoverableLength)
            {
                maxTokens = Math.Min(maxTokens * 2, 8192);
                result = await _completer.CompleteAsync(BuildRequest(run, messages, maxTokens), ct).ConfigureAwait(false);
                if (result.IsRecoverableLength)
                {
                    yield return new AssistantStep(result.Content, result.Reasoning, Stats(result));
                    yield return new RunFinished(RunEndReason.Error, "model exhausted its token budget on reasoning");
                    yield break;
                }
            }

            yield return new AssistantStep(result.Content, result.Reasoning, Stats(result));

            if (result.ToolCalls.Count == 0 || result.FinishReason == FinishReason.Stop)
            {
                messages.Add(new ChatMessageDto { Role = "assistant", Content = result.Content });
                yield return new RunFinished(RunEndReason.Answered);
                yield break;
            }

            // Record the assistant turn (content may be empty) with its tool calls.
            messages.Add(new ChatMessageDto
            {
                Role = "assistant",
                Content = string.IsNullOrEmpty(result.Content) ? null : result.Content,
                ToolCalls = result.ToolCalls.Select(ToDto).ToList()
            });

            // Loop detection on identical consecutive tool-call sets.
            var signature = string.Join("|", result.ToolCalls.Select(c => $"{c.Name}:{c.ArgumentsJson}"));
            if (signature == lastSignature) repeatCount++;
            else { repeatCount = 1; lastSignature = signature; }
            if (repeatCount >= _options.LoopRepeatThreshold)
            {
                Log.Warn($"loop detected: {signature}");
                yield return new RunFinished(RunEndReason.LoopDetected, $"repeated the same tool call {repeatCount}×");
                yield break;
            }

            // Partition: read-tier runs in parallel; everything else sequentially.
            var readCalls = new List<ToolCall>();
            var otherCalls = new List<ToolCall>();
            foreach (var call in result.ToolCalls)
            {
                var found = _registry.Find(call.Name);
                // A read-tier tool runs in the parallel batch only when nothing forces
                // approval; an override (e.g. a conservative MCP tool) sends it to the
                // gated path so it still prompts.
                if (found?.Tier == PermissionTier.Read && found.ApprovalOverride is null) readCalls.Add(call);
                else otherCalls.Add(call);
            }

            var readOutcomes = await Task.WhenAll(readCalls.Select(c => RunReadAsync(c, run, ct))).ConfigureAwait(false);
            foreach (var outcome in readOutcomes)
            {
                yield return outcome.Started;
                yield return outcome.Finished;
                messages.Add(outcome.ToolMessage);
            }

            foreach (var call in otherCalls)
            {
                var outcome = await RunGatedAsync(call, run, ct).ConfigureAwait(false);
                yield return outcome.Started;
                yield return outcome.Finished;
                messages.Add(outcome.ToolMessage);
            }
        }

        yield return new RunFinished(RunEndReason.Budget, $"reached the step limit ({_options.MaxSteps})");
    }

    private ChatCompletionRequest BuildRequest(AgentRun run, List<ChatMessageDto> messages, int maxTokens)
    {
        var tools = _registry.BuildToolsArray(run.EnabledTools);
        return new ChatCompletionRequest
        {
            Model = run.Model,
            Messages = messages,
            MaxTokens = maxTokens,
            Temperature = _options.Temperature,
            Stream = false,
            Tools = tools.Count > 0 ? tools : null,
            ToolChoice = tools.Count > 0 ? "auto" : null
        };
    }

    private async Task<Outcome> RunReadAsync(ToolCall call, AgentRun run, CancellationToken ct)
    {
        var tool = _registry.Find(call.Name);
        var started = new ToolStarted(call.Id, call.Name, call.ArgumentsJson, tool?.Tier ?? PermissionTier.Read, null);
        var result = await ExecuteAsync(tool, call, run.ToolContext, ct).ConfigureAwait(false);
        return new Outcome(started, new ToolFinished(call.Id, call.Name, result), ToolMessage(call, result));
    }

    private async Task<Outcome> RunGatedAsync(ToolCall call, AgentRun run, CancellationToken ct)
    {
        var tool = _registry.Find(call.Name);
        var tier = tool?.Tier ?? PermissionTier.Exec;

        ToolPreview? preview = null;
        if (tool is not null && TryParseArgs(call.ArgumentsJson, out var previewArgs))
            preview = await tool.PreviewAsync(previewArgs, run.ToolContext, ct).ConfigureAwait(false);

        var started = new ToolStarted(call.Id, call.Name, call.ArgumentsJson, tier, preview);

        var action = PermissionGrid.Resolve(tier, run.Autonomy, tool?.ApprovalOverride);
        if (action == PermissionAction.Ask)
        {
            Log.Info($"approval requested: {call.Name} ({tier})");
            var approved = run.Approve is not null &&
                           await run.Approve(new ApprovalRequest(call.Id, call.Name, call.ArgumentsJson, tier, preview), ct)
                               .ConfigureAwait(false);
            Log.Info($"approval {(approved ? "granted" : "denied")}: {call.Name}");
            if (!approved)
            {
                var denied = ToolResult.Fail("The user denied this action.");
                return new Outcome(started, new ToolFinished(call.Id, call.Name, denied), ToolMessage(call, denied));
            }
        }

        var result = await ExecuteAsync(tool, call, run.ToolContext, ct).ConfigureAwait(false);
        return new Outcome(started, new ToolFinished(call.Id, call.Name, result), ToolMessage(call, result));
    }

    private async Task<ToolResult> ExecuteAsync(ITool? tool, ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        if (tool is null)
        {
            Log.Warn($"unknown tool requested: {call.Name}");
            return ToolResult.Fail($"unknown tool '{call.Name}'. Available tools: {_registry.NamesForError()}.");
        }
        if (!TryParseArgs(call.ArgumentsJson, out var args))
            return ToolResult.Fail("invalid tool arguments (not valid JSON).");

        Log.Info($"tool exec: {call.Name} args={Truncate(call.ArgumentsJson, 200)}");
        try
        {
            // Run off the calling thread so synchronous file I/O never blocks the UI.
            var result = await Task.Run(() => tool.ExecuteAsync(args, ctx, ct), ct).ConfigureAwait(false);
            Log.Info($"tool done: {call.Name} success={result.Success}");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"tool {call.Name} threw", ex);
            return ToolResult.Fail($"tool threw: {ex.Message}");
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static bool TryParseArgs(string json, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static ToolCallDto ToDto(ToolCall call) => new()
    {
        Id = call.Id,
        Type = "function",
        Function = new FunctionCallDto { Name = call.Name, Arguments = call.ArgumentsJson }
    };

    private static ChatMessageDto ToolMessage(ToolCall call, ToolResult result) => new()
    {
        Role = "tool",
        ToolCallId = call.Id,
        Name = call.Name,
        Content = result.Content
    };

    private static ChatStats? Stats(ChatTurnResult result)
    {
        if (result.Usage is null && result.Timings is null) return null;
        return new ChatStats(
            result.Timings?.PredictedPerSecond,
            result.Timings?.PromptMs,
            result.Usage?.CompletionTokens,
            result.Usage?.TotalTokens);
    }

    private readonly record struct Outcome(ToolStarted Started, ToolFinished Finished, ChatMessageDto ToolMessage);
}
