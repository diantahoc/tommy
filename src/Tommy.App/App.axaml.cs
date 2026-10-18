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
    private ShellViewModel? _shell;

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
                    var chat = CreateShellWindow(profile, savedConfig, store);
                    chat.Show();
                    desktop.MainWindow = chat;
                    setupWindow.Close();
                };
                desktop.MainWindow = setupWindow;
            }
            else
            {
                desktop.MainWindow = CreateShellWindow(config.ActiveProfile!, config, store);
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                _shell?.Dispose();
                _engine?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateShellWindow(EngineProfile profile, TommyConfig config, IConfigStore store)
    {
        _shell?.Dispose();
        _engine?.Dispose();
        _engine = new EngineService(profile);

        _shell = new ShellViewModel(_engine, config, store);
        var window = new MainWindow { DataContext = _shell };
        _ = _shell.InitializeAsync();
        return window;
    }
}
