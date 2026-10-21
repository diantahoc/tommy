using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Chat;
using Tommy.Core.Configuration;
using Tommy.Core.Diagnostics;
using Tommy.Core.Engine;
using Tommy.Core.Mcp;
using Tommy.Core.Skills;
using Tommy.Core.Tools;
using Tommy.Engine;
using Tommy.Engine.Research;
using Tommy.Mcp;
using Tommy.Mcp.Integrations;
using Tommy.Mcp.WebSearch;
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
    private readonly WebSearchMcp _webSearch;
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
        // returning tagged data. The bundled web-search MCP backs both the search
        // provider (web_research query path) and the JS-rendering fetch
        // (web_fetch_js); when its Chromium/build isn't present it reports
        // unavailable and those paths degrade with a clear message.
        var fetcher = new HttpWebFetcher();
        var summarizer = new LlmSummarizer(_engine, () => _engine.ModelId);
        _webSearch = new WebSearchMcp();
        var searchProvider = _webSearch.IsAvailable ? _webSearch : null;
        _registry.Register(new WebFetchTool(fetcher, summarizer));
        _registry.Register(new WebResearchTool(new WebResearchOrchestrator(fetcher, summarizer, searchProvider)));
        _registry.Register(new BrowserReadTool(_webSearch.IsAvailable ? _webSearch : null, summarizer));

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
    public double SidebarWidthPx => SidebarCollapsed ? 54 : 250;

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
        OpenChat(handle, _sandbox);
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
        return handle.Record.ReadOnlyFolder ?? _sandbox;
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

        var vm = new ChatViewModel(_engine, _registry, _chats, handle, workingDir, resolved,
            _skills, memoryDirs, writeDir, _globals.ElectricityRate, _globals.Currency, mcpConnector: this);
        vm.MetadataChanged += RefreshList;
        _openChats[handle.Record.Id] = vm;
        ActiveChat = vm;
    }

    /// <summary>Drop a chat's cached view-model (on delete/archive or a settings re-open).</summary>
    private void EvictChat(string id)
    {
        if (_openChats.Remove(id, out var vm))
            vm.MetadataChanged -= RefreshList;
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

        var records = Sort(_chats.List().Where(r => r.Kind == Section && !r.Archived))
            .OrderByDescending(r => r.Pinned)
            .ToList();

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

    public ObservableCollection<ProjectMenuItem> ContextProjects { get; } = new();

    /// <summary>Capture the chat a context menu was opened on, and refresh the project submenu.</summary>
    public void SetContextTarget(string chatId)
    {
        ContextChatId = chatId;
        var record = _chats.LoadMeta(chatId);
        ContextTargetIsCode = record?.Kind == ChatKind.Code;

        ContextProjects.Clear();
        foreach (var project in _projects.List())
            ContextProjects.Add(new ProjectMenuItem(project.Id, project.Name));
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

    // --- Settings ----------------------------------------------------------

    public double ElectricityRate => _globals.ElectricityRate;
    public string Currency => _globals.Currency;

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
        _mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _webSearch.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
