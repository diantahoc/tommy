using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Tommy.App.Behaviors;

/// <summary>
/// Middle-click autoscroll for a <see cref="ScrollViewer"/>, like a browser: a middle-click
/// anchors a point and the view then scrolls at a speed proportional to how far the cursor
/// is from that anchor; any click stops it. Vertical only — the transcript doesn't scroll
/// horizontally. Setting Offset flows through the bottom-pinned autoscroll, so scrolling up
/// detaches from the bottom and reveals the jump-to-latest button as usual.
/// </summary>
public sealed class MiddleClickAutoScroll
{
    private const double DeadZone = 12;  // px around the anchor with no movement
    private const double Speed = 0.08;   // offset px per tick per px of cursor distance

    private readonly ScrollViewer _sv;
    private readonly DispatcherTimer _timer;
    private bool _active;
    private double _anchorY;
    private double _cursorY;

    public MiddleClickAutoScroll(ScrollViewer scrollViewer)
    {
        _sv = scrollViewer;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();

        // Tunnel so the middle-press is seen before child controls (text blocks, buttons).
        _sv.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        _sv.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_active)
        {
            Stop(); // any click while active stops autoscroll
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(_sv).Properties.IsMiddleButtonPressed)
        {
            _anchorY = _cursorY = e.GetPosition(_sv).Y;
            _active = true;
            _sv.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            _timer.Start();
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_active) _cursorY = e.GetPosition(_sv).Y;
    }

    private void Tick()
    {
        if (!_active) return;

        var dy = _cursorY - _anchorY;
        var magnitude = Math.Abs(dy) - DeadZone;
        if (magnitude <= 0) return;

        var delta = Math.Sign(dy) * magnitude * Speed;
        var maxY = Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);
        var newY = Math.Clamp(_sv.Offset.Y + delta, 0, maxY);
        if (Math.Abs(newY - _sv.Offset.Y) > 0.01)
            _sv.Offset = new Vector(_sv.Offset.X, newY);
    }

    private void Stop()
    {
        _active = false;
        _timer.Stop();
        _sv.Cursor = Cursor.Default;
    }
}
