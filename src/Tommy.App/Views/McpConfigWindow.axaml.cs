using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Tommy.App.Views;

/// <summary>Modal dialog that collects an instanceable MCP's configuration; returns true on Connect.</summary>
public partial class McpConfigWindow : Window
{
    public McpConfigWindow() => AvaloniaXamlLoader.Load(this);

    private void OnConnect(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
