namespace Tommy.Mcp;

/// <summary>
/// Minimal retry for a flaky async operation: run it, and on exception retry up to
/// <c>maxAttempts</c> with a fixed delay. Used for MCP connects, whose stdio server
/// process can exit on a cold first spawn and succeed on a retry.
/// </summary>
public static class AsyncRetry
{
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> op,
        int maxAttempts,
        TimeSpan delay,
        Action<int, Exception>? onRetry,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await op(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                last = ex;
                onRetry?.Invoke(attempt, ex);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        // The final attempt is outside the catch filter, so it returns or throws directly;
        // this is only reached if maxAttempts < 1.
        throw last ?? new InvalidOperationException("AsyncRetry needs at least one attempt.");
    }
}
