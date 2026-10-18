using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tommy.Core.Configuration;
using Tommy.Engine;
using Tommy.Persistence;
using Tommy.App.ViewModels;
using Tommy.App.Views;

namespace Tommy.App;

public partial class App : Application
{
    private EngineService? _engine;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new ConfigStore();
            var config = store.Load();

            if (config.NeedsFirstRun)
            {
                var setupVm = new FirstRunViewModel(store);
                var setupWindow = new FirstRunWindow { DataContext = setupVm };
                setupVm.Completed += (savedConfig, profile) =>
                {
                    var chat = CreateShellWindow(profile, savedConfig.Defaults);
                    chat.Show();
                    desktop.MainWindow = chat;
                    setupWindow.Close();
                };
                desktop.MainWindow = setupWindow;
            }
            else
            {
                desktop.MainWindow = CreateShellWindow(config.ActiveProfile!, config.Defaults);
            }

            desktop.ShutdownRequested += (_, _) => _engine?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateShellWindow(EngineProfile profile, GlobalDefaults defaults)
    {
        _engine?.Dispose();
        _engine = new EngineService(profile);

        var shell = new ShellViewModel(_engine, defaults);
        var window = new MainWindow { DataContext = shell };
        _ = shell.InitializeAsync();
        return window;
    }
}
