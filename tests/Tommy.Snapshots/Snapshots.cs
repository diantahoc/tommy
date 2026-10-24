using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tommy.App.Rendering;
using Tommy.App.ViewModels;
using Tommy.App.Views;
using Tommy.Core.Configuration;
using Tommy.Core.Engine;
using Tommy.Engine;
using Tommy.Persistence;
using Xunit;

namespace Tommy.Snapshots;

/// <summary>
/// Headless renders of the real windows, so UI work can be verified by looking
/// at a PNG instead of editing AXAML blind. See
/// developer-docs/claude-visual-verification.md.
/// </summary>
public class Snapshots
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Point all on-disk state at a throwaway dir so snapshots never touch the
        // real ~/.tommy. Set before anything resolves TommyPaths.
        var home = Path.Combine(Path.GetTempPath(), "tommy-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("TOMMY_HOME", home);
    }

    private sealed class FakeConfigStore(TommyConfig config) : IConfigStore
    {
        private TommyConfig _config = config;
        public TommyConfig Load() => _config;
        public void Save(TommyConfig c) => _config = c;
        public string Path => "(memory)";
    }

    /// <summary>An offline shell with one chat open — never connects to an engine.</summary>
    private static ShellViewModel OfflineShell()
    {
        var profile = new EngineProfile
        {
            Name = "snapshot",
            Mode = EngineMode.Remote,
            BaseUrl = "http://127.0.0.1:65535/v1"
        };
        var config = new TommyConfig { ActiveProfileName = "snapshot" };
        config.EngineProfiles.Add(profile);

        var engine = new EngineService(profile);
        var shell = new ShellViewModel(engine, config, new FakeConfigStore(config));
        shell.NewChatCommand.Execute(null); // sets ActiveChat (no engine needed)
        return shell;
    }

    [AvaloniaFact]
    public void MainWindow_sidebar_collapsed()
    {
        var shell = OfflineShell();
        shell.SidebarCollapsed = true; // no empty rail/sliver should remain
        var window = new MainWindow { DataContext = shell, Width = 1000, Height = 680 };
        Capture(window, "MainWindow-sidebar-collapsed");
    }

    [AvaloniaFact]
    public void MainWindow_archived_chats()
    {
        var shell = OfflineShell();
        var id = shell.ActiveChat!.ChatId;
        shell.SetContextTarget(id);
        shell.ArchiveContextChat();
        shell.ShowArchived = true; // sidebar shows the "Archived" header + the chat
        var window = new MainWindow { DataContext = shell, Width = 1000, Height = 680 };
        Capture(window, "MainWindow-archived-chats");
    }

    [AvaloniaFact]
    public void MainWindow_right_sidebar()
    {
        var shell = OfflineShell();
        shell.RightSidebarOpen = true;

        // Expand a couple of tool groups so the nested rows + group toggles show.
        var chat = shell.ActiveChat!;
        foreach (var g in chat.ToolGroups.Take(2)) g.IsExpanded = true;

        // Show the activity hint (normally set while a tool runs).
        chat.ActivityHint = "Read notes.txt";

        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 900 };
        Capture(window, "MainWindow-right-sidebar");
    }

    [AvaloniaFact]
    public void MainWindow_connected_mcps()
    {
        var shell = OfflineShell();
        shell.RightSidebarOpen = true;
        // Groups stay collapsed (default) so the Connected MCPs section is in view.
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        Capture(window, "MainWindow-connected-mcps");
    }

    [AvaloniaFact]
    public void Mcp_config_dialog()
    {
        var shell = OfflineShell();
        var entry = shell.McpCatalog.First(e => e.IsIntegration); // MySQL
        var window = new McpConfigWindow { DataContext = new McpConfigViewModel(entry) };
        Capture(window, "Mcp-config-dialog");
    }

    [AvaloniaFact]
    public void Mcp_config_dialog_mssql()
    {
        var shell = OfflineShell();
        // The MSSQL integration has three boolean fields — the best check that
        // boolean config renders as on/off switches rather than text boxes.
        var entry = shell.McpCatalog.First(e => e.DisplayName.Contains("SQL Server"));
        var window = new McpConfigWindow { DataContext = new McpConfigViewModel(entry) };
        Capture(window, "Mcp-config-dialog-mssql");
    }

    [AvaloniaFact]
    public void Settings_mcp_auth()
    {
        var shell = OfflineShell();
        var vm = new SettingsViewModel(shell) { NewIsHttp = true, NewAuthIsBearer = true };
        var window = new SettingsWindow { DataContext = vm, Width = 640, Height = 620 };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        // Jump to the "MCP servers" tab so the add form + auth fields render.
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is not null) tabs.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        var dir = SnapshotDir();
        Directory.CreateDirectory(dir);
        frame!.Save(System.IO.Path.Combine(dir, "Settings-mcp-auth.png"));
    }

    [AvaloniaFact]
    public void Wide_table_wraps_within_bubble()
    {
        // A wide, many-column table like the model produces — it must wrap to fit the
        // capped assistant-bubble width, not overflow/clip.
        const string md = """
            | Price | Item | Power | Filtration | Capacity | Reach | Extras | Best for |
            |---|---|---|---|---|---|---|---|
            | $141 | Sencor bagged canister | 750 W | HEPA + washable output | 3.5 L bag | 7 m cord, 10 m radius | power control, bag-full light, telescopic metal tubes, 2 nozzles | apartments, allergies |
            | $125 | Ariete Bag & Bagless | 2400 W | not stated | not stated | not stated | bag or bagless | cheapest big brand-name |
            """;
        var table = MarkdownBlockBuilder.Build(md).OfType<MdTable>().Single();

        // Host mirrors the MdTable DataTemplate (clipped, stretches to the bubble width).
        // Harder case: a table-only message (no prose to set the width). MinWidth on the
        // host keeps star columns from collapsing; mirrors the MdTable DataTemplate.
        var host = new Border { CornerRadius = new(6), ClipToBounds = true, MinWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch };
        MarkdownTableView.SetSource(host, table);

        var bubble = new Border { MaxWidth = 780, Padding = new(12, 10), HorizontalAlignment = HorizontalAlignment.Left, Child = host };
        var window = new Window { Content = bubble, Width = 860, Height = 320 };
        Capture(window, "Wide-table-wrap");
    }

    [AvaloniaFact]
    public void Project_create_and_archive_round_trip()
    {
        var shell = OfflineShell();
        var id = shell.ActiveChat!.ChatId;

        // Creating a project makes it available to assign.
        shell.CreateProject("Reports");
        shell.SetContextTarget(id);
        Assert.Contains(shell.ContextProjects, p => p.Name == "Reports");

        // Archiving drops the chat from the active list…
        shell.SetContextTarget(id);
        shell.ArchiveContextChat();
        Assert.DoesNotContain(shell.ChatList, i => !i.IsHeader && i.Id == id);

        // …and Show archived brings it back, flagged as archived.
        shell.ShowArchived = true;
        Assert.Contains(shell.ChatList, i => !i.IsHeader && i.Id == id);
        shell.SetContextTarget(id);
        Assert.True(shell.ContextTargetIsArchived);

        // Unarchive restores it to the active list.
        shell.UnarchiveContextChat();
        shell.ShowArchived = false;
        Assert.Contains(shell.ChatList, i => !i.IsHeader && i.Id == id);
    }

    private static void Capture(Window window, string name)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();

        var dir = SnapshotDir();
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, $"{name}.png"));
    }

    private static string SnapshotDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        var root = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "artifacts", "snapshots");
    }
}
