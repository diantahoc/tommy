using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Engine;
using Tommy.Engine;
using Tommy.Engine.Dto;
using Tommy.App.Rendering;

namespace Tommy.App.ViewModels;

/// <summary>
/// Drives the chat window: owns the transcript, talks to <see cref="EngineService"/>,
/// and routes streamed reasoning/content deltas through decoupled render throttles
/// so the UI stays smooth and never blocks on the loop.
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly EngineService _engine;

    public ChatViewModel(EngineService engine)
    {
        _engine = engine;
        _engine.StateChanged += (_, state) =>
            Dispatcher.UIThread.Post(() => ApplyState(state));
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    [ObservableProperty]
    private string _engineStatus = "connecting…";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Connect, detect capabilities, and post a status banner.</summary>
    public async Task InitializeAsync()
    {
        EngineHealth health;
        try
        {
            health = await _engine.ConnectAsync();
        }
        catch (Exception ex)
        {
            EngineStatus = "unreachable";
            Messages.Add(new ChatMessageViewModel(ChatRole.Assistant,
                $"Could not reach the engine: {ex.Message}", includeInHistory: false));
            return;
        }

        ApplyState(_engine.State);

        if (health.Status == HealthStatus.Ready)
        {
            var caps = _engine.Capabilities;
            Messages.Add(new ChatMessageViewModel(ChatRole.Assistant,
                $"Connected to **{_engine.ModelId}** — {caps?.MaxContextTokens:N0} token context, " +
                $"vision {(caps?.SupportsVision == true ? "on" : "off")}. Ask me anything.",
                includeInHistory: false));
        }
        else
        {
            Messages.Add(new ChatMessageViewModel(ChatRole.Assistant,
                $"Engine not ready ({health.Status}). {health.Detail}", includeInHistory: false));
        }
    }

    private bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0) return;

        Input = string.Empty;
        IsSending = true;

        Messages.Add(new ChatMessageViewModel(ChatRole.User, text));
        var assistant = new ChatMessageViewModel(ChatRole.Assistant) { IsStreaming = true };
        Messages.Add(assistant);

        // Reasoning can be throttled harder (larger cap) than the answer.
        var contentThrottle = new StreamRenderThrottle(assistant.AppendContent, capPerTick: 48);
        var reasoningThrottle = new StreamRenderThrottle(assistant.AppendReasoning, capPerTick: 120);
        contentThrottle.Start();
        reasoningThrottle.Start();

        var request = new ChatCompletionRequest
        {
            Model = _engine.ModelId,
            Messages = BuildHistory(),
            MaxTokens = 2048,
            Temperature = 0.7
        };

        try
        {
            await Task.Run(async () =>
            {
                await foreach (var ev in _engine.StreamAsync(request))
                {
                    switch (ev.Kind)
                    {
                        case ChatStreamEventKind.ReasoningDelta:
                            reasoningThrottle.Enqueue(ev.Text!);
                            break;
                        case ChatStreamEventKind.ContentDelta:
                            contentThrottle.Enqueue(ev.Text!);
                            break;
                        case ChatStreamEventKind.Completed:
                            await contentThrottle.CompleteAsync();
                            await reasoningThrottle.CompleteAsync();
                            Dispatcher.UIThread.Post(() =>
                            {
                                assistant.IsStreaming = false;
                                assistant.Stats = FormatStats(ev);
                            });
                            break;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            await contentThrottle.CompleteAsync();
            await reasoningThrottle.CompleteAsync();
            Dispatcher.UIThread.Post(() =>
            {
                assistant.IsStreaming = false;
                assistant.AppendContent($"\n\n_Error: {ex.Message}_");
            });
        }
        finally
        {
            IsSending = false;
        }
    }

    private List<ChatMessageDto> BuildHistory()
    {
        var list = new List<ChatMessageDto>();
        foreach (var m in Messages)
        {
            if (!m.IncludeInHistory) continue;
            var content = m.RawText;
            if (string.IsNullOrEmpty(content)) continue;
            list.Add(new ChatMessageDto { Role = m.IsUser ? "user" : "assistant", Content = content });
        }
        return list;
    }

    private void ApplyState(EngineState state)
    {
        EngineStatus = state switch
        {
            EngineState.Stopped => "stopped",
            EngineState.Warming => "warming…",
            EngineState.Loaded => "ready",
            EngineState.Busy => "busy",
            _ => state.ToString()
        };
        IsBusy = state == EngineState.Busy;
    }

    private static string? FormatStats(ChatStreamEvent ev)
    {
        if (ev.Usage is null && ev.Timings is null) return null;
        var parts = new List<string>();
        if (ev.Timings?.PredictedPerSecond is { } tps) parts.Add($"{tps:0.0} tok/s");
        if (ev.Timings?.PromptMs is { } ttft) parts.Add($"ttft {ttft:0} ms");
        if (ev.Usage is { } u) parts.Add($"{u.CompletionTokens} out / {u.TotalTokens} total");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }
}
