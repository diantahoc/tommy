using Avalonia;
using Avalonia.Controls;

namespace Tommy.App.Behaviors;

/// <summary>
/// Autoscrolls a <see cref="ScrollViewer"/> to the bottom only while the view is
/// already pinned there. The moment the user scrolls up, following detaches and
/// the viewport is never moved under them; a "jump to latest" affordance re-pins.
/// Implemented by watching Offset vs Extent via ScrollChanged.
/// </summary>
public sealed class BottomPinnedAutoScroll
{
    private const double Threshold = 24; // px tolerance for "at bottom"

    private readonly ScrollViewer _scrollViewer;
    private readonly Action<bool>? _onPinnedChanged;
    private bool _pinned = true;

    public BottomPinnedAutoScroll(ScrollViewer scrollViewer, Action<bool>? onPinnedChanged = null)
    {
        _scrollViewer = scrollViewer;
        _onPinnedChanged = onPinnedChanged;
        _scrollViewer.ScrollChanged += OnScrollChanged;
    }

    /// <summary>True while following the bottom.</summary>
    public bool IsPinned => _pinned;

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Content grew while pinned -> keep following the bottom.
        if (e.ExtentDelta.Y > 0 && _pinned)
        {
            ScrollToBottom();
            return;
        }

        // A real user scroll (offset changed, extent did not) -> re-evaluate pin.
        if (e.OffsetDelta.Y != 0 && e.ExtentDelta.Y == 0)
            SetPinned(IsAtBottom());
    }

    private bool IsAtBottom() =>
        _scrollViewer.Offset.Y >= _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - Threshold;

    /// <summary>Jump back to the bottom and re-pin.</summary>
    public void ScrollToBottom()
    {
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, _scrollViewer.Extent.Height);
        SetPinned(true);
    }

    private void SetPinned(bool value)
    {
        if (_pinned == value) return;
        _pinned = value;
        _onPinnedChanged?.Invoke(value);
    }
}
