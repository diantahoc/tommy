using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Chat;
using Tommy.Core.Configuration;
using Tommy.Core.Diagnostics;
using Tommy.Core.Engine;
using Tommy.Core.Tools;
using Tommy.Engine;
using Tommy.Mcp;
using Tommy.Persistence;
using Tommy.Persistence.Storage;
using Tommy.Tools;

namespace Tommy.App.ViewModels;

/// <summary>An entry in the sidebar chat list.</summary>
public sealed record ChatListItem(string Id, string Title, ChatKind Kind, string? Subtitle);

/// <summary>
/// The app shell: owns the engine, the repositories, the chat-list sidebar
/// (Chats/Code sections + search), and the active chat.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly EngineService _engine;
    private readonly GlobalDefaults _globals;
    private readonly ChatRepository _chats = new();
    private readonly WorkspaceRepository _workspaces = new();
    private readonly ProjectRepository _projects = new();
    private readonly ChatSearch _search;
    private readonly ToolRegistry _registry = new();
    private readonly SkillRepository _skills;
    private readonly McpManager _mcp;
    private readonly string _sandbox;

    public ShellViewModel(EngineService engine, GlobalDefaults globals)
    {
        _engine = engine;
        _globals = globals;

        // Native + memory tools by discovery; skill tools need the provider, so register them.
        _registry.DiscoverFrom(typeof(ReadFileTool).Assembly);
        StarterSkills.SeedIfEmpty(TommyPaths.SkillsDir);
        _skills = new SkillRepository();
        _registry.Register(new SkillSearchTool(_skills));
        _registry.Register(new SkillLoadTool(_skills));

        // MCP tools register through the same registry; predefined/user servers
        // connect via the manager (Phase 4B wires the roster + credentials).
        _mcp = new McpManager(_registry);

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
            _skills, memoryDirs, writeDir);
        vm.MetadataChanged += RefreshList;
        ActiveChat = vm;
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

        foreach (var record in _chats.List().Where(r => r.Kind == Section))
            ChatList.Add(new ChatListItem(record.Id, record.Title, record.Kind, SubtitleFor(record)));
    }

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

    public void Dispose() => _mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
