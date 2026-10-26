namespace Tommy.Engine.Agent;

/// <summary>
/// A resettable "send now" interrupt, separate from the run's Stop token. Signalling
/// it cancels only the <b>current</b> model generation; the loop then drops the
/// partial, resets it, and regenerates with the just-injected message — the run keeps
/// going. (A plain CancellationToken can't be reset, so the loop owns a fresh CTS.)
/// </summary>
public sealed class RunInterrupt : IDisposable
{
    private CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public bool IsSignalled => _cts.IsCancellationRequested;

    /// <summary>Interrupt the current generation.</summary>
    public void Signal()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Arm a fresh interrupt for the next generation (call after handling one).</summary>
    public void Reset()
    {
        var old = _cts;
        _cts = new CancellationTokenSource();
        old.Dispose();
    }

    public void Dispose() => _cts.Dispose();
}
