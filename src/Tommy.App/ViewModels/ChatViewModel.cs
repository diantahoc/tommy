using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Chat;
using Tommy.Core.Diagnostics;
using Tommy.Core.Events;
using Tommy.Core.Skills;
using Tommy.Core.Telemetry;
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
    private readonly ISkillProvider _skills;
    private readonly IReadOnlyList<string> _memoryDirs;
    private readonly string? _writeMemoryDir;
    private readonly AgentLoop _loop;
    private readonly EnergyMeter _energy = new();
    private readonly double _electricityRate;
    private readonly string _currency;
    private double _messageStartWh;
    private ActivityViewModel? _activity;

    public ChatViewModel(
        EngineService engine, ToolRegistry registry, ChatRepository repo,
        ChatHandle handle, string workingDirectory, ResolvedParameters resolved,
        ISkillProvider skills, IReadOnlyList<string> memoryDirs, string? writeMemoryDir,
        double electricityRate = 0.0, string currency = "$")
    {
        _electricityRate = electricityRate;
        _currency = currency;
        _engine = engine;
        _registry = registry;
        _repo = repo;
        _handle = handle;
        _resolved = resolved;
        _skills = skills;
        _memoryDirs = memoryDirs;
        _writeMemoryDir = writeMemoryDir;
        _loop = new AgentLoop(engine, registry, new AgentOptions
        {
            MaxTokens = resolved.MaxTokens,
            Temperature = resolved.Temperature
        });

        WorkingDirectory = workingDirectory;
        _autonomy = resolved.Autonomy;
        _thinkingEffort = resolved.ThinkingEffort;
        _contextMax = engine.Capabilities?.MaxContextTokens ?? 0;
        GitBranch = ReadGitBranch(workingDirectory);

        LoadHistory();
    }

    /// <summary>Read the active branch from .git/HEAD — no git process, cross-platform.</summary>
    private static string? ReadGitBranch(string dir)
    {
        try
        {
            var head = Path.Combine(dir, ".git", "HEAD");
            if (!File.Exists(head)) return null;
            var text = File.ReadAllText(head).Trim();
            if (text.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                return text["ref: refs/heads/".Length..];
            return text.Length >= 7 ? text[..7] : null; // detached HEAD -> short hash
        }
        catch
        {
            return null;
        }
    }

    partial void OnThinkingEffortChanged(ThinkingEffort value)
    {
        _handle.Record.Parameters.ThinkingEffort = value;
        _repo.SaveMeta(_handle.Record);
    }

    /// <summary>Files staged for the next message (shown as chips/thumbnails).</summary>
    public ObservableCollection<AttachmentViewModel> PendingAttachments { get; } = new();

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    private const string AttachmentsFolder = "attachments";

    /// <summary>
    /// Stage files for the next message. Each file is copied into the chat's
    /// working directory (<c>attachments/</c>) so the model's sandboxed tools can
    /// read it, and is shown as an image thumbnail or a named document chip.
    /// </summary>
    public void AttachFiles(IEnumerable<string> paths)
    {
        var dir = Path.Combine(WorkingDirectory, AttachmentsFolder);
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                Directory.CreateDirectory(dir);
                var destName = UniqueName(dir, Path.GetFileName(path));
                var dest = Path.Combine(dir, destName);
                File.Copy(path, dest, overwrite: false);

                var rel = $"{AttachmentsFolder}/{destName}";
                var vm = new AttachmentViewModel(Path.GetFileName(path), dest, rel, showRemove: true);
                vm.RemoveRequested = () => RemovePendingAttachment(vm);
                PendingAttachments.Add(vm);
            }
            catch (Exception ex)
            {
                Log.Error($"attach failed: {path}", ex);
            }
        }
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RemovePendingAttachment(AttachmentViewModel vm)
    {
        PendingAttachments.Remove(vm);
        if (vm.FullPath is { } p) { try { File.Delete(p); } catch { /* best effort */ } }
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Avoid clobbering an existing attachment with the same name.</summary>
    private static string UniqueName(string dir, string name)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var n = 1;
        while (File.Exists(Path.Combine(dir, candidate)))
            candidate = $"{stem} ({++n}){ext}";
        return candidate;
    }

    private static string? AttachmentNote(IReadOnlyList<AttachmentViewModel> attachments)
    {
        if (attachments.Count == 0) return null;
        var paths = string.Join(", ", attachments.Select(a => $"`{a.RelativePath}`"));
        return $"[Attached in workspace: {paths}]";
    }

    public string ChatId => _handle.Record.Id;
    public string WorkingDirectory { get; }
    public string WorkingDirectoryLabel => WorkingDirectory;

    public bool IsCodeChat => _handle.Record.Kind == ChatKind.Code;

    /// <summary>Just the workspace folder name (shown in the below-input status bar).</summary>
    public string WorkspaceLabel => Path.GetFileName(WorkingDirectory.TrimEnd('/', '\\'));

    /// <summary>The active git branch of the working directory, or null if not a repo.</summary>
    public string? GitBranch { get; }

    public bool HasGitBranch => !string.IsNullOrEmpty(GitBranch);

    public ThinkingEffort[] ThinkingEffortOptions { get; } = Enum.GetValues<ThinkingEffort>();

    [ObservableProperty]
    private ThinkingEffort _thinkingEffort;

    // --- Context ring -------------------------------------------------------

    private readonly int _contextMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextPercent))]
    [NotifyPropertyChangedFor(nameof(ContextLabel))]
    [NotifyPropertyChangedFor(nameof(ContextPercentLabel))]
    private int _contextUsed;

    public double ContextPercent => _contextMax > 0 ? Math.Min(100.0, 100.0 * ContextUsed / _contextMax) : 0;
    public string ContextPercentLabel => _contextMax > 0 ? $"{ContextPercent:0}%" : "–";
    public string ContextLabel => _contextMax > 0
        ? $"{ContextUsed / 1000.0:0.0}k / {_contextMax / 1000}k tokens"
        : "context usage";

    // --- Electricity cost (local power, not tokens) -------------------------

    public bool HasCost => _electricityRate > 0;
    public string CostLabel => $"{_currency}{_energy.Cost(_electricityRate):0.0000}";
    public string EnergyLabel => $"{_energy.Wh:0.0} Wh this chat";

    /// <summary>Feed a GPU-power sample while this chat is generating.</summary>
    public void SampleEnergy(double watts, DateTimeOffset at)
    {
        _energy.Sample(watts, at);
        OnPropertyChanged(nameof(CostLabel));
        OnPropertyChanged(nameof(EnergyLabel));
    }

    /// <summary>End the integration interval (generation paused/idle).</summary>
    public void PauseEnergy() => _energy.Pause();

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
        _activity = null;
        foreach (var ev in _handle.Events.Read())
        {
            switch (ev)
            {
                case UserEvent u:
                    CloseActivity();
                    var um = new ChatMessageViewModel(ChatRole.User, u.Text);
                    foreach (var att in u.Attachments)
                    {
                        var full = att.RelativePath is { } rel
                            ? Path.Combine(WorkingDirectory, rel.Replace('/', Path.DirectorySeparatorChar))
                            : null;
                        um.AddAttachment(new AttachmentViewModel(att.Name, full, att.RelativePath ?? att.Name, showRemove: false));
                    }
                    Messages.Add(um);
                    break;

                case AssistantEvent a when !string.IsNullOrEmpty(a.Content):
                    if (!string.IsNullOrEmpty(a.Reasoning)) EnsureActivity().AddThinking(a.Reasoning);
                    CloseActivity();
                    var msg = new ChatMessageViewModel(ChatRole.Assistant);
                    msg.AppendContent(a.Content);
                    Messages.Add(msg);
                    break;

                case AssistantEvent a when !string.IsNullOrEmpty(a.Reasoning):
                    EnsureActivity().AddThinking(a.Reasoning);
                    break;

                case ToolResultEvent t:
                    var tier = _registry.Find(t.Tool)?.Tier ?? PermissionTier.Read;
                    var card = new ToolCallViewModel(t.CallId, t.Tool, string.Empty, tier, null);
                    card.Complete(t.Success ? ToolResult.Ok(t.Result) : ToolResult.Fail(t.Result));
                    EnsureActivity().AddTool(card);
                    break;
            }
        }
        CloseActivity();
    }

    private ActivityViewModel EnsureActivity()
    {
        if (_activity is null)
        {
            _activity = new ActivityViewModel();
            Messages.Add(_activity);
        }
        return _activity;
    }

    private void CloseActivity()
    {
        if (_activity is null) return;
        if (!_activity.HasItems) Messages.Remove(_activity);
        else _activity.Finish();
        _activity = null;
    }

    private bool CanSend() => !IsSending && (!string.IsNullOrWhiteSpace(Input) || PendingAttachments.Count > 0);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var typed = Input.Trim();
        var staged = PendingAttachments.ToList();
        if (typed.Length == 0 && staged.Count == 0) return;

        // The model sees the attachment paths appended as a note; the bubble shows
        // the same note plus thumbnails/chips.
        var note = AttachmentNote(staged);
        var text = (typed, note) switch
        {
            ("", not null) => note,
            (_, not null) => $"{typed}\n\n{note}",
            _ => typed
        };

        Input = string.Empty;
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasPendingAttachments));
        IsSending = true;
        _messageStartWh = _energy.Wh;

        Log.Info($"chat {ChatId}: send ({text.Length} chars, {staged.Count} attachments)");
        var isFirstMessage = string.Equals(_handle.Record.Title, "New chat", StringComparison.Ordinal);

        var userMsg = new ChatMessageViewModel(ChatRole.User, text);
        foreach (var a in staged)
            userMsg.AddAttachment(new AttachmentViewModel(a.FileName, a.FullPath, a.RelativePath, showRemove: false));
        Messages.Add(userMsg);

        _handle.Events.Append(new UserEvent
        {
            Text = text,
            Attachments = staged.Select(a => new Attachment
            {
                Name = a.FileName,
                Mime = AttachmentViewModel.GuessMime(a.FileName),
                RelativePath = a.RelativePath
            }).ToList()
        });
        MaybeTitle(text); // instant interim title; the model refines it after the run

        var run = new AgentRun
        {
            Model = _engine.ModelId,
            SystemPrompt = BuildSystemPrompt(),
            History = BuildHistory(),
            ToolContext = new ToolContext(
                WorkingDirectory,
                MemoryDirs: _memoryDirs.Count > 0 ? _memoryDirs : null,
                WriteMemoryDir: _writeMemoryDir,
                VisionEnabled: _engine.Capabilities?.SupportsVision == true),
            Autonomy = Autonomy,
            ThinkingEffort = ThinkingEffort,
            EnabledTools = _resolved.EnabledTools,
            Approve = HandleApprovalAsync
        };

        _activity = null;
        try
        {
            await foreach (var ev in _loop.RunAsync(run))
            {
                switch (ev)
                {
                    // Final prose answer: close the activity, render as a normal bubble.
                    case AssistantStep step when !string.IsNullOrEmpty(step.Content):
                        if (!string.IsNullOrEmpty(step.Reasoning)) EnsureActivity().AddThinking(step.Reasoning);
                        CloseActivity();
                        var answer = new ChatMessageViewModel(ChatRole.Assistant);
                        answer.AppendContent(step.Content);
                        answer.Stats = AppendEnergy(FormatStats(step.Stats));
                        if (step.Stats?.TotalTokens is { } used) ContextUsed = used;
                        Messages.Add(answer);
                        _handle.Events.Append(new AssistantEvent { Content = step.Content, Reasoning = step.Reasoning });
                        break;

                    // Reasoning-only step (before tool calls): goes into the activity.
                    case AssistantStep step when !string.IsNullOrEmpty(step.Reasoning):
                        EnsureActivity().AddThinking(step.Reasoning);
                        _handle.Events.Append(new AssistantEvent { Content = string.Empty, Reasoning = step.Reasoning });
                        break;

                    case ToolStarted started:
                        EnsureActivity().AddTool(new ToolCallViewModel(
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
                        CloseActivity();
                        _handle.Events.Append(new MetaEvent { Kind = done.Reason.ToString(), Detail = done.Note });
                        if (done.Reason != RunEndReason.Answered && done.Note is not null)
                            AddBanner($"_Run paused: {done.Note}._");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"chat {ChatId}: run failed", ex);
            CloseActivity();
            AddBanner($"_Error: {ex.Message}_");
            _handle.Events.Append(new MetaEvent { Kind = "error", Detail = ex.Message });
        }
        finally
        {
            CloseActivity();
            PendingApproval = null;
            IsSending = false;
            _repo.SaveMeta(_handle.Record);
            Log.Info($"chat {ChatId}: send complete");
        }

        if (isFirstMessage)
            _ = GenerateTitleAsync(text);
    }

    /// <summary>Ask the model for a short chat title after the first exchange.</summary>
    private async Task GenerateTitleAsync(string firstMessage)
    {
        try
        {
            var request = new ChatCompletionRequest
            {
                Model = _engine.ModelId,
                MaxTokens = 256,
                Temperature = 0.3,
                Messages = new List<ChatMessageDto>
                {
                    new()
                    {
                        Role = "system",
                        Content = "Generate a concise 3-5 word title for a chat that begins with the user's message. " +
                                  "Reply with ONLY the title text — no quotes, no punctuation, no preamble."
                    },
                    new() { Role = "user", Content = firstMessage }
                }
            };

            var result = await _engine.CompleteAsync(request);
            var title = CleanTitle(result.Content);
            if (title.Length == 0) return;

            _handle.Record.Title = title;
            _repo.SaveMeta(_handle.Record);
            await Dispatcher.UIThread.InvokeAsync(() => MetadataChanged?.Invoke());
            Log.Info($"chat {ChatId}: titled \"{title}\"");
        }
        catch (Exception ex)
        {
            Log.Error("title generation failed", ex);
        }
    }

    private static string CleanTitle(string raw)
    {
        var t = raw.Trim().Trim('"', '\'', '.', '#').Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length > 60 ? t[..60] : t;
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
        if (_activity is null) return null;
        for (var i = _activity.Items.Count - 1; i >= 0; i--)
            if (_activity.Items[i] is ToolCallViewModel tc && tc.CallId == callId)
                return tc;
        return null;
    }

    public void AddBanner(string markdown) =>
        Messages.Add(new ChatMessageViewModel(ChatRole.Assistant, markdown, includeInHistory: false));

    /// <summary>
    /// Assemble the system prompt: base prompt + skill descriptions (loaded on
    /// demand via skill_load) + the always-loaded memory index + upward-cascading
    /// convention files (read-only).
    /// </summary>
    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder(_resolved.SystemPrompt);

        var skills = _skills.List();
        if (skills.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine("# Available skills (use skill_load to read one)");
            foreach (var s in skills)
                sb.Append("- ").Append(s.Name).Append(": ").AppendLine(s.Description);
        }

        foreach (var dir in _memoryDirs)
        {
            var index = new MemoryStore(dir).ReadIndex();
            if (!string.IsNullOrWhiteSpace(index))
                sb.AppendLine().AppendLine().AppendLine("# Memory index").AppendLine(index.Trim());
        }

        var conventions = ConventionLoader.LoadUpward(WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(conventions))
            sb.AppendLine().AppendLine().AppendLine("# Project conventions (read-only inherited context)")
              .AppendLine(conventions);

        return sb.ToString();
    }

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

    /// <summary>Append this message's estimated energy (Wh since it started) to the stats line.</summary>
    private string? AppendEnergy(string? stats)
    {
        if (!HasCost) return stats;
        var wh = _energy.Wh - _messageStartWh;
        if (wh <= 0) return stats;
        var energy = $"~{wh:0.0} Wh";
        return string.IsNullOrEmpty(stats) ? energy : $"{stats} · {energy}";
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
