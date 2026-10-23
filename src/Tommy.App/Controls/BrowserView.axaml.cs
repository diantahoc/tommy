using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Tommy.App.ViewModels;
using Tommy.Core.Research;

namespace Tommy.App.Controls;

/// <summary>
/// Renders the live browser frames and forwards the user's pointer/keyboard to the
/// session (watching / taking over). Coordinates are mapped from the control to the
/// letterboxed image, then handed to the view-model as 0..1 fractions.
/// </summary>
public partial class BrowserView : UserControl
{
    public BrowserView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private BrowserPaneViewModel? Vm => DataContext as BrowserPaneViewModel;

    // --- pointer ------------------------------------------------------------

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        Focus(); // so key events flow to the page
        if (Vm is { } vm && Map(e.GetPosition(Surface)) is { } p)
        {
            e.Pointer.Capture(Surface);
            _ = vm.PointerAsync(BrowserInputKind.Down, p.X, p.Y);
        }
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is { } vm && Map(e.GetPosition(Surface)) is { } p)
            _ = vm.PointerAsync(BrowserInputKind.Up, p.X, p.Y);
        e.Pointer.Capture(null);
    }

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        // Only forward moves while dragging, to avoid flooding the session with hovers.
        if (!e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        if (Vm is { } vm && Map(e.GetPosition(Surface)) is { } p)
            _ = vm.PointerAsync(BrowserInputKind.Move, p.X, p.Y);
    }

    private void OnSurfaceWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is { } vm && Map(e.GetPosition(Surface)) is { } p)
            _ = vm.PointerAsync(BrowserInputKind.Wheel, p.X, p.Y, delta: -e.Delta.Y * 100);
    }

    /// <summary>Map a control-space point to a 0..1 fraction within the letterboxed (Uniform) image.</summary>
    private (double X, double Y)? Map(Point p)
    {
        if (Surface.Source is not Bitmap src) return null;
        double cw = Surface.Bounds.Width, ch = Surface.Bounds.Height;
        double iw = src.Size.Width, ih = src.Size.Height;
        if (cw <= 0 || ch <= 0 || iw <= 0 || ih <= 0) return null;

        var scale = Math.Min(cw / iw, ch / ih);
        double dw = iw * scale, dh = ih * scale;
        double ox = (cw - dw) / 2, oy = (ch - dh) / 2;
        double relX = (p.X - ox) / dw, relY = (p.Y - oy) / dh;
        if (relX is < 0 or > 1 || relY is < 0 or > 1) return null; // outside the drawn image
        return (relX, relY);
    }

    // --- keyboard -----------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Vm is not { } vm) return;
        if (KeyName(e.Key) is { } name)
        {
            _ = vm.KeyAsync(name);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Vm is { } vm && !string.IsNullOrEmpty(e.Text))
        {
            _ = vm.TextAsync(e.Text);
            e.Handled = true;
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm)
        {
            vm.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Map special Avalonia keys to Playwright key names; null for printable (handled by text input).</summary>
    private static string? KeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Escape => "Escape",
        Key.Delete => "Delete",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        _ => null
    };
}
