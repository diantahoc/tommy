using System.Runtime.CompilerServices;
using System.Text.Json;
using Tommy.Core.Chat;
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
    private const int MaxTokenCeiling = 8192;

    /// <summary>Per-tool-result cap on what is fed back to the model (~5K tokens).</summary>
    private const int MaxToolResultChars = 20_000;

    private const string ContextOverflowNote =
        "the conversation outgrew the model's context window and couldn't be compacted enough to continue — start a new chat or narrow the task";

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

            var outcome = await CompleteOrCompactAsync(run, messages, maxTokens, ct).ConfigureAwait(false);
            if (outcome.Result is null)
            {
                yield return new RunFinished(RunEndReason.Error, ContextOverflowNote);
                yield break;
            }
            if (outcome.Compacted) yield return new ContextCompacted(outcome.CompactNote);
            var result = outcome.Result;
            Log.Info($"step {step}: finish={result.FinishReason} toolCalls={result.ToolCalls.Count} " +
                     $"contentLen={result.Content.Length} tok/s={result.Timings?.PredictedPerSecond:0.0}");

            // A "length" finish means the model was cut off at the output cap — very
            // often right before the tool calls it was about to make (so toolCalls=0
            // even though it wasn't done). Give it more room and regenerate the step,
            // instead of mistaking the fragment for a final answer and ending the run.
            while (result.FinishReason == FinishReason.Length && maxTokens < MaxTokenCeiling)
            {
                maxTokens = Math.Min(maxTokens * 2, MaxTokenCeiling);
                Log.Info($"step {step}: output truncated, retrying at maxTokens={maxTokens}");
                outcome = await CompleteOrCompactAsync(run, messages, maxTokens, ct).ConfigureAwait(false);
                if (outcome.Result is null)
                {
                    yield return new RunFinished(RunEndReason.Error, ContextOverflowNote);
                    yield break;
                }
                if (outcome.Compacted) yield return new ContextCompacted(outcome.CompactNote);
                result = outcome.Result;
            }

            yield return new AssistantStep(result.Content, result.Reasoning, Stats(result));

            if (result.ToolCalls.Count == 0 || result.FinishReason == FinishReason.Stop)
            {
                messages.Add(new ChatMessageDto { Role = "assistant", Content = result.Content });
                // Still truncated after growing to the ceiling with no tool calls: the
                // model ran out of output budget mid-turn. Pause visibly so the user can
                // tell it to continue, rather than reporting a silent (partial) answer.
                if (result.ToolCalls.Count == 0 && result.FinishReason == FinishReason.Length)
                    yield return new RunFinished(RunEndReason.Budget,
                        "the model hit its output-token limit mid-turn — send \"continue\" to let it finish",
                        ResumeSnapshot(messages));
                else
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

            // Parallel reads: announce every call BEFORE running it, so the UI shows the
            // active tool immediately (and never sits on a silent "Thinking…" while a
            // read is slow or hung). Then run the batch and report each result.
            if (readCalls.Count > 0)
            {
                foreach (var call in readCalls)
                {
                    var tool = _registry.Find(call.Name);
                    yield return new ToolStarted(call.Id, call.Name, call.ArgumentsJson, tool?.Tier ?? PermissionTier.Read, null);
                }

                var results = await Task.WhenAll(
                    readCalls.Select(c => ExecuteAsync(_registry.Find(c.Name), c, run.ToolContext, ct))).ConfigureAwait(false);

                for (var i = 0; i < readCalls.Count; i++)
                {
                    yield return new ToolFinished(readCalls[i].Id, readCalls[i].Name, results[i]);
                    messages.Add(ToolMessage(readCalls[i], results[i]));
                }
            }

            // Gated tools run one at a time. Announce each (with its preview) before the
            // approval prompt and execution, so the indicator shows the tool that is
            // awaiting approval or running.
            foreach (var call in otherCalls)
            {
                var tool = _registry.Find(call.Name);
                var tier = tool?.Tier ?? PermissionTier.Exec;

                ToolPreview? preview = null;
                if (tool is not null && TryParseArgs(call.ArgumentsJson, out var previewArgs))
                    preview = await tool.PreviewAsync(previewArgs, run.ToolContext, ct).ConfigureAwait(false);

                yield return new ToolStarted(call.Id, call.Name, call.ArgumentsJson, tier, preview);

                ToolResult toolResult;
                var action = PermissionGrid.Resolve(tier, run.Autonomy, tool?.ApprovalOverride);
                if (action == PermissionAction.Ask)
                {
                    Log.Info($"approval requested: {call.Name} ({tier})");
                    var decision = run.Approve is not null
                        ? await run.Approve(new ApprovalRequest(call.Id, call.Name, call.ArgumentsJson, tier, preview), ct)
                              .ConfigureAwait(false)
                        : new ApprovalDecision(false);
                    Log.Info($"approval {(decision.Approved ? "granted" : "denied")}: {call.Name}");

                    if (!decision.Approved)
                    {
                        // A denial stops the whole run and hands control back to the user,
                        // rather than feeding a failure the model would route around. The
                        // optional reason is fed back so the next turn has the context.
                        var reason = string.IsNullOrWhiteSpace(decision.Reason) ? null : decision.Reason!.Trim();
                        var denyMsg = reason is null
                            ? "The user denied this action and stopped the run. Do not retry or try another approach — wait for the user's next instruction."
                            : $"The user denied this action and stopped the run. Reason: {reason}. Do not retry or try another approach — wait for the user's next instruction.";
                        var denied = ToolResult.Fail(denyMsg);
                        yield return new ToolFinished(call.Id, call.Name, denied);
                        messages.Add(ToolMessage(call, denied));
                        yield return new RunFinished(RunEndReason.Stopped, reason);
                        yield break;
                    }

                    toolResult = await ExecuteAsync(tool, call, run.ToolContext, ct).ConfigureAwait(false);
                }
                else
                {
                    toolResult = await ExecuteAsync(tool, call, run.ToolContext, ct).ConfigureAwait(false);
                }

                yield return new ToolFinished(call.Id, call.Name, toolResult);
                messages.Add(ToolMessage(call, toolResult));
            }
        }

        yield return new RunFinished(RunEndReason.Budget, $"reached the step limit ({_options.MaxSteps})",
            ResumeSnapshot(messages));
    }

    /// <summary>
    /// The run's working set to hand to a continuation: everything except the system
    /// prompt (the next turn rebuilds that fresh). Lets "continue" resume with the tool
    /// calls and results already in hand instead of a prose-only history, so the model
    /// doesn't redo work it already did.
    /// </summary>
    private static IReadOnlyList<ChatMessageDto> ResumeSnapshot(List<ChatMessageDto> messages) =>
        messages.Count > 1 ? messages.GetRange(1, messages.Count - 1) : Array.Empty<ChatMessageDto>();

    private readonly record struct CompletionOutcome(ChatTurnResult? Result, bool Compacted, string? CompactNote);

    /// <summary>
    /// Completes a step, and if the engine rejects the request for exceeding the
    /// context window, compacts the in-run history once and retries. Returns a null
    /// result only when even compaction can't make it fit.
    /// </summary>
    private async Task<CompletionOutcome> CompleteOrCompactAsync(
        AgentRun run, List<ChatMessageDto> messages, int maxTokens, CancellationToken ct)
    {
        try
        {
            var r = await _completer.CompleteAsync(BuildRequest(run, messages, maxTokens), ct).ConfigureAwait(false);
            return new CompletionOutcome(r, false, null);
        }
        catch (StrataRequestException ex) when (ex.IsContextOverflow)
        {
            Log.Warn($"context overflow — compacting and retrying: {ex.Message}");
            var note = await CompactAsync(run, messages, ct).ConfigureAwait(false);
            if (note is null)
                return new CompletionOutcome(null, false, null);
            try
            {
                var r = await _completer.CompleteAsync(BuildRequest(run, messages, maxTokens), ct).ConfigureAwait(false);
                return new CompletionOutcome(r, true, note);
            }
            catch (StrataRequestException ex2) when (ex2.IsContextOverflow)
            {
                Log.Warn($"context overflow persists after compaction: {ex2.Message}");
                return new CompletionOutcome(null, false, null);
            }
        }
    }

    /// <summary>
    /// Frees context by summarizing older steps with the model, falling back to the
    /// deterministic drop when the summary call itself can't fit. Returns a short note
    /// for the UI, or null when nothing could be compacted.
    /// </summary>
    private async Task<string?> CompactAsync(AgentRun run, List<ChatMessageDto> messages, CancellationToken ct)
    {
        if (await ContextSummarizer.TrySummarizeAsync(_completer, run.Model, messages, ct).ConfigureAwait(false))
            return "summarized earlier steps";
        if (ContextCompactor.Compact(messages))
            return "dropped older tool output";
        return null;
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
            ReasoningEffort = MapEffort(run.ThinkingEffort),
            Tools = tools.Count > 0 ? tools : null,
            ToolChoice = tools.Count > 0 ? "auto" : null
        };
    }

    /// <summary>Map abstract effort to the engine's <c>reasoning_effort</c>; Off omits it.</summary>
    private static string? MapEffort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        _ => null
    };

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
        Content = CapToolContent(result.Content)
    };

    /// <summary>
    /// Bounds a single tool result before it enters the model's context, so one huge
    /// output (a wide query, a big file) can't blow the window on its own. The UI
    /// still shows the full result — only what's fed back to the model is capped.
    /// </summary>
    private static string CapToolContent(string? content)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= MaxToolResultChars)
            return content ?? string.Empty;
        return content[..MaxToolResultChars] +
            $"\n… [truncated: {content.Length:N0} chars total, first {MaxToolResultChars:N0} kept — " +
            "narrow the query or path, or request a specific slice]";
    }

    private static ChatStats? Stats(ChatTurnResult result)
    {
        if (result.Usage is null && result.Timings is null) return null;
        return new ChatStats(
            result.Timings?.PredictedPerSecond,
            result.Timings?.PromptMs,
            result.Usage?.CompletionTokens,
            result.Usage?.TotalTokens);
    }
}
