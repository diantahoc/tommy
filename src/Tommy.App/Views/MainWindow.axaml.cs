using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Tommy.App.Behaviors;
using Tommy.App.Rendering;
using Tommy.App.ViewModels;

namespace Tommy.App.Views;

public partial class MainWindow : Window
{
    private BottomPinnedAutoScroll? _autoScroll;

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

        // Intercept Enter on the TUNNEL route: a TextBox with AcceptsReturn handles
        // Enter on the bubble route (inserting a newline) and marks it handled before
        // a bubble handler runs, so we must catch it on the way down to send.
        InputBox.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnChatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ChatListItem item } && !item.IsHeader && DataContext is ShellViewModel shell)
            shell.SelectChat(item.Id);
    }

    /// <summary>The single "New" button: a chat in the Chats tab, a folder-bound code chat in the Code tab.</summary>
    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;

        if (shell.IsCodeSection)
            await PickFolderAndCreateCodeChat(shell);
        else
            shell.NewChatCommand.Execute(null);
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

        // Ctrl+Enter / Shift+Enter insert a newline; plain Enter sends.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var caret = InputBox.CaretIndex;
            InputBox.Text = (InputBox.Text ?? string.Empty).Insert(caret, "\n");
            InputBox.CaretIndex = caret + 1;
            e.Handled = true;
            return;
        }

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
}
