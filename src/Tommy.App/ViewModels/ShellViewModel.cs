using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Chat;
using Tommy.Core.Projects;
using Tommy.Core.Configuration;
using Tommy.Core.Diagnostics;
using Tommy.Core.Engine;
using Tommy.Core.Export;
using Tommy.Core.Mcp;
using Tommy.Core.Research;
using Tommy.Core.Runtime;
using Tommy.Core.Skills;
using Tommy.Core.Tools;
using Tommy.Engine;
using Tommy.Engine.Provisioning;
using Tommy.Engine.Research;
using Tommy.Engine.Research.Browser;
using Tommy.Engine.Research.Searxng;
using Tommy.Mcp;
using Tommy.Mcp.Integrations;
using Tommy.Persistence;
using Tommy.Persistence.Storage;
using Tommy.Tools;

namespace Tommy.App.ViewModels;

/// <summary>An entry in the sidebar chat list (or a group header when IsHeader).</summary>
public sealed record ChatListItem(string Id, string Title, ChatKind Kind, string? Subtitle, bool IsHeader = false)
{
    public static ChatListItem Header(string title) => new(string.Empty, title, ChatKind.Chat, null, IsHeader: true);
}

/// <summary>How the chat list is ordered.</summary>
public enum ChatSortMode { LastUpdated, Name, Created }

/// <summary>How the chat list is grouped.</summary>
public enum ChatGroupMode { None, Project }

