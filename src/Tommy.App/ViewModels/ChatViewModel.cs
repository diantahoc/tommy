using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Agent;
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
    // Mutable per-chat enabled-tools set (null = all registered tools). Toggled
    // live from the right sidebar and persisted to the chat record.
    private HashSet<string>? _enabledTools;
    private readonly EnergyMeter _energy = new();
    private readonly double _electricityRate;
    private readonly string _currency;
    private double _messageStartWh;
    private ActivityViewModel? _activity;
    private CancellationTokenSource? _sendCts;

    private readonly IMcpConnector? _mcpConnector;

    public ChatViewModel(
        EngineService engine, ToolRegistry registry, ChatRepository repo,
        ChatHandle handle, string workingDirectory, ResolvedParameters resolved,
        ISkillProvider skills, IReadOnlyList<string> memoryDirs, string? writeMemoryDir,
        double electricityRate = 0.0, string currency = "$", IMcpConnector? mcpConnector = null)
    {
        _mcpConnector = mcpConnector;
        _electricityRate = electricityRate;
        _currency = currency;
        _engine = engine;
        _registry = registry;
        _repo = repo;
        _handle = handle;
        _energy.Restore(handle.Record.EnergyKwh); // resume the running total across reopens/restarts
        _resolved = resolved;
        _enabledTools = resolved.EnabledTools is null ? null : new HashSet<string>(resolved.EnabledTools, StringComparer.Ordinal);
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
        RefreshContextBreakdown();
        RefreshTools();
        RefreshConnectedMcps();
        if (_mcpConnector is not null && _handle.Record.Parameters.Mcps.Count > 0)
            _ = ReconnectMcpsAsync();
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

    /// <summary>Headline for the context panel: estimated used over the window.</summary>
    public string ContextMaxLabel => _contextMax > 0 ? $"{_contextMax / 1000}k" : "–";
    public bool HasContextWindow => _contextMax > 0;

    /// <summary>Estimated breakdown of what fills the context window (≈ chars/4).</summary>
    public ObservableCollection<ContextSlice> ContextSlices { get; } = new();

    /// <summary>Recompute the context breakdown from the current prompt, tools and transcript.</summary>
    public void RefreshContextBreakdown()
    {
        ContextSlices.Clear();
        if (_contextMax <= 0) return;

        static int Est(string? s) => string.IsNullOrEmpty(s) ? 0 : s.Length / 4;

        var sys = Est(_resolved.SystemPrompt);

        var skillsTokens = 0;
        foreach (var s in _skills.List())
            skillsTokens += Est(s.Name) + Est(s.Description) + 1;

        var memTokens = 0;
        foreach (var dir in _memoryDirs)
            memTokens += Est(new MemoryStore(dir).ReadIndex());

        var convTokens = Messages.OfType<ChatMessageViewModel>()
            .Where(m => m.IncludeInHistory)
            .Sum(m => Est(m.RawText));

        var toolsTokens = _registry.BuildToolsArray(_enabledTools).ToJsonString().Length / 4;

        void Add(string label, int tokens, string color)
        {
            if (tokens <= 0) return;
            ContextSlices.Add(new ContextSlice(label, tokens, 100.0 * tokens / _contextMax, color));
        }

        Add("Tools", toolsTokens, "#2563EB");
        Add("Conversation", convTokens, "#16A34A");
        Add("System prompt", sys, "#F59E0B");
        Add("Skills", skillsTokens, "#9333EA");
        Add("Memory", memTokens, "#DC2626");

        // The engine's reported total for the last turn (ContextUsed) is the real figure:
        // it includes tool-call arguments, tool results and reasoning that the per-category
        // estimate can't see. Reconcile so the pie matches the header percentage — show the
        // known categories, then an "Other" slice for the remainder. Before any run has
        // reported a total, seed the header from the estimate so it isn't stuck at 0.
        var estimated = toolsTokens + convTokens + sys + skillsTokens + memTokens;
        if (ContextUsed <= 0) ContextUsed = estimated;

        var used = Math.Max(ContextUsed, estimated);
        Add("Other (tool output, reasoning)", used - estimated, "#94A3B8");

        var free = Math.Max(0, _contextMax - used);
        ContextSlices.Add(new ContextSlice("Free space", free, 100.0 * free / _contextMax, "#CBD5E1"));
    }

    // --- Enabled tools (per-chat), grouped for the sidebar ------------------

    /// <summary>Registered tools, grouped for display; each tool toggles per chat.</summary>
    public ObservableCollection<ToolGroupViewModel> ToolGroups { get; } = new();

    /// <summary>
    /// Rebuild the grouped tool list from the registry (picks up MCP tools
    /// connected after the chat opened) and the current enabled set. Call when
    /// the sidebar opens.
    /// </summary>
    public void RefreshTools()
    {
        ToolGroups.Clear();

        // Bucket visible tools by group (hidden tools, e.g. skills, map to null).
        var byGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tool in _registry.Tools)
        {
            var group = ToolGroupMap.GroupOf(tool.Name);
            if (group is null) continue;
            (byGroup.TryGetValue(group, out var list) ? list : byGroup[group] = new()).Add(tool.Name);
        }

        foreach (var group in ToolGroupMap.Order)
        {
            if (!byGroup.TryGetValue(group, out var names)) continue;
            var rows = names.OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => new ToolToggleViewModel(n, IsToolEnabled(n), SetToolEnabled));
            ToolGroups.Add(new ToolGroupViewModel(group, rows, SetToolsEnabled));
        }
    }

    private bool IsToolEnabled(string name) => _enabledTools is null || _enabledTools.Contains(name);

    private void SetToolEnabled(string name, bool on) => ApplyToolChange(new[] { name }, on);

    private void SetToolsEnabled(IEnumerable<string> names, bool on)
    {
        ApplyToolChange(names, on);
        // Reflect the batch on the individual switches without re-persisting.
        var set = names.ToHashSet(StringComparer.Ordinal);
        foreach (var g in ToolGroups)
            foreach (var t in g.Tools)
                if (set.Contains(t.Name)) t.SetQuietly(on);
    }

    private void ApplyToolChange(IEnumerable<string> names, bool on)
    {
        var all = _registry.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        // "All enabled" is stored as null; materialize the explicit set the first
        // time the user turns something off.
        _enabledTools ??= new HashSet<string>(all, StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (on) _enabledTools.Add(name);
            else _enabledTools.Remove(name);
        }

        // Collapse back to null when everything is on again.
        if (all.SetEquals(_enabledTools)) _enabledTools = null;

        _handle.Record.Parameters.EnabledTools = _enabledTools;
        _repo.SaveMeta(_handle.Record);
        RefreshContextBreakdown();
    }

    // --- Connected MCP servers (per-chat) -----------------------------------

    /// <summary>MCP servers connected for this chat, shown in the sidebar.</summary>
    public ObservableCollection<ConnectedMcpViewModel> ConnectedMcps { get; } = new();

    private void RefreshConnectedMcps()
    {
        ConnectedMcps.Clear();
        foreach (var r in _handle.Record.Parameters.Mcps)
            ConnectedMcps.Add(new ConnectedMcpViewModel(r, RemoveConnectedMcpAsync));
    }

    /// <summary>Reconnect every saved MCP when the chat opens and re-enable its tools.</summary>
    private async Task ReconnectMcpsAsync()
    {
        foreach (var row in ConnectedMcps.ToList())
            await ConnectRowAsync(row);
    }

    /// <summary>Persist a new MCP ref, connect it, and enable its tools for this chat.</summary>
    public async Task AddConnectedMcpAsync(ChatMcpRef reference)
    {
        if (_mcpConnector is null) return;
        if (_handle.Record.Parameters.Mcps.Any(m => m.Id == reference.Id)) return;

        _handle.Record.Parameters.Mcps.Add(reference);
        _repo.SaveMeta(_handle.Record);

        var row = new ConnectedMcpViewModel(reference, RemoveConnectedMcpAsync);
        ConnectedMcps.Add(row);
        await ConnectRowAsync(row);
    }

    private async Task ConnectRowAsync(ConnectedMcpViewModel row)
    {
        if (_mcpConnector is null) return;
        try
        {
            var result = await _mcpConnector.ConnectChatMcpAsync(row.Reference);
            if (result.Ok)
            {
                ApplyToolChange(result.ToolNames, on: true); // enable the server's tools for this chat
                RefreshTools();
                row.Status = $"connected · {result.ToolNames.Count} tool(s)";
            }
            else
            {
                row.Status = result.Error ?? "failed to connect";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"mcp connect failed ({row.Id}): {ex.Message}");
            row.Status = "failed to connect";
        }
    }

    private async Task RemoveConnectedMcpAsync(ConnectedMcpViewModel row)
    {
        if (_mcpConnector is not null)
            await _mcpConnector.DisconnectChatMcpAsync(row.Id);

        _handle.Record.Parameters.Mcps.RemoveAll(m => m.Id == row.Id);
        _repo.SaveMeta(_handle.Record);
        ConnectedMcps.Remove(row);
        RefreshTools();
        RefreshContextBreakdown();
    }

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

    /// <summary>Raised when the model asks to open a file in the file pane (present_file).</summary>
    public event Action<PresentedFile>? FilePresented;

    private void HandlePresentFile(PresentedFile file)
    {
        if (Dispatcher.UIThread.CheckAccess()) FilePresented?.Invoke(file);
        else Dispatcher.UIThread.Post(() => FilePresented?.Invoke(file));
    }

    public ObservableCollection<object> Messages { get; } = new();

    public AutonomyLevel[] AutonomyOptions { get; } = Enum.GetValues<AutonomyLevel>();

    [ObservableProperty]
    private AutonomyLevel _autonomy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isSending;

    /// <summary>
    /// A small left-aligned hint under the last message telling the user what the
    /// chat is waiting on (queued for the engine, thinking, or running a tool), so
    /// a busy chat never looks idle. Empty when nothing is pending.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityHint))]
    private string _activityHint = string.Empty;

    public bool HasActivityHint => ActivityHint.Length > 0;

    [ObservableProperty]
    private ApprovalViewModel? _pendingApproval;

    [ObservableProperty]
    private QuestionViewModel? _pendingQuestion;

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
        RefreshResumeState(); // reopened on a Budget pause → the next send resumes the work
        SetCurrentTask(TaskStateProjection.Latest(_handle.Events.Read().ToList())); // restore the task pane
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

    /// <summary>Stop the in-flight run — cancels the current tool (cooperatively) and the loop.</summary>
    [RelayCommand(CanExecute = nameof(IsSending))]
    private void Stop()
    {
        ActivityHint = "Stopping…";
        try { _sendCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // When a run pauses mid-work (a Budget pause) the next send resumes from the paused
    // run's working set — its tool calls, results, and partial answer — reconstructed
    // from the event log, so the model keeps its findings instead of rebuilding from
    // prose-only history and redoing work. Rebuilt after every run and on load, so it
    // survives an app restart. Null when there's nothing to continue.
    private List<ChatMessageDto>? _resumeState;

    // The agent records its task state (objective + checklist + findings) via the
    // update_task_state tool, which runs the RecordTaskState sink below on the loop's
    // thread. We stash the snapshot here and flush it to the event log on the UI thread
    // when the tool finishes, so every log write stays on one thread. The log is the
    // source of truth — BuildSystemPrompt folds the latest snapshot back into context.
    private TaskState? _pendingTaskState;

    /// <summary>
    /// The agent's current task (objective + checklist + findings) for the task pane,
    /// or null when there is no active task. Folded from the event log on load and
    /// refreshed whenever the agent updates it, so it survives a pause or restart.
    /// </summary>
    [ObservableProperty]
    private TaskStateViewModel? _currentTask;

    private void SetCurrentTask(TaskState? state) =>
        CurrentTask = state is null || state.IsEmpty ? null : new TaskStateViewModel(state);

    /// <summary>
    /// Recompute the pending resume state from the event log: set when the most recent
    /// run ended in a Budget pause, cleared otherwise. Called after each run and on load.
    /// </summary>
    private void RefreshResumeState()
    {
        var (history, resumable) = ResumeHistoryBuilder.Build(_handle.Events.Read().ToList());
        _resumeState = resumable ? history : null;
    }

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
        // If the model is already busy with another chat, we're waiting in the
        // single-model queue; otherwise we're about to generate.
        ActivityHint = _engine.Queue.IsBusy ? "Queued…" : ThinkingWords.Next();
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

        // A pending resume state (from a mid-work pause) is consumed exactly once: this
        // turn seeds history from the paused run's working set; later turns go back to
        // the prose-only history.
        var resume = _resumeState;
        _resumeState = null;

        var run = new AgentRun
        {
            Model = _engine.ModelId,
            SystemPrompt = BuildSystemPrompt(),
            History = resume is not null ? ResumeHistory(resume, text) : BuildHistory(),
            ToolContext = new ToolContext(
                WorkingDirectory,
                MemoryDirs: _memoryDirs.Count > 0 ? _memoryDirs : null,
                WriteMemoryDir: _writeMemoryDir,
                VisionEnabled: _engine.Capabilities?.SupportsVision == true,
                AskUser: HandleQuestionAsync,
                PresentFile: HandlePresentFile,
                RecordTaskState: s => _pendingTaskState = s),
            Autonomy = Autonomy,
            ThinkingEffort = ThinkingEffort,
            EnabledTools = _enabledTools,
            Approve = HandleApprovalAsync
        };

        _activity = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        try
        {
            await foreach (var ev in _loop.RunAsync(run, _sendCts.Token))
            {
                switch (ev)
                {
                    case AssistantStep step:
                        if (!string.IsNullOrEmpty(step.Reasoning)) EnsureActivity().AddThinking(step.Reasoning);
                        if (!string.IsNullOrEmpty(step.Content))
                        {
                            // Prose content: close the activity, render as a normal bubble.
                            CloseActivity();
                            var answer = new ChatMessageViewModel(ChatRole.Assistant);
                            answer.AppendContent(step.Content);
                            answer.Stats = AppendEnergy(FormatStats(step.Stats));
                            if (step.Stats?.TotalTokens is { } used) ContextUsed = used;
                            Messages.Add(answer);
                        }
                        else if (!string.IsNullOrEmpty(step.Reasoning))
                        {
                            ActivityHint = ThinkingWords.Next(); // reasoning-only step, before tool calls
                        }
                        // Record the turn — including its tool calls — so a run that pauses
                        // mid-work can be reconstructed from the log for a continuation.
                        if (!string.IsNullOrEmpty(step.Content) || !string.IsNullOrEmpty(step.Reasoning) || step.ToolCalls.Count > 0)
                            _handle.Events.Append(new AssistantEvent
                            {
                                Content = step.Content,
                                Reasoning = step.Reasoning,
                                ToolCalls = step.ToolCalls
                                    .Select(c => new ToolCallRecord { Id = c.Id, Name = c.Name, ArgumentsJson = c.ArgumentsJson })
                                    .ToList()
                            });
                        break;

                    case AutoContinued auto:
                        EnsureActivity().AddThinking($"Answer hit the output limit — continuing automatically (#{auto.Attempt}).");
                        ActivityHint = "Continuing…";
                        break;

                    case ContextCompacted compacted:
                        EnsureActivity().AddThinking(compacted.Note is { Length: > 0 } how
                            ? $"Context window was full — {how} to keep going."
                            : "Context window was full — compacted earlier steps to keep going.");
                        ActivityHint = "Compacting context…";
                        break;

                    case ToolStarted started:
                        var toolCall = new ToolCallViewModel(
                            started.CallId, started.ToolName, started.ArgumentsJson,
                            started.Tier, started.Preview?.Diff);
                        EnsureActivity().AddTool(toolCall);
                        ActivityHint = toolCall.IntentLabel; // the model's goal for this tool
                        break;

                    case ToolFinished finished:
                        ActivityHint = ThinkingWords.Next(); // back to the model for the next step
                        FindToolCall(finished.CallId)?.Complete(finished.Result);
                        _handle.Events.Append(new ToolResultEvent
                        {
                            CallId = finished.CallId,
                            Tool = finished.ToolName,
                            Success = finished.Result.Success,
                            Result = finished.Result.Content
                        });
                        // Flush a task-state snapshot the tool just recorded (same thread as
                        // every other log write), so the plan survives a pause or restart.
                        if (_pendingTaskState is { } task)
                        {
                            _handle.Events.Append(new TaskStateEvent { State = task });
                            SetCurrentTask(task);
                            _pendingTaskState = null;
                        }
                        break;

                    case RunFinished done:
                        CloseActivity();
                        _handle.Events.Append(new MetaEvent { Kind = done.Reason.ToString(), Detail = done.Note });
                        if (done.Reason == RunEndReason.Stopped)
                        {
                            // A denial halts the run. With a reason, keep it in history so the
                            // next turn knows why; without one, just show a quiet marker.
                            if (string.IsNullOrWhiteSpace(done.Note))
                                AddBanner("_Stopped — awaiting your instruction._");
                            else
                                Messages.Add(new ChatMessageViewModel(ChatRole.Assistant,
                                    $"Stopped at your request. Your note: {done.Note}", includeInHistory: true));
                        }
                        else if (done.Reason == RunEndReason.Error)
                        {
                            AddBanner($"_Error: {done.Note ?? "the run failed."}_");
                        }
                        else if (done.Reason != RunEndReason.Answered && done.Note is not null)
                        {
                            AddBanner($"_Run paused: {done.Note}._");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"chat {ChatId}: run stopped by user");
            CloseActivity();
            AddBanner("_Stopped._");
            _handle.Events.Append(new MetaEvent { Kind = "stopped", Detail = "user" });
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
            PendingQuestion = null;
            IsSending = false;
            ActivityHint = string.Empty;
            _sendCts?.Dispose();
            _sendCts = null;
            _handle.Record.EnergyKwh = _energy.Kwh; // persist the running total with the chat
            _repo.SaveMeta(_handle.Record);
            RefreshContextBreakdown();
            RefreshResumeState(); // a Budget pause this run leaves work the next send resumes
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

    private Task<ApprovalDecision> HandleApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>();
        // Stopping the run while an approval is pending resolves it as "not approved"
        // and dismisses the prompt, so the loop unblocks instead of hanging on it.
        ct.Register(() =>
        {
            if (Dispatcher.UIThread.CheckAccess()) PendingApproval = null;
            else Dispatcher.UIThread.Post(() => PendingApproval = null);
            tcs.TrySetResult(new ApprovalDecision(false));
        });
        void Show() => PendingApproval = new ApprovalViewModel(request, decision =>
        {
            PendingApproval = null;
            tcs.TrySetResult(decision);
        });
        if (Dispatcher.UIThread.CheckAccess()) Show();
        else Dispatcher.UIThread.Post(Show);
        return tcs.Task;
    }

    private Task<string> HandleQuestionAsync(UserQuestion question, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>();
        // A stop/cancel dismisses the question and returns an empty answer so the
        // tool call unblocks (the tool reports it as dismissed).
        ct.Register(() =>
        {
            if (Dispatcher.UIThread.CheckAccess()) PendingQuestion = null;
            else Dispatcher.UIThread.Post(() => PendingQuestion = null);
            tcs.TrySetResult(string.Empty);
        });
        void Show() => PendingQuestion = new QuestionViewModel(question, answer =>
        {
            PendingQuestion = null;
            tcs.TrySetResult(answer ?? string.Empty);
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

        // The agent's own plan, folded from the event log so it persists across a pause
        // or an app restart. This is how it remembers what it was doing.
        if (TaskStateProjection.Latest(_handle.Events.Read().ToList()) is { } task)
            sb.AppendLine().AppendLine()
              .AppendLine("# Current task (your plan — continue it, and keep it current with update_task_state)")
              .AppendLine(task.Render());

        return sb.ToString();
    }

    /// <summary>
    /// History for a continuation after a mid-work pause: the paused run's working set
    /// (prose history + its tool calls, tool results, and partial answer) plus the new
    /// user message. Unlike <see cref="BuildHistory"/> this keeps the tool scaffold, so
    /// the model resumes with its findings in hand instead of redoing the tool calls.
    /// </summary>
    private static List<ChatMessageDto> ResumeHistory(List<ChatMessageDto> resume, string userText) =>
        new(resume) { new ChatMessageDto { Role = "user", Content = userText } };

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
