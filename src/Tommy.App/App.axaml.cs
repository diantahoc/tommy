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
                setupVm.Completed += (_, profile) =>
                {
                    var chat = CreateChatWindow(profile);
                    chat.Show();
                    desktop.MainWindow = chat;
                    setupWindow.Close();
                };
                desktop.MainWindow = setupWindow;
            }
            else
            {
                desktop.MainWindow = CreateChatWindow(config.ActiveProfile!);
            }

            desktop.ShutdownRequested += (_, _) => _engine?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateChatWindow(EngineProfile profile)
    {
        _engine?.Dispose();
        _engine = new EngineService(profile);

        var vm = new ChatViewModel(_engine);
        var window = new MainWindow { DataContext = vm };
        _ = vm.InitializeAsync();
        return window;
    }
}