/// <summary>
/// The app shell: owns the engine, the repositories, the chat-list sidebar
/// (Chats/Code sections + search), and the active chat.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable, IMcpConnector
{
    private readonly McpIntegrationRegistry _integrations = new();
    private readonly EngineService _engine;
    private readonly TommyConfig _config;
    private readonly IConfigStore _store;
    private readonly GlobalDefaults _globals;
    private readonly ChatRepository _chats = new();
    // Live chat view-models kept alive across switches, so an in-flight run (its
    // activity, pending approval/question and Stop button) survives leaving the chat
    // and is still there when the user comes back.
    private readonly Dictionary<string, ChatViewModel> _openChats = new();
    private readonly WorkspaceRepository _workspaces = new();
    private readonly ProjectRepository _projects = new();
    private readonly ChatSearch _search;
    private readonly ToolRegistry _registry = new();
    private readonly SkillRepository _skills;
    private readonly McpManager _mcp;
    private readonly WebResearchOrchestrator _research;
    private readonly SearxngInstaller _searxngInstaller;
    private readonly ToolProvisioner _provisioner = new();
    private readonly PlaywrightBrowser _playwrightBrowser = new();
    private readonly BrowserSessionManager _browserSessions = new();
    private ManagedSearxngProvider? _managedProvider;
    private readonly string _sandbox;
    private DispatcherTimer? _telemetryTimer;

    public TelemetryViewModel Telemetry { get; } = new();

    public ShellViewModel(EngineService engine, TommyConfig config, IConfigStore store)
    {
        _engine = engine;
        _config = config;
        _store = store;
        _globals = config.Defaults;

        // Native + memory tools by discovery; skill tools need the provider, so register them.
        _registry.DiscoverFrom(typeof(ReadFileTool).Assembly);
        StarterSkills.SeedIfEmpty(TommyPaths.SkillsDir);
        _skills = new SkillRepository();
        _registry.Register(new SkillSearchTool(_skills));
        _registry.Register(new SkillLoadTool(_skills));

        // MCP tools register through the same registry; predefined/user servers
        // connect via the manager (Phase 4B wires the roster + credentials).
        _mcp = new McpManager(_registry);

        // Web research: fetch + goal-directed summarize (isolated, no tools),
        // returning tagged data. Search is SearXNG (managed/custom); JS rendering
        // (web_fetch_js) is our own Node+Playwright program. Both degrade with a
        // clear message until the runtime provisioner has installed them.
        var fetcher = new HttpWebFetcher();
        var summarizer = new LlmSummarizer(_engine, () => _engine.ModelId);
        _searxngInstaller = new SearxngInstaller(TommyPaths.SearxngDir);
        // Search backend precedence: a user-set SearXNG URL wins; else the managed SearXNG when
        // installed; else nothing (web_research degrades). JS rendering (web_fetch_js) uses
        // PlaywrightBrowser (Microsoft.Playwright + bundled Chromium), unavailable until provisioned.
        _research = new WebResearchOrchestrator(fetcher, summarizer, ResolveSearchProvider());
        _registry.Register(new WebFetchTool(fetcher, summarizer));
        _registry.Register(new WebResearchTool(_research));
        _registry.Register(new BrowserReadTool(_playwrightBrowser, summarizer));
        // Interactive browser (persistent, per-chat; actions always ask). Sessions resolve per chat
        // via ToolContext.OpenBrowser, keyed by the chat's isolated profile dir.
        _registry.Register(new BrowserNavigateTool());
        _registry.Register(new BrowserPageReadTool());
        _registry.Register(new BrowserClickTool());
        _registry.Register(new BrowserTypeTool());
        _registry.Register(new BrowserScreenshotTool());
        _searxngStatus = _searxngInstaller.IsInstalled ? "Installed." : "Not installed.";
        _toolsStatus = _provisioner.IsFullyProvisioned() ? "Ready." : "Not installed.";
        _ = BootstrapToolsAsync(); // provision runtimes/scripts, then SearXNG, on startup

        _search = new ChatSearch(_chats);

        _sandbox = Path.Combine(TommyPaths.Root, "workspace");
        Directory.CreateDirectory(_sandbox);

        _engine.StateChanged += (_, s) => Dispatcher.UIThread.Post(() => ApplyState(s));
        _engine.Queue.StateChanged += (_, _) => Dispatcher.UIThread.Post(() => QueuedCount = _engine.Queue.Depth);
    }

    public ObservableCollection<ChatListItem> ChatList { get; } = new();

    [ObservableProperty]
    private ChatViewModel? _activeChat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatsSection))]
    [NotifyPropertyChangedFor(nameof(IsCodeSection))]
    private ChatKind _section = ChatKind.Chat;

    public bool IsChatsSection => Section == ChatKind.Chat;
    public bool IsCodeSection => Section == ChatKind.Code;

    [ObservableProperty]
    private string _searchText = string.Empty;

    // --- Sidebar collapse ---------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidthPx))]
    [NotifyPropertyChangedFor(nameof(SidebarExpanded))]
    private bool _sidebarCollapsed;

    public bool SidebarExpanded => !SidebarCollapsed;
    // Collapsed = fully hidden (the panel body, search and list all key off
    // SidebarExpanded; the toggle lives in the title bar). 0, not a narrow rail,
    // so no empty sliver is left behind.
    public double SidebarWidthPx => SidebarCollapsed ? 0 : 250;

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    // --- Right context sidebar ---------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightSidebarWidthPx))]
    private bool _rightSidebarOpen;

    public double RightSidebarWidthPx => RightSidebarOpen ? 340 : 0;

    [RelayCommand]
    private void ToggleRightSidebar() => RightSidebarOpen = !RightSidebarOpen;

    partial void OnRightSidebarOpenChanged(bool value)
    {
        if (!value) return;
        ActiveChat?.RefreshContextBreakdown();
        ActiveChat?.RefreshTools();
    }

    // --- Sort & group -------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortName))]
    [NotifyPropertyChangedFor(nameof(IsSortUpdated))]
    [NotifyPropertyChangedFor(nameof(IsSortCreated))]
    private ChatSortMode _sortMode = ChatSortMode.LastUpdated;

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupNone))]
    [NotifyPropertyChangedFor(nameof(IsGroupProject))]
    private ChatGroupMode _groupMode = ChatGroupMode.None;

    public bool IsSortName => SortMode == ChatSortMode.Name;
    public bool IsSortUpdated => SortMode == ChatSortMode.LastUpdated;
    public bool IsSortCreated => SortMode == ChatSortMode.Created;
    public bool IsGroupNone => GroupMode == ChatGroupMode.None;
    public bool IsGroupProject => GroupMode == ChatGroupMode.Project;

    partial void OnSortModeChanged(ChatSortMode value) => RefreshList();
    partial void OnSortDescendingChanged(bool value) => RefreshList();
    partial void OnGroupModeChanged(ChatGroupMode value) => RefreshList();

    /// <summary>When on, the chat list shows archived chats (for review / restore)
    /// instead of the active ones.</summary>
    [ObservableProperty]
    private bool _showArchived;

    partial void OnShowArchivedChanged(bool value) => RefreshList();

    [RelayCommand] private void SortByName() => SortMode = ChatSortMode.Name;
    [RelayCommand] private void SortByUpdated() => SortMode = ChatSortMode.LastUpdated;
    [RelayCommand] private void SortByCreated() => SortMode = ChatSortMode.Created;
    [RelayCommand] private void GroupByNone() => GroupMode = ChatGroupMode.None;
    [RelayCommand] private void GroupByProject() => GroupMode = ChatGroupMode.Project;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private string _engineStatus = "connecting…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private int _queuedCount;

    [ObservableProperty]
    private bool _isBusy;

    public string StatusLine =>
        QueuedCount > 1 ? $"{EngineStatus} · {QueuedCount - 1} queued" : EngineStatus;

    public bool CanManageEngine => _engine.CanManage;

    partial void OnSectionChanged(ChatKind value) => RefreshList();
    partial void OnSearchTextChanged(string value) => RefreshList();

    public async Task InitializeAsync()
    {
        Log.Info($"shell init: connecting to {_engine.Profile.BaseUrl}");
        EngineHealth health;
        try { health = await _engine.ConnectAsync(); }
        catch (Exception ex) { EngineStatus = "unreachable"; health = EngineHealth.Down(ex.Message); Log.Error("connect failed", ex); }

        Log.Info($"shell init: health={health.Status} model={health.ModelId}");
        ApplyState(_engine.State);
        RefreshList();

        if (ActiveChat is null)
        {
            var existing = _chats.List().FirstOrDefault(r => r.Kind == Section) ?? _chats.List().FirstOrDefault();
            if (existing is not null) SelectChat(existing.Id);
            else NewChat();
        }

        if (health.Status == HealthStatus.Ready)
        {
            var caps = _engine.Capabilities;
            ActiveChat?.AddBanner($"Connected to **{_engine.ModelId}** — {caps?.MaxContextTokens:N0} token context, " +
                                  $"vision {(caps?.SupportsVision == true ? "on" : "off")}. {_registry.Tools.Count} tools.");
        }
        else
        {
            ActiveChat?.AddBanner($"Engine not ready ({health.Status}). {health.Detail}");
        }

        StartTelemetry();
        _ = ConnectSavedMcpServersAsync();
    }

    private async Task ConnectSavedMcpServersAsync()
    {
        foreach (var entry in _config.McpServers.ToList())
        {
            try { await _mcp.ConnectAsync(entry.ToConfig()); }
            catch (Exception ex) { Log.Error($"mcp reconnect failed: {entry.Id}", ex); }
        }
    }

    private void StartTelemetry()
    {
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _telemetryTimer.Tick += async (_, _) => await PollTelemetryAsync();
        _telemetryTimer.Start();
    }

    private async Task PollTelemetryAsync()
    {
        try
        {
            var t = await _engine.GetTelemetryAsync();
            Telemetry.Update(t, _engine.Capabilities?.SupportsVision == true);

            // Integrate GPU power into the active chat's cost only while it generates.
            if (ActiveChat is { } chat)
            {
                if (chat.IsSending) chat.SampleEnergy(t.Hardware.GpuPowerWatts, t.SampledAt);
                else chat.PauseEnergy();
            }
        }
        catch
        {
            Telemetry.Clear();
            ActiveChat?.PauseEnergy();
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        var record = new ChatRecord { Id = NewId(), Kind = ChatKind.Chat };
        var handle = _chats.Create(record);
        Section = ChatKind.Chat;
        OpenChat(handle, ResolveWorkingDir(handle)); // its own per-chat scratch workspace
        RefreshList();
    }

    /// <summary>Create a code chat bound to a selected folder (called after a folder pick).</summary>
    public void CreateCodeChat(string folderPath)
    {
        var ws = _workspaces.GetOrCreate(folderPath);
        var record = new ChatRecord { Id = NewId(), Kind = ChatKind.Code, WorkspaceId = ws.Id };
        var handle = _chats.Create(record);
        _workspaces.AddChat(ws.Id, record.Id);
        Section = ChatKind.Code;
        OpenChat(handle, ws.FolderPath);
        RefreshList();
    }

    public void SelectChat(string id)
    {
        if (ActiveChat?.ChatId == id) return;
        // Reuse the live view-model if this chat is already open, so a run in progress
        // (and its approval/question prompt and Stop button) is preserved.
        if (_openChats.TryGetValue(id, out var cached))
        {
            ActiveChat = cached;
            cached.RefreshContextBreakdown();
            cached.RefreshTools();
            return;
        }
        var handle = _chats.Open(id);
        if (handle is null) return;
        OpenChat(handle, ResolveWorkingDir(handle));
    }

    [RelayCommand]
    private async Task StartEngineAsync()
    {
        EngineStatus = "starting…";
        await _engine.StartAsync();
    }

    [RelayCommand]
    private Task StopEngineAsync() => _engine.StopAsync();

    [RelayCommand]
    private void SetSectionChats() => Section = ChatKind.Chat;

    [RelayCommand]
    private void SetSectionCode() => Section = ChatKind.Code;

    private string ResolveWorkingDir(ChatHandle handle)
    {
        if (handle.Record.Kind == ChatKind.Code && handle.Record.WorkspaceId is { } wsId)
            return _workspaces.Load(wsId)?.FolderPath ?? _sandbox;
        // A plain chat has no project, so it gets its own scratch workspace under its
        // chat folder — isolated from other chats, and removed when the chat is deleted.
        return handle.Record.ReadOnlyFolder ?? ChatWorkspace(handle.Record.Id);
    }

    /// <summary>Per-chat scratch workspace (<c>~/.tommy/chats/&lt;id&gt;/workspace</c>),
    /// created on demand so one chat's scratch files don't bleed into another's.</summary>
    private static string ChatWorkspace(string chatId)
    {
        var dir = Path.Combine(TommyPaths.ChatsDir, chatId, "workspace");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void OpenChat(ChatHandle handle, string workingDir)
    {
        var projectParams = handle.Record.ProjectId is { } pid ? _projects.Load(pid)?.Defaults : null;
        var resolved = ParameterResolver.Resolve(handle.Record.Parameters, projectParams, _globals);

        // Memory: workspace (code chats) and/or project; standalone chats get none.
        var memoryDirs = new List<string>();
        string? writeDir = null;
        if (handle.Record.Kind == ChatKind.Code && handle.Record.WorkspaceId is { } wsId)
        {
            var wsMem = _workspaces.MemoryDir(wsId);
            Directory.CreateDirectory(wsMem);
            memoryDirs.Add(wsMem);
            writeDir = wsMem;
        }
        if (handle.Record.ProjectId is { } projectId)
        {
            var projMem = _projects.MemoryDir(projectId);
            Directory.CreateDirectory(projMem);
            memoryDirs.Add(projMem);
            writeDir ??= projMem;
        }

        // Each chat gets its own isolated, persistent browser profile (never shared, never the
        // user's real Chrome). Lives under the chat's data dir so it doesn't touch the repo folder.
        var browserProfile = Path.Combine(TommyPaths.ChatsDir, handle.Record.Id, "browser");
        var chatId = handle.Record.Id;
        var vm = new ChatViewModel(_engine, _registry, _chats, handle, workingDir, resolved,
            _skills, memoryDirs, writeDir, _globals.ElectricityRate, _globals.Currency, mcpConnector: this,
            allowUnsandboxedOffice: () => _config.AllowUnsandboxedOffice,
            openBrowser: () =>
            {
                var session = _browserSessions.Get(browserProfile);
                OnBrowserSessionOpened(chatId, session); // surface the pane when the agent drives it
                return session;
            });
        vm.MetadataChanged += RefreshList;
        vm.FilePresented += OnFilePresented;
        _openChats[handle.Record.Id] = vm;
        ActiveChat = vm;
    }

    /// <summary>Drop a chat's cached view-model (on delete/archive or a settings re-open).</summary>
    private void EvictChat(string id)
    {
        if (_openChats.Remove(id, out var vm))
        {
            vm.MetadataChanged -= RefreshList;
            vm.FilePresented -= OnFilePresented;
            // Free the chat's browser session (process + profile handle).
            _ = _browserSessions.CloseAsync(Path.Combine(TommyPaths.ChatsDir, id, "browser"));
        }
    }

    // --- File pane (present_file) -------------------------------------------

    [ObservableProperty]
    private FilePaneViewModel? _filePane;

    private void OnFilePresented(Tommy.Core.Tools.PresentedFile file) => FilePane = new FilePaneViewModel(file);

    [RelayCommand]
    private void CloseFilePane() => FilePane = null;

    private double _filePaneWidth = 380;

    /// <summary>User-adjustable width (px) of the file preview pane; clamped and driven by its drag handle.</summary>
    public double FilePaneWidth
    {
        get => _filePaneWidth;
        set
        {
            var clamped = System.Math.Clamp(value, 260, 1000);
            if (System.Math.Abs(clamped - _filePaneWidth) < 0.5) return;
            _filePaneWidth = clamped;
            OnPropertyChanged();
        }
    }

    // --- Browser pane (live view of the active chat's browser) --------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowserColumnWidth))]
    private BrowserPaneViewModel? _browserPane;

    private double _browserPaneWidth = 440;

    /// <summary>User-adjustable width (px) of the browser pane; clamped and driven by the pane's drag handle.</summary>
    public double BrowserPaneWidth
    {
        get => _browserPaneWidth;
        set
        {
            var clamped = System.Math.Clamp(value, 320, 1200);
            if (System.Math.Abs(clamped - _browserPaneWidth) < 0.5) return;
            _browserPaneWidth = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BrowserColumnWidth));
        }
    }

    /// <summary>Grid width for the browser column: the chosen width when open, collapsed to 0 when closed.</summary>
    public Avalonia.Controls.GridLength BrowserColumnWidth =>
        BrowserPane is null ? new Avalonia.Controls.GridLength(0) : new Avalonia.Controls.GridLength(_browserPaneWidth);

    /// <summary>The chat whose browser the open pane is showing, so we never leave a stale view up.</summary>
    private string? _browserPaneChatId;

    [RelayCommand]
    private void ToggleBrowserPane()
    {
        if (ActiveChat is not { } chat) return;
        // Open for the active chat → close. Otherwise (nothing open, or a stale pane from
        // another chat) (re)bind to the active chat's own session.
        if (BrowserPane is not null && _browserPaneChatId == chat.ChatId) { CloseBrowserPane(); return; }
        var profile = Path.Combine(TommyPaths.ChatsDir, chat.ChatId, "browser");
        ShowBrowserPane(chat.ChatId, _browserSessions.Get(profile));
    }

    [RelayCommand]
    private void CloseBrowserPane()
    {
        BrowserPane?.Dispose();
        BrowserPane = null;
        _browserPaneChatId = null;
    }

    /// <summary>Bind the pane to a chat's browser session (replacing any pane from another chat).</summary>
    private void ShowBrowserPane(string chatId, IBrowserSession session)
    {
        if (BrowserPane is not null && _browserPaneChatId == chatId) return; // already showing it
        BrowserPane?.Dispose();
        BrowserPane = new BrowserPaneViewModel(session);
        _browserPaneChatId = chatId;
    }

    /// <summary>
    /// Called from a browser tool (on the tool thread) the first time a chat touches its browser,
    /// so the pane pops up to show what the agent is doing — but only for the chat on screen.
    /// </summary>
    private void OnBrowserSessionOpened(string chatId, IBrowserSession session) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ActiveChat?.ChatId == chatId) ShowBrowserPane(chatId, session);
        });

    /// <summary>When the user switches chats, drop a browser pane that belongs to the old chat.</summary>
    partial void OnActiveChatChanged(ChatViewModel? value)
    {
        if (BrowserPane is not null && _browserPaneChatId != value?.ChatId)
            CloseBrowserPane();
    }

    private void RefreshList()
    {
        ChatList.Clear();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var hit in _search.Search(SearchText, Section))
                ChatList.Add(new ChatListItem(hit.ChatId, hit.ChatTitle, hit.Kind, hit.Snippet));
            return;
        }

        var records = Sort(_chats.List().Where(r => r.Kind == Section && r.Archived == ShowArchived))
            .OrderByDescending(r => r.Pinned)
            .ToList();

        // A header so it's clear the list is showing the archive, not live chats.
        if (ShowArchived) ChatList.Add(ChatListItem.Header("Archived"));

        if (GroupMode == ChatGroupMode.Project)
        {
            foreach (var group in records.GroupBy(r => r.ProjectId))
            {
                var name = group.Key is { } pid ? _projects.Load(pid)?.Name ?? "Project" : "No project";
                ChatList.Add(ChatListItem.Header(name));
                foreach (var record in group)
                    ChatList.Add(Item(record));
            }
        }
        else
        {
            foreach (var record in records)
                ChatList.Add(Item(record));
        }

        ChatListItem Item(ChatRecord r) => new(r.Id, r.Title, r.Kind, SubtitleFor(r));
    }

    private IEnumerable<ChatRecord> Sort(IEnumerable<ChatRecord> records) => SortMode switch
    {
        ChatSortMode.Name => SortDescending
            ? records.OrderByDescending(r => r.Title, StringComparer.OrdinalIgnoreCase)
            : records.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase),
        ChatSortMode.Created => SortDescending
            ? records.OrderByDescending(r => r.Created)
            : records.OrderBy(r => r.Created),
        _ => SortDescending
            ? records.OrderByDescending(r => r.Updated)
            : records.OrderBy(r => r.Updated)
    };

    private string? SubtitleFor(ChatRecord record)
    {
        if (record.Kind == ChatKind.Code && record.WorkspaceId is { } wsId)
            return _workspaces.Load(wsId)?.FolderPath;
        return null;
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

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    // --- Chat context menu --------------------------------------------------

    /// <summary>A project choice in the "Assign to project" submenu.</summary>
    public sealed record ProjectMenuItem(string Id, string Name);

    public string? ContextChatId { get; private set; }
    public bool ContextTargetIsCode { get; private set; }
    public bool ContextTargetIsArchived { get; private set; }

    public ObservableCollection<ProjectMenuItem> ContextProjects { get; } = new();

    /// <summary>Capture the chat a context menu was opened on, and refresh the project submenu.</summary>
    public void SetContextTarget(string chatId)
    {
        ContextChatId = chatId;
        var record = _chats.LoadMeta(chatId);
        ContextTargetIsCode = record?.Kind == ChatKind.Code;
        ContextTargetIsArchived = record?.Archived ?? false;

        ContextProjects.Clear();
        foreach (var project in _projects.List())
            ContextProjects.Add(new ProjectMenuItem(project.Id, project.Name));
    }

    /// <summary>Create a new (empty) project and return it. Chats are added to it
    /// via "Assign to Project"; it also shows up under Group by project.</summary>
    public ProjectRecord CreateProject(string name)
    {
        var record = new ProjectRecord { Id = NewId(), Name = name.Trim() };
        _projects.Save(record);
        RefreshList();
        return record;
    }

    public bool IsContextChatPinned =>
        ContextChatId is { } id && (_chats.LoadMeta(id)?.Pinned ?? false);

    public void PinContextChat()
    {
        if (ContextChatId is not { } id || _chats.LoadMeta(id) is not { } r) return;
        r.Pinned = !r.Pinned;
        _chats.SaveMeta(r);
        RefreshList();
    }

    public void RenameContextChat(string newTitle)
    {
        if (ContextChatId is not { } id || string.IsNullOrWhiteSpace(newTitle) || _chats.LoadMeta(id) is not { } r) return;
        r.Title = newTitle.Trim();
        _chats.SaveMeta(r);
        RefreshList();
    }

    public void ForkContextChat()
    {
        if (ContextChatId is not { } id || _chats.Fork(id, NewId()) is not { } forked) return;
        if (forked.WorkspaceId is { } wsId) _workspaces.AddChat(wsId, forked.Id);
        RefreshList();
        SelectChat(forked.Id);
    }

    public void AssignContextChatToProject(string? projectId)
    {
        if (ContextChatId is not { } id || _chats.LoadMeta(id) is not { } r) return;
        r.ProjectId = projectId;
        _chats.SaveMeta(r);
        RefreshList();
    }

    public void ArchiveContextChat()
    {
        if (ContextChatId is not { } id || _chats.LoadMeta(id) is not { } r) return;
        r.Archived = true;
        _chats.SaveMeta(r);
        if (ActiveChat?.ChatId == id) ActiveChat = null;
        EvictChat(id);
        RefreshList();
    }

    public void UnarchiveContextChat()
    {
        if (ContextChatId is not { } id || _chats.LoadMeta(id) is not { } r) return;
        r.Archived = false;
        _chats.SaveMeta(r);
        RefreshList();
    }

    public void DeleteContextChat()
    {
        if (ContextChatId is not { } id) return;
        if (ActiveChat?.ChatId == id) ActiveChat = null;
        EvictChat(id);
        _chats.Delete(id);
        RefreshList();
    }

    /// <summary>The folder a code chat is bound to, for "Open in Explorer".</summary>
    public string? ContextChatFolder()
    {
        if (ContextChatId is not { } id || _chats.LoadMeta(id) is not { } r) return null;
        if (r.WorkspaceId is { } wsId) return _workspaces.Load(wsId)?.FolderPath;
        return r.ReadOnlyFolder;
    }

    /// <summary>
    /// Render the context-menu target chat's full transcript (turns, tool calls, and
    /// results) to Markdown. Returns the text and a suggested file name, or null if
    /// there is no target chat. The caller writes it wherever the user chooses.
    /// </summary>
    public (string Markdown, string FileName)? ExportContextChatMarkdown()
    {
        if (ContextChatId is not { } id || _chats.Open(id) is not { } handle) return null;
        var events = handle.Events.Read().ToList();
        var markdown = TranscriptMarkdown.Render(handle.Record, events, handle.Blobs.GetText);
        return (markdown, SlugFileName(handle.Record.Title) + ".md");
    }

    /// <summary>A safe-ish file name from a chat title (keeps letters/digits/space/dash/underscore).</summary>
    private static string SlugFileName(string title)
    {
        var cleaned = new string((title ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '-').ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "chat" : cleaned;
    }

    // --- Settings ----------------------------------------------------------

    public double ElectricityRate => _globals.ElectricityRate;
    public string Currency => _globals.Currency;

    /// <summary>Whether office_extract may run LibreOffice outside a sandbox (read live by each run).</summary>
    public bool AllowUnsandboxedOffice => _config.AllowUnsandboxedOffice;

    /// <summary>Persist the unsandboxed-office setting; the next tool run reads it live (no re-open needed).</summary>
    public void SaveAllowUnsandboxedOffice(bool value)
    {
        _config.AllowUnsandboxedOffice = value;
        _store.Save(_config);
    }

    /// <summary>Persist the cost-meter settings and apply them to the open chat.</summary>
    public void SaveGeneralSettings(double rate, string currency)
    {
        _globals.ElectricityRate = rate < 0 ? 0 : rate;
        _globals.Currency = string.IsNullOrWhiteSpace(currency) ? "$" : currency.Trim();
        _config.ElectricityRatePerKwh = _globals.ElectricityRate;
        _store.Save(_config);

        // Re-open the active chat so the new rate/currency take effect immediately.
        if (ActiveChat is { } chat)
        {
            var id = chat.ChatId;
            ActiveChat = null;
            EvictChat(id); // force a rebuild so the new rate/currency apply
            SelectChat(id);
        }
    }

    /// <summary>The endpoint the active engine profile points at (shown read-only).</summary>
    public string EngineBaseUrl => _engine.Profile.BaseUrl;

    /// <summary>The active profile's current auth token (for pre-filling the field).</summary>
    public string? EngineAuthToken => _engine.Profile.AuthToken;

    /// <summary>
    /// Persist a new endpoint auth token on the active profile and apply it to the
    /// running engine immediately, so the next request carries it without a restart.
    /// </summary>
    public void SaveEngineAuthToken(string? token)
    {
        var normalized = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

        if (_config.ActiveProfile is { } active)
        {
            var idx = _config.EngineProfiles.FindIndex(p => p.Name == active.Name);
            if (idx >= 0)
            {
                _config.EngineProfiles[idx] = active with { AuthToken = normalized };
                _store.Save(_config);
            }
        }

        _engine.SetAuthToken(normalized);
    }

    // --- Web search backend -------------------------------------------------

    /// <summary>A custom SearXNG endpoint that overrides the managed one; null/blank = use managed/fallback.</summary>
    public string? SearxngUrl => _config.WebSearch.SearxngUrl;

    /// <summary>Whether the managed SearXNG is auto-preferred when installed.</summary>
    public bool SearxngManagedEnabled => _config.WebSearch.ManagedEnabled;

    /// <summary>Whether a managed SearXNG is built and runnable.</summary>
    public bool SearxngInstalled => _searxngInstaller.IsInstalled;

    /// <summary>Human-readable install status for the Settings UI.</summary>
    [ObservableProperty] private string _searxngStatus = "Not installed.";

    /// <summary>True while an install/update is running (disables the button).</summary>
    [ObservableProperty] private bool _searxngInstalling;

    /// <summary>Health-check verdict for the Settings UI.</summary>
    [ObservableProperty] private string _searxngHealth = string.Empty;

    /// <summary>True while a health check runs (disables the Test button).</summary>
    [ObservableProperty] private bool _searxngTesting;

    /// <summary>
    /// Diagnose the SearXNG backend (a custom URL, or the managed server started fresh): probe the
    /// JSON API and report whether it actually works, with the server's output on failure.
    /// </summary>
    public async Task TestSearxngHealthAsync()
    {
        if (SearxngTesting) return;
        SearxngTesting = true;
        SearxngHealth = "Testing…";
        try
        {
            _managedProvider?.Stop(); // free the port so the check can start its own instance
            var cfg = _config.WebSearch;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var token = cts.Token;
            SearxngHealth = await Task.Run(() =>
                SearxngHealthCheck.RunAsync(_searxngInstaller, cfg.SearxngUrl, cfg.ManagedPort, token), token)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SearxngHealth = $"Health check failed: {ex.Message}";
        }
        finally
        {
            SearxngTesting = false;
        }
    }

    /// <summary>The pinned SearXNG source from the provisioning manifest, or null if not configured.</summary>
    private SearxngSpec? SearxngSource()
    {
        try { return File.Exists(_provisioner.ManifestPath) ? ToolManifest.Load(_provisioner.ManifestPath).Searxng : null; }
        catch (Exception ex) { Log.Warn($"searxng manifest read failed: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Persist the web-search settings and apply them live: web_research re-resolves its backend
    /// (custom URL → managed → none) with no restart.
    /// </summary>
    public void SaveWebSearchSettings(string? customUrl, bool managedEnabled)
    {
        _config.WebSearch.SearxngUrl = string.IsNullOrWhiteSpace(customUrl) ? null : customUrl.Trim();
        _config.WebSearch.ManagedEnabled = managedEnabled;
        _store.Save(_config);
        RepointSearch();
    }

    /// <summary>Clone + build (or update) the managed SearXNG from the pinned manifest source.</summary>
    public async Task InstallManagedSearxngAsync()
    {
        if (SearxngSource() is not { } src || string.IsNullOrWhiteSpace(src.Repo) || string.IsNullOrWhiteSpace(src.Commit))
        {
            SearxngStatus = "No SearXNG source in the manifest.";
            return;
        }
        if (SearxngInstalling) return;

        var overlayDir = Path.Combine(IntegratedTools.ScriptsSource, src.Overlay);
        SearxngInstalling = true;
        try
        {
            await Task.Run(() => _searxngInstaller.InstallAsync(src.Repo, src.Commit, overlayDir,
                (msg, pct) => Dispatcher.UIThread.Post(() => SearxngStatus = msg), CancellationToken.None))
                .ConfigureAwait(true);
            SearxngStatus = "Installed — ready.";
            RepointSearch(); // managed is now available; prefer it if nothing overrides
        }
        catch (Exception ex)
        {
            Log.Error("managed searxng install failed", ex);
            SearxngStatus = $"Install failed: {ex.Message}";
        }
        finally
        {
            SearxngInstalling = false;
        }
    }

    // --- Integrated tools provisioning --------------------------------------

    /// <summary>Human-readable provisioning status for the Settings UI.</summary>
    [ObservableProperty] private string _toolsStatus = "…";

    /// <summary>True while provisioning runs (disables the Download/Repair button).</summary>
    [ObservableProperty] private bool _toolsProvisioning;

    /// <summary>
    /// Startup bootstrap: provision the integrated tools (Node/Python/MCP/scripts) first, then
    /// auto-install the managed SearXNG (its venv needs the bundled Python), then re-point search.
    /// web_fetch_js and web_research simply start working once their backends are present.
    /// </summary>
    private async Task BootstrapToolsAsync()
    {
        await MaybeProvisionAsync().ConfigureAwait(false);
        await MaybeAutoInstallSearxngAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(RepointSearch);
    }

    /// <summary>On startup, provision anything the manifest declares that isn't installed/current.</summary>
    private async Task MaybeProvisionAsync()
    {
        try { if (_provisioner.IsFullyProvisioned()) { ToolsStatus = "Ready."; return; } }
        catch (Exception ex) { Log.Warn($"provision check failed: {ex.Message}"); }
        await ProvisionToolsAsync(force: false).ConfigureAwait(false);
    }

    /// <summary>Download/build (or repair) the integrated tools, reporting progress to the status line.</summary>
    public async Task ProvisionToolsAsync(bool force = false)
    {
        if (ToolsProvisioning) return;
        ToolsProvisioning = true;
        try
        {
            await Task.Run(() => _provisioner.ProvisionAsync(
                (msg, _) => Dispatcher.UIThread.Post(() => ToolsStatus = msg), force, CancellationToken.None))
                .ConfigureAwait(true);
            ToolsStatus = "Ready.";
        }
        catch (Exception ex)
        {
            Log.Error("tool provisioning failed", ex);
            ToolsStatus = $"Setup failed: {ex.Message}";
        }
        finally
        {
            ToolsProvisioning = false;
        }
    }

    /// <summary>On startup, build the managed SearXNG in the background when enabled and the pinned commit changed.</summary>
    private async Task MaybeAutoInstallSearxngAsync()
    {
        if (!_config.WebSearch.ManagedEnabled || SearxngSource() is not { } src) return;
        if (_searxngInstaller.IsInstalled && !_searxngInstaller.UpdateAvailable(src.Commit)) return;

        await InstallManagedSearxngAsync().ConfigureAwait(false);
    }

    /// <summary>Re-resolve the search backend and apply it live, stopping the managed process if unused.</summary>
    private void RepointSearch()
    {
        var provider = ResolveSearchProvider();
        if (!ReferenceEquals(provider, _managedProvider))
            _managedProvider?.Stop(); // switched away from the managed instance — free it
        _research.SetSearchProvider(provider);
    }

    /// <summary>Search backend precedence: custom URL → managed SearXNG (when installed) → none.</summary>
    private IWebSearchProvider? ResolveSearchProvider()
    {
        var cfg = _config.WebSearch;
        if (!string.IsNullOrWhiteSpace(cfg.SearxngUrl)) return new SearxngSearchProvider(cfg.SearxngUrl!);
        if (cfg.ManagedEnabled && _searxngInstaller.IsInstalled)
            return _managedProvider ??= new ManagedSearxngProvider(_searxngInstaller, cfg.ManagedPort);
        return null;
    }

    public IReadOnlyList<Skill> Skills() => _skills.List();

    public void OpenSkillsFolder()
    {
        Directory.CreateDirectory(TommyPaths.SkillsDir);
        Reveal(TommyPaths.SkillsDir);
    }

    // MCP management --------------------------------------------------------

    public IReadOnlyList<McpServerEntry> SavedMcpServers => _config.McpServers;
    public bool IsMcpConnected(string id) => _mcp.ConnectedIds.Contains(id);
    public int McpToolCount(string id) => _mcp.RegisteredTools(id).Count;

    // --- Per-chat MCP connection (IMcpConnector) ----------------------------

    /// <summary>Configured user servers + built-in integrations the active chat can connect.</summary>
    public IReadOnlyList<McpCatalogEntry> McpCatalog =>
        _config.McpServers
            .Select(e => new McpCatalogEntry(e.Id, string.IsNullOrWhiteSpace(e.DisplayName) ? e.Id : e.DisplayName,
                IsIntegration: false, Fields: Array.Empty<McpFieldSpec>()))
            .Concat(_integrations.All.Select(i => new McpCatalogEntry(i.Key, i.DisplayName, IsIntegration: true, i.Fields)))
            .ToList();

    public async Task<McpConnectResult> ConnectChatMcpAsync(ChatMcpRef reference)
    {
        var config = BuildChatMcpConfig(reference);
        if (config is null) return new McpConnectResult(false, Array.Empty<string>(), "unknown MCP server.");

        var names = await _mcp.ConnectAsync(config);
        return new McpConnectResult(names.Count > 0, names,
            names.Count > 0 ? null : "connected with 0 tools (check the configuration).");
    }

    public Task DisconnectChatMcpAsync(string id) => _mcp.DisconnectAsync(id);

    private McpServerConfig? BuildChatMcpConfig(ChatMcpRef reference)
    {
        if (reference.IntegrationKey is null)
            return _config.McpServers.FirstOrDefault(e => e.Id == reference.Id)?.ToConfig();

        return _integrations.Find(reference.IntegrationKey)?.Build(new McpInstance(
            reference.IntegrationKey, reference.Id, reference.DisplayName, reference.Config, reference.ReadOnly));
    }

    /// <summary>Add an MCP to the active chat (used by the sidebar picker code-behind).</summary>
    public Task AddUserMcpToActiveChatAsync(McpCatalogEntry entry) =>
        ActiveChat?.AddConnectedMcpAsync(new ChatMcpRef { Id = entry.Key, DisplayName = entry.DisplayName })
        ?? Task.CompletedTask;

    public Task AddIntegrationToActiveChatAsync(McpCatalogEntry entry, Dictionary<string, string> values, bool readOnly)
    {
        if (ActiveChat is not { } chat) return Task.CompletedTask;
        var id = $"{chat.ChatId[..Math.Min(8, chat.ChatId.Length)]}-{entry.Key}";
        return chat.AddConnectedMcpAsync(new ChatMcpRef
        {
            Id = id,
            DisplayName = entry.DisplayName,
            IntegrationKey = entry.Key,
            Config = values,
            ReadOnly = readOnly
        });
    }

    /// <summary>Add, connect, and persist a user MCP server. Returns the tool count.</summary>
    public async Task<int> AddMcpServerAsync(McpServerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) return 0;
        _config.McpServers.RemoveAll(s => s.Id == entry.Id);
        _config.McpServers.Add(entry);
        _store.Save(_config);

        var names = await _mcp.ConnectAsync(entry.ToConfig());
        return names.Count;
    }

    public async Task RemoveMcpServerAsync(string id)
    {
        await _mcp.DisconnectAsync(id);
        _config.McpServers.RemoveAll(s => s.Id == id);
        _store.Save(_config);
    }

    private static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", $"\"{path}\"");
            else
                System.Diagnostics.Process.Start("xdg-open", $"\"{path}\"");
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _telemetryTimer?.Stop();
        _managedProvider?.Dispose(); // stop the managed SearXNG process
        BrowserPane?.Dispose();
        _playwrightBrowser.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _browserSessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
