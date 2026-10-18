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
    }

    private void OnChatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ChatListItem item } && DataContext is ShellViewModel shell)
            shell.SelectChat(item.Id);
    }

    private async void OnNewCodeChat(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;

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

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Ctrl+Enter inserts a newline; plain Enter sends.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (sender is TextBox box)
            {
                var caret = box.CaretIndex;
                box.Text = (box.Text ?? string.Empty).Insert(caret, "\n");
                box.CaretIndex = caret + 1;
            }
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (sender is Control { DataContext: ChatViewModel vm } && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
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
