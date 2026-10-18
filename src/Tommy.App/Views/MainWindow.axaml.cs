using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tommy.App.Behaviors;
using Tommy.App.Rendering;

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

    private void OnJumpToLatest(object? sender, RoutedEventArgs e) =>
        _autoScroll?.ScrollToBottom();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter inserts a newline.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (DataContext is ViewModels.ChatViewModel vm && vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
        }
    }

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MdCode code } && Clipboard is not null)
            await Clipboard.SetTextAsync(code.Code);
    }
}
