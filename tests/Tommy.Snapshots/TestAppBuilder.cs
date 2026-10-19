using Avalonia;
using Avalonia.Headless;
using Tommy.Snapshots;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Tommy.Snapshots;

/// <summary>
/// Builds the real <see cref="App"/> (so its themes/styles load) on Avalonia's
/// headless platform with the Skia renderer, so views render to a real bitmap
/// in memory. App startup is guarded by the classic-desktop lifetime, so it
/// won't try to open a window under the headless lifetime.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Tommy.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
}
