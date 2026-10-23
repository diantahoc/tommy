using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Tommy.App.ViewModels;

namespace Tommy.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnCopyHealth(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && Clipboard is not null)
            await Clipboard.SetTextAsync(vm.Shell.SearxngHealth ?? string.Empty);
    }
}
