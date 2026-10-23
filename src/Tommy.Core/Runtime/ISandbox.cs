namespace Tommy.Core.Runtime;

/// <summary>
/// A process sandbox: runs an external program with filesystem access limited to
/// explicit read-only and read-write paths, and the network off by default. A
/// reusable primitive — the office converter runs LibreOffice in one, and other
/// tools (e.g. untrusted code execution) can reuse it. Backends: Windows
/// AppContainer (via LM Studio's <c>office-document-helper.exe appcontainer-exec</c>)
/// and Linux <c>bwrap</c> (bubblewrap). A missing backend reports
/// <see cref="IsAvailable"/> = false rather than running unsandboxed.
/// </summary>
public interface ISandbox
{
    /// <summary>A short backend name for messages ("AppContainer", "bwrap", "none").</summary>
    string Name { get; }

    /// <summary>Whether this backend can actually run a sandboxed process here.</summary>
    bool IsAvailable { get; }

    Task<SandboxRun> RunAsync(SandboxSpec spec, CancellationToken ct);
}

/// <summary>
/// What to run and what it may touch. <see cref="ReadOnlyPaths"/> are exposed
/// read-only (e.g. the program's install tree), <see cref="ReadWritePaths"/>
/// read-write (e.g. a scratch dir holding the input copy and output). The network
/// is off unless <see cref="AllowNetwork"/> is set.
/// </summary>
public sealed record SandboxSpec(
    string Program,
    IReadOnlyList<string> Args,
    IReadOnlyList<string> ReadOnlyPaths,
    IReadOnlyList<string> ReadWritePaths,
    bool AllowNetwork = false,
    string? WorkingDir = null,
    int TimeoutMs = 120_000);

/// <summary>The result of a sandboxed run. <see cref="Started"/> = false when the backend couldn't launch.</summary>
public sealed record SandboxRun(bool Started, int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => Started && ExitCode == 0;
}
