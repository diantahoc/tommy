using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Research;

namespace Tommy.App.ViewModels;

/// <summary>
/// The live browser pane: streams the active chat's browser session as frames into an
/// <see cref="Frame"/> bitmap, and forwards the user's pointer/keyboard + an address bar
/// back to the session. User-driven input (watching / taking over) needs no approval;
/// the agent's own actions still go through the approval-gated browser tools.
/// </summary>
public sealed partial class BrowserPaneViewModel : ObservableObject, IDisposable
{
    private readonly IBrowserSession _session;
    private readonly CancellationTokenSource _cts = new();
    private int _frameW = 1280;
    private int _frameH = 800;

    public BrowserPaneViewModel(IBrowserSession session)
    {
        _session = session;
        _address = session.CurrentUrl ?? string.Empty;
        _ = StartAsync();
    }

    [ObservableProperty] private Bitmap? _frame;
    [ObservableProperty] private string _address = string.Empty;

    private async Task StartAsync()
    {
        try { await _session.StartScreencastAsync(OnFrame, _cts.Token).ConfigureAwait(false); }
        catch { /* pane just stays blank */ }
    }

    private void OnFrame(BrowserFrame f)
    {
        _frameW = f.Width;
        _frameH = f.Height;
        Bitmap bmp;
        try { using var ms = new MemoryStream(f.Jpeg); bmp = new Bitmap(ms); }
        catch { return; }
        Dispatcher.UIThread.Post(() =>
        {
            var old = Frame;
            Frame = bmp;
            old?.Dispose();
        });
    }

    // --- toolbar ------------------------------------------------------------

    [RelayCommand]
    private Task Navigate() =>
        string.IsNullOrWhiteSpace(Address) ? Task.CompletedTask : Run(_session.NavigateAsync(NormalizeUrl(Address), _cts.Token));

    [RelayCommand] private Task Back() => Run(_session.GoBackAsync(_cts.Token));
    [RelayCommand] private Task Forward() => Run(_session.GoForwardAsync(_cts.Token));
    [RelayCommand] private Task Reload() => Run(_session.ReloadAsync(_cts.Token));

    // --- input forwarding (called by the view; rel coords are 0..1 of the image) ---

    public Task PointerAsync(BrowserInputKind kind, double relX, double relY, double delta = 0) =>
        Run(_session.InputAsync(new BrowserInput(kind, Clamp01(relX) * _frameW, Clamp01(relY) * _frameH, Delta: delta), _cts.Token), refreshAddress: kind == BrowserInputKind.Click);

    public Task KeyAsync(string key) => Run(_session.InputAsync(new BrowserInput(BrowserInputKind.Key, Key: key), _cts.Token));
    public Task TextAsync(string text) => Run(_session.InputAsync(new BrowserInput(BrowserInputKind.Text, Text: text), _cts.Token));

    private async Task Run(Task op, bool refreshAddress = true)
    {
        try
        {
            await op.ConfigureAwait(false);
            if (refreshAddress && _session.CurrentUrl is { } url)
                await Dispatcher.UIThread.InvokeAsync(() => Address = url);
        }
        catch { /* best effort — input/nav failures shouldn't crash the pane */ }
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static string NormalizeUrl(string url) =>
        url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url.Trim();

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* best effort */ }
        _ = _session.StopScreencastAsync();
        _cts.Dispose();
        Frame?.Dispose();
    }
}
