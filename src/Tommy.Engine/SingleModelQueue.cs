using System.Runtime.CompilerServices;

namespace Tommy.Engine;

/// <summary>
/// Serializes work against the single local model: only one generation runs at a
/// time, everything else queues. Sub-agent fan-out must route through the same
/// queue rather than issuing parallel calls. Surfaces a visible busy/queued
/// state via <see cref="StateChanged"/>.
/// </summary>
public sealed class SingleModelQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _depth;

    /// <summary>Number of requests waiting or running (0 = idle).</summary>
    public int Depth => Volatile.Read(ref _depth);

    /// <summary>True while a request holds the model.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Raised whenever the queue depth or busy state changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Run a unit of work exclusively, queueing behind any in-flight request.</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _depth);
        RaiseStateChanged();
        var acquired = false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            RaiseStateChanged();
            return await work(ct).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _gate.Release();
            Interlocked.Decrement(ref _depth);
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Stream a response while holding the model for the full duration of the
    /// stream, so no other request interleaves mid-generation.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        Func<CancellationToken, IAsyncEnumerable<ChatStreamEvent>> work,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Interlocked.Increment(ref _depth);
        RaiseStateChanged();
        var acquired = false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            RaiseStateChanged();

            await foreach (var ev in work(ct).WithCancellation(ct).ConfigureAwait(false))
                yield return ev;
        }
        finally
        {
            if (acquired) _gate.Release();
            Interlocked.Decrement(ref _depth);
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
