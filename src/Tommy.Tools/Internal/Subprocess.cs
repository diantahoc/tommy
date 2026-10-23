using System.ComponentModel;
using System.Diagnostics;

namespace Tommy.Tools.Internal;

/// <summary>
/// Minimal out-of-process runner for native-backed conversion tools (Tesseract,
/// Python). Captures stdout/stderr, enforces a timeout, and reports a missing
/// executable as <see cref="Result.Started"/> = false so callers degrade
/// gracefully instead of throwing.
/// </summary>
internal static class Subprocess
{
    public sealed record Result(bool Started, int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => Started && ExitCode == 0;
    }

    public static async Task<Result> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        string? workingDir,
        CancellationToken ct,
        int timeoutMs = 60_000,
        bool closeStdin = false)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = closeStdin,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDir is not null && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Win32Exception)
        {
            return new Result(Started: false, -1, "", "executable not found");
        }

        // Some wrapped programs block on a "Press Enter" prompt; closing stdin feeds
        // them EOF so they continue instead of hanging.
        if (closeStdin)
        {
            try { proc.StandardInput.Close(); } catch { /* best effort */ }
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new Result(Started: true, -1, await stdoutTask.ConfigureAwait(false), $"timed out after {timeoutMs / 1000}s");
        }

        return new Result(Started: true, proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
