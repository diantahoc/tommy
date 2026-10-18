using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tommy.Core.Configuration;
using Tommy.Core.Engine;
using Tommy.Engine;
using Tommy.App.ViewModels;
using Tommy.App.Views;

namespace Tommy.App;

public partial class App : Application
{
    // Default dev profile until first-run setup + config persistence lands.
    private const string DefaultBaseUrl = "http://127.0.0.1:8080/v1";

    private EngineService? _engine;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var profile = new EngineProfile
            {
                Name = "dev",
                Mode = EngineMode.Remote,
                BaseUrl = DefaultBaseUrl
            };
            _engine = new EngineService(profile);

            var vm = new ChatViewModel(_engine);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            desktop.ShutdownRequested += (_, _) => _engine?.Dispose();

            _ = vm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
