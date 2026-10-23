using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Tommy.Engine.Provisioning;

/// <summary>
/// Shared plumbing for runtime provisioning: streamed downloads with progress,
/// SHA-256 verification, archive extraction (zip + tar.*), and running a child
/// process to completion. Used by both <see cref="ToolProvisioner"/> and the
/// managed-SearXNG installer so the download/verify/run behaviour is identical.
/// </summary>
internal static class ProvisionIo
{
    /// <summary>One client for all provisioning; large downloads get a long timeout.</summary>
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("tommy-provisioner");
        return http;
    }

    /// <summary>Stream a URL to <paramref name="dest"/>, reporting download percent via the callback.</summary>
    public static async Task DownloadAsync(string url, string dest, Action<string, double?>? onProgress, CancellationToken ct)
    {
        onProgress?.Invoke("downloading…", 0);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0)
            {
                var pct = read * 100.0 / total.Value;
                onProgress?.Invoke($"downloading… {pct:0}%", pct);
            }
        }
    }

    /// <summary>Compute a file's lowercase hex SHA-256.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Verify a downloaded file's SHA-256, throwing with both hashes on a mismatch.</summary>
    public static async Task VerifyAsync(string path, string expectedSha256, CancellationToken ct)
    {
        var actual = await Sha256Async(path, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SHA-256 mismatch for {Path.GetFileName(path)}: expected {expectedSha256.Trim()}, got {actual}.");
    }

    /// <summary>
    /// Extract an archive into <paramref name="destDir"/>. A plain <c>.zip</c> with no
    /// strip uses the managed <see cref="ZipFile"/>; everything else (tar.gz, tar.xz,
    /// .tgz, and any strip) goes through the system <c>tar</c> (bsdtar on Windows 10+,
    /// GNU tar on Linux), which auto-detects the compression.
    /// </summary>
    public static async Task ExtractAsync(string archivePath, string destDir, int stripComponents, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        var isZip = archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (isZip && stripComponents == 0)
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
            return;
        }

        var args = new List<string> { "-xf", archivePath, "-C", destDir };
        if (stripComponents > 0) args.Add($"--strip-components={stripComponents}");
        await RunAsync("tar", args, destDir, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
    }

    /// <summary>Run a child process to completion, throwing with its output on failure or timeout.</summary>
    public static async Task RunAsync(
        string exe, IReadOnlyList<string> args, string workingDir, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var (exit, _, stderr) = await RunCaptureAsync(exe, args, workingDir, timeout, ct, env).ConfigureAwait(false);
        if (exit != 0)
        {
            var err = stderr.Trim();
            var tail = err.Length > 600 ? err[^600..] : err;
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {exit}): {tail}");
        }
    }

    /// <summary>
    /// Run a child process and capture its exit code, stdout and stderr. Kills the tree on
    /// timeout (throwing), but a non-zero exit is returned to the caller, not thrown.
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunCaptureAsync(
        string exe, IReadOnlyList<string> args, string workingDir, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {Path.GetFileName(exe)}.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException($"{Path.GetFileName(exe)} {args.FirstOrDefault()} timed out after {timeout.TotalMinutes:0} min.");
        }

        return (proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
