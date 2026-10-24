using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tommy.App.Behaviors;
using Tommy.App.Rendering;
using Tommy.App.ViewModels;

namespace Tommy.App.Views;

public partial class MainWindow : Window
{
    private BottomPinnedAutoScroll? _autoScroll;
    private MiddleClickAutoScroll? _middleClickScroll;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        // Behavior 2: bottom-pinned autoscroll, with the jump button shown when detached.
        _autoScroll = new BottomPinnedAutoScroll(
            ChatScroll,
            pinned => JumpButton.IsVisible = !pinned);

        // Browser-style middle-click autoscroll over the transcript.
        _middleClickScroll = new MiddleClickAutoScroll(ChatScroll);

        // Intercept Enter on the TUNNEL route: a TextBox with AcceptsReturn handles
        // Enter on the bubble route (inserting a newline) and marks it handled before
        // a bubble handler runs, so we must catch it on the way down to send.
        InputBox.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Keep the maximize/restore glyph in sync with the window state (also when
        // the OS changes it, e.g. Win+Up or snap).
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty) UpdateMaxIcon();
        };
        UpdateMaxIcon();

        if (DataContext is ShellViewModel { ActiveChat: not null }) FocusInput();
    }

    /// <summary>Put the caret in the chat input so the user can type immediately on open.</summary>
    private void FocusInput() =>
        Dispatcher.UIThread.Post(() => { if (InputBox.IsVisible) InputBox.Focus(); }, DispatcherPriority.Input);

    // --- Custom title bar: drag + window controls ---------------------------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            BeginMoveDrag(e);
    }

    private void OnMinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Show the restore glyph when maximized, the maximize glyph otherwise.</summary>
    private void UpdateMaxIcon() =>
        MaxIcon.Text = WindowState == WindowState.Maximized ? "" : "";

    private void OnChatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ChatListItem item } && !item.IsHeader && DataContext is ShellViewModel shell)
        {
            shell.SelectChat(item.Id);
            FocusInput();
        }
    }

    /// <summary>The single "New" button: a chat in the Chats tab, a folder-bound code chat in the Code tab.</summary>
    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;

        if (shell.IsCodeSection)
            await PickFolderAndCreateCodeChat(shell);
        else
            shell.NewChatCommand.Execute(null);

        FocusInput();
    }

    private async System.Threading.Tasks.Task PickFolderAndCreateCodeChat(ShellViewModel shell)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a workspace folder",
            AllowMultiple = false
        });

        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
            shell.CreateCodeChat(path);
    }

    private void OnJumpToLatest(object? sender, RoutedEventArgs e) =>
        _autoScroll?.ScrollToBottom();

    private void OnToggleExpand(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case ViewModels.ActivityViewModel a: a.IsExpanded = !a.IsExpanded; break;
            case ViewModels.ToolCallViewModel t: t.IsDetailExpanded = !t.IsDetailExpanded; break;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Shift+Enter inserts a newline.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var caret = InputBox.CaretIndex;
            InputBox.Text = (InputBox.Text ?? string.Empty).Insert(caret, "\n");
            InputBox.CaretIndex = caret + 1;
            e.Handled = true;
            return;
        }

        // Ctrl+Enter queues the message to send once the engine frees up (or sends it now if idle).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            if (InputBox.DataContext is ChatViewModel qvm) qvm.QueueCurrentInput();
            return;
        }

        // Plain Enter sends.
        e.Handled = true;
        if (InputBox.DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    private async void OnAttachFiles(object? sender, RoutedEventArgs e)
    {
        if (InputBox.DataContext is not ChatViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true
        });

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count > 0) vm.AttachFiles(paths);
    }

    private void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        var window = new SettingsWindow { DataContext = new SettingsViewModel(shell) };
        window.Show(this);
    }

    // Picking a server from the "Connected MCPs" (+) flyout: singletons connect
    // immediately; integration servers (e.g. MySQL) prompt for configuration first.
    private async void OnPickMcp(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: McpCatalogEntry entry }) return;
        if (DataContext is not ShellViewModel shell) return;

        // Close the picker flyout before opening any dialog.
        (sender as Visual)?.FindAncestorOfType<Popup>()?.Close();

        if (entry.IsIntegration)
            await ConfigureIntegrationAsync(shell, entry);
        else
            await shell.AddUserMcpToActiveChatAsync(entry);
    }

    // The compose "+" menu → Add database → MySQL / MSSQL: open the same connect
    // dialog the Connected-MCPs picker uses, selected by integration key.
    private async void OnAddDatabase(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        if (DataContext is not ShellViewModel shell) return;

        var entry = shell.McpCatalog.FirstOrDefault(x => x.IsIntegration && x.Key == key);
        if (entry is not null) await ConfigureIntegrationAsync(shell, entry);
    }

    // Prompt for an integration's configuration, then attach a configured instance
    // to the active chat.
    private async Task ConfigureIntegrationAsync(ShellViewModel shell, McpCatalogEntry entry)
    {
        var vm = new McpConfigViewModel(entry);
        var dialog = new McpConfigWindow { DataContext = vm };
        if (await dialog.ShowDialog<bool>(this))
            await shell.AddIntegrationToActiveChatAsync(entry, vm.Values(), vm.ReadOnly);
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant =
                app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MdCode code } && Clipboard is not null)
            await Clipboard.SetTextAsync(code.Code);
    }

    // --- Chat context menu --------------------------------------------------

    private void OnChatContextOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (menu.DataContext is not ChatListItem item || item.IsHeader || DataContext is not ShellViewModel shell)
        {
            menu.Close();
            return;
        }

        shell.SetContextTarget(item.Id);
        foreach (var obj in menu.Items)
        {
            if (obj is not MenuItem mi || mi.Header is not string header) continue;
            if (header == "Open in Explorer") mi.IsVisible = shell.ContextTargetIsCode;
            else if (header == "Assign to Project") PopulateAssignSubmenu(mi, shell);
        }
    }

    private static void PopulateAssignSubmenu(MenuItem parent, ShellViewModel shell)
    {
        parent.Items.Clear();

        var none = new MenuItem { Header = "None" };
        none.Click += (_, _) => shell.AssignContextChatToProject(null);
        parent.Items.Add(none);

        if (shell.ContextProjects.Count > 0) parent.Items.Add(new Separator());
        foreach (var project in shell.ContextProjects)
        {
            var id = project.Id;
            var mi = new MenuItem { Header = project.Name };
            mi.Click += (_, _) => shell.AssignContextChatToProject(id);
            parent.Items.Add(mi);
        }
    }

    private void OnPinChat(object? sender, RoutedEventArgs e) => Shell()?.PinContextChat();
    private void OnForkChat(object? sender, RoutedEventArgs e) => Shell()?.ForkContextChat();
    private void OnArchiveChat(object? sender, RoutedEventArgs e) => Shell()?.ArchiveContextChat();
    private void OnDeleteChat(object? sender, RoutedEventArgs e) => Shell()?.DeleteContextChat();

    private async void OnRenameChat(object? sender, RoutedEventArgs e)
    {
        if (Shell() is not { } shell) return;
        var current = (sender as MenuItem)?.DataContext as ChatListItem;
        var dialog = new TextPromptWindow("Rename chat", current?.Title ?? string.Empty);
        var name = await dialog.PromptAsync(this);
        if (!string.IsNullOrWhiteSpace(name)) shell.RenameContextChat(name);
    }

    private void OnOpenInExplorer(object? sender, RoutedEventArgs e)
    {
        if (Shell()?.ContextChatFolder() is { } folder)
            RevealFolder(folder);
    }

    private async void OnExportChat(object? sender, RoutedEventArgs e)
    {
        if (Shell()?.ExportContextChatMarkdown() is not { } export) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export chat to Markdown",
            SuggestedFileName = export.FileName,
            DefaultExtension = "md",
            FileTypeChoices = new[] { new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } } }
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(export.Markdown);
    }

    private ShellViewModel? Shell() => DataContext as ShellViewModel;

    // --- Browser pane resize handle -----------------------------------------

    private bool _resizingBrowser;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private void OnBrowserResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Shell() is not { } shell) return;
        _resizingBrowser = true;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = shell.BrowserPaneWidth;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void OnBrowserResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingBrowser || Shell() is not { } shell) return;
        // The handle sits on the pane's left edge: dragging left (dx < 0) widens the pane.
        var dx = e.GetPosition(this).X - _resizeStartX;
        shell.BrowserPaneWidth = _resizeStartWidth - dx;
    }

    private void OnBrowserResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        _resizingBrowser = false;
        e.Pointer.Capture(null);
    }

    private static void RevealFolder(string path)
    {
        if (!System.IO.Directory.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", $"\"{path}\"");
            else
                System.Diagnostics.Process.Start("xdg-open", $"\"{path}\"");
        }
        catch
        {
            // best effort — nothing actionable if the OS file manager can't launch
        }
    }
}
