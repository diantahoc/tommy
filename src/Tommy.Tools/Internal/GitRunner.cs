using System.ComponentModel;
using System.Diagnostics;

namespace Tommy.Tools.Internal;

/// <summary>
/// Runs the <c>git</c> CLI out-of-process on behalf of the typed git tools. The
/// model never composes a git command line — each tool builds fixed arguments
/// around typed parameters and calls through here. Missing git or a timeout
/// degrades to a clean error instead of hanging or crashing.
/// </summary>
internal static class GitRunner
{
    public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;

        /// <summary>stdout, falling back to (or appending) stderr for messages.</summary>
        public string Combined =>
            string.IsNullOrWhiteSpace(StdErr) ? StdOut
            : StdOut.Length > 0 ? $"{StdOut}\n{StdErr}"
            : StdErr;
    }

    public static async Task<GitResult> RunAsync(
        string workingDir,
        IReadOnlyList<string> args,
        CancellationToken ct,
        int timeoutMs = 30_000)
    {
        if (!Directory.Exists(workingDir))
            return new GitResult(-1, "", "working directory does not exist.");

        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workingDir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Never block on an interactive credential / passphrase prompt.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Win32Exception)
        {
            return new GitResult(-1, "", "git is not installed or not on PATH.");
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
            return new GitResult(-1, "", $"git timed out after {timeoutMs / 1000}s.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new GitResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>Current branch name, or null outside a repo.</summary>
    public static async Task<string?> CurrentBranchAsync(string workingDir, CancellationToken ct)
    {
        var result = await RunAsync(workingDir, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ct, timeoutMs: 5_000)
            .ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : null;
    }
}
