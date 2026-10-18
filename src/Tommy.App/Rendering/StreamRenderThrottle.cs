using System.Text;
using Avalonia.Threading;

namespace Tommy.App.Rendering;

/// <summary>
/// Decouples raw token arrival from UI updates. Incoming text lands in a buffer
/// (thread-safe, callable from the streaming worker); a <see cref="DispatcherTimer"/>
/// drains it on the UI thread at a readable cap. Because a tick drains at most
/// <c>capPerTick</c> characters, fast output is smoothed while slow output
/// (buffer never fills) is revealed immediately — the cap only engages above a
/// certain rate. Each drain is also when the trailing markdown block re-parses.
/// </summary>
public sealed class StreamRenderThrottle
{
    private readonly Action<string> _onChunk;
    private readonly int _capPerTick;
    private readonly TimeSpan _interval;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private DispatcherTimer? _timer;

    /// <param name="onChunk">Invoked on the UI thread with each drained chunk.</param>
    /// <param name="capPerTick">Max characters revealed per tick (reveal-rate cap).</param>
    /// <param name="intervalMs">Drain cadence; also the re-parse cadence.</param>
    public StreamRenderThrottle(Action<string> onChunk, int capPerTick = 48, int intervalMs = 30)
    {
        _onChunk = onChunk;
        _capPerTick = capPerTick;
        _interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    /// <summary>Start draining. Must be called on the UI thread.</summary>
    public void Start()
    {
        _timer = new DispatcherTimer { Interval = _interval };
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    /// <summary>Queue text from any thread.</summary>
    public void Enqueue(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_lock) _buffer.Append(text);
    }

    private void Drain()
    {
        string chunk;
        lock (_lock)
        {
            if (_buffer.Length == 0) return;
            var take = Math.Min(_capPerTick, _buffer.Length);
            chunk = _buffer.ToString(0, take);
            _buffer.Remove(0, take);
        }
        _onChunk(chunk);
    }

    /// <summary>Stop the timer and flush any remainder; completes on the UI thread.</summary>
    public Task CompleteAsync()
    {
        var tcs = new TaskCompletionSource();

        void Finish()
        {
            _timer?.Stop();
            string rest;
            lock (_lock)
            {
                rest = _buffer.ToString();
                _buffer.Clear();
            }
            if (rest.Length > 0) _onChunk(rest);
            tcs.TrySetResult();
        }

        if (Dispatcher.UIThread.CheckAccess()) Finish();
        else Dispatcher.UIThread.Post(Finish);

        return tcs.Task;
    }
}
