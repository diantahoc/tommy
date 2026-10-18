using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Chat;
using Tommy.Core.Events;
using Tommy.Core.Tools;
using Tommy.Engine;
using Tommy.Engine.Agent;
using Tommy.Engine.Dto;
using Tommy.Persistence.Storage;
using Tommy.Tools;

namespace Tommy.App.ViewModels;

/// <summary>
/// One chat: owns its transcript, runs the agent loop, persists every turn to
/// the chat's JSONL event log, and rebuilds from that log when reopened.
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly EngineService _engine;
    private readonly ToolRegistry _registry;
    private readonly ChatRepository _repo;
    private readonly ChatHandle _handle;
    private readonly ResolvedParameters _resolved;
    private readonly AgentLoop _loop;

    public ChatViewModel(
        EngineService engine, ToolRegistry registry, ChatRepository repo,
        ChatHandle handle, string workingDirectory, ResolvedParameters resolved)
    {
        _engine = engine;
        _registry = registry;
        _repo = repo;
        _handle = handle;
        _resolved = resolved;
        _loop = new AgentLoop(engine, registry, new AgentOptions
        {
            MaxTokens = resolved.MaxTokens,
            Temperature = resolved.Temperature
        });

        WorkingDirectory = workingDirectory;
        _autonomy = resolved.Autonomy;

        LoadHistory();
    }

    public string ChatId => _handle.Record.Id;
    public string WorkingDirectory { get; }
    public string WorkingDirectoryLabel => WorkingDirectory;

    /// <summary>Raised when the title (or other list-visible metadata) changes.</summary>
    public event Action? MetadataChanged;

    public ObservableCollection<object> Messages { get; } = new();

    public AutonomyLevel[] AutonomyOptions { get; } = Enum.GetValues<AutonomyLevel>();

    [ObservableProperty]
    private AutonomyLevel _autonomy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    [ObservableProperty]
    private ApprovalViewModel? _pendingApproval;

    partial void OnAutonomyChanged(AutonomyLevel value)
    {
        _handle.Record.Parameters.Autonomy = value;
        _repo.SaveMeta(_handle.Record);
    }

    private void LoadHistory()
    {
        foreach (var ev in _handle.Events.Read())
        {
            switch (ev)
            {
                case UserEvent u:
                    Messages.Add(new ChatMessageViewModel(ChatRole.User, u.Text));
                    break;
                case AssistantEvent a when !string.IsNullOrEmpty(a.Content) || !string.IsNullOrEmpty(a.Reasoning):
                    var msg = new ChatMessageViewModel(ChatRole.Assistant);
                    if (!string.IsNullOrEmpty(a.Reasoning)) msg.AppendReasoning(a.Reasoning);
                    if (!string.IsNullOrEmpty(a.Content)) msg.AppendContent(a.Content);
                    Messages.Add(msg);
                    break;
                case ToolResultEvent t:
                    var tier = _registry.Find(t.Tool)?.Tier ?? PermissionTier.Read;
                    var card = new ToolCallViewModel(t.CallId, t.Tool, string.Empty, tier, null);
                    card.Complete(t.Success ? ToolResult.Ok(t.Result) : ToolResult.Fail(t.Result));
                    Messages.Add(card);
                    break;
            }
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
        _handle.Events.Append(new UserEvent { Text = text });
        MaybeTitle(text);

        var run = new AgentRun
        {
            Model = _engine.ModelId,
            SystemPrompt = _resolved.SystemPrompt,
            History = BuildHistory(),
            ToolContext = new ToolContext(WorkingDirectory),
            Autonomy = Autonomy,
            EnabledTools = _resolved.EnabledTools,
            Approve = HandleApprovalAsync
        };

        ChatMessageViewModel? current = null;
        try
        {
            await foreach (var ev in _loop.RunAsync(run))
            {
                switch (ev)
                {
                    case AssistantStep step:
                        current = new ChatMessageViewModel(ChatRole.Assistant) { IsStreaming = true };
                        if (!string.IsNullOrEmpty(step.Reasoning)) current.AppendReasoning(step.Reasoning);
                        if (!string.IsNullOrEmpty(step.Content)) current.AppendContent(step.Content);
                        current.Stats = FormatStats(step.Stats);
                        Messages.Add(current);
                        _handle.Events.Append(new AssistantEvent { Content = step.Content, Reasoning = step.Reasoning });
                        break;

                    case ToolStarted started:
                        Messages.Add(new ToolCallViewModel(
                            started.CallId, started.ToolName, started.ArgumentsJson,
                            started.Tier, started.Preview?.Diff));
                        break;

                    case ToolFinished finished:
                        FindToolCall(finished.CallId)?.Complete(finished.Result);
                        _handle.Events.Append(new ToolResultEvent
                        {
                            CallId = finished.CallId,
                            Tool = finished.ToolName,
                            Success = finished.Result.Success,
                            Result = finished.Result.Content
                        });
                        break;

                    case RunFinished done:
                        if (current is not null) current.IsStreaming = false;
                        _handle.Events.Append(new MetaEvent { Kind = done.Reason.ToString(), Detail = done.Note });
                        if (done.Reason != RunEndReason.Answered && done.Note is not null)
                            AddBanner($"_Run paused: {done.Note}._");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AddBanner($"_Error: {ex.Message}_");
            _handle.Events.Append(new MetaEvent { Kind = "error", Detail = ex.Message });
        }
        finally
        {
            if (current is not null) current.IsStreaming = false;
            PendingApproval = null;
            IsSending = false;
            _repo.SaveMeta(_handle.Record);
        }
    }

    private void MaybeTitle(string firstUserText)
    {
        if (!string.Equals(_handle.Record.Title, "New chat", StringComparison.Ordinal)) return;
        var title = firstUserText.Length > 40 ? firstUserText[..40] + "…" : firstUserText;
        _handle.Record.Title = title.Replace('\n', ' ');
        _repo.SaveMeta(_handle.Record);
        MetadataChanged?.Invoke();
    }

    private Task<bool> HandleApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        void Show() => PendingApproval = new ApprovalViewModel(request, approved =>
        {
            PendingApproval = null;
            tcs.TrySetResult(approved);
        });
        if (Dispatcher.UIThread.CheckAccess()) Show();
        else Dispatcher.UIThread.Post(Show);
        return tcs.Task;
    }

    private ToolCallViewModel? FindToolCall(string callId)
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
            if (Messages[i] is ToolCallViewModel tc && tc.CallId == callId)
                return tc;
        return null;
    }

    public void AddBanner(string markdown) =>
        Messages.Add(new ChatMessageViewModel(ChatRole.Assistant, markdown, includeInHistory: false));

    private List<ChatMessageDto> BuildHistory()
    {
        var list = new List<ChatMessageDto>();
        foreach (var m in Messages.OfType<ChatMessageViewModel>())
        {
            if (!m.IncludeInHistory) continue;
            var content = m.RawText;
            if (string.IsNullOrEmpty(content)) continue;
            list.Add(new ChatMessageDto { Role = m.IsUser ? "user" : "assistant", Content = content });
        }
        return list;
    }

    private static string? FormatStats(ChatStats? stats)
    {
        if (stats is null) return null;
        var parts = new List<string>();
        if (stats.TokensPerSecond is { } tps) parts.Add($"{tps:0.0} tok/s");
        if (stats.TtftMs is { } ttft) parts.Add($"ttft {ttft:0} ms");
        if (stats.CompletionTokens is { } c && stats.TotalTokens is { } t) parts.Add($"{c} out / {t} total");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }
}
