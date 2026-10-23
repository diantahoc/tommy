using Tommy.Core.Runtime;

namespace Tommy.Tools.Internal;

/// <summary>Picks the right <see cref="ISandbox"/> backend for the current OS.</summary>
public static class Sandbox
{
    public static ISandbox ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new AppContainerSandbox()
        : OperatingSystem.IsLinux() ? new BwrapSandbox()
        : new NullSandbox();
}

/// <summary>No backend (unsupported OS): always unavailable.</summary>
public sealed class NullSandbox : ISandbox
{
    public string Name => "none";
    public bool IsAvailable => false;
    public Task<SandboxRun> RunAsync(SandboxSpec spec, CancellationToken ct) =>
        Task.FromResult(new SandboxRun(false, -1, "", "no sandbox backend on this platform."));
}

/// <summary>
/// Windows AppContainer sandbox via LM Studio's <c>office-document-helper.exe
/// appcontainer-exec &lt;ro-root&gt; &lt;rw-root&gt; &lt;program&gt; [args…]</c>. One
/// read-only root + one read-write root; the program's own tree is reachable
/// through the ro-root. (The helper's <c>sandbox-check</c> probes a stricter,
/// unrelated mechanism, so availability is based on the helper being present.)
/// </summary>
public sealed class AppContainerSandbox : ISandbox
{
    public string Name => "AppContainer";

    public bool IsAvailable => OperatingSystem.IsWindows() && IntegratedTools.FindSandboxHelper() is not null;

    public async Task<SandboxRun> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        var helper = IntegratedTools.FindSandboxHelper();
        if (helper is null)
            return new SandboxRun(false, -1, "", "the AppContainer helper is not installed.");

        var args = BuildArgs(spec);
        var run = await Subprocess.RunAsync(helper, args, spec.WorkingDir, ct, spec.TimeoutMs, closeStdin: true).ConfigureAwait(false);
        return new SandboxRun(run.Started, run.ExitCode, run.StdOut, run.StdErr);
    }

    /// <summary>Build the helper argv (testable). Uses the first ro/rw path; the program dir is the ro fallback.</summary>
    public static IReadOnlyList<string> BuildArgs(SandboxSpec spec)
    {
        var ro = spec.ReadOnlyPaths.FirstOrDefault() ?? Path.GetDirectoryName(spec.Program) ?? ".";
        var rw = spec.ReadWritePaths.FirstOrDefault() ?? Path.GetTempPath();
        var args = new List<string> { "appcontainer-exec", ro, rw, spec.Program };
        args.AddRange(spec.Args);
        return args;
    }
}

/// <summary>
/// Linux sandbox via <c>bwrap</c> (bubblewrap). Binds the system runtime read-only,
/// each <see cref="SandboxSpec.ReadOnlyPaths"/> read-only and each
/// <see cref="SandboxSpec.ReadWritePaths"/> read-write, unshares everything
/// (including the network, unless <see cref="SandboxSpec.AllowNetwork"/>), and dies
/// with the parent. bwrap is a user-installed prerequisite (needs setup/root).
/// </summary>
public sealed class BwrapSandbox : ISandbox
{
    public string Name => "bwrap";

    public bool IsAvailable => OperatingSystem.IsLinux() && NativeProbe.IsOnPath("bwrap");

    public async Task<SandboxRun> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        if (!NativeProbe.IsOnPath("bwrap"))
            return new SandboxRun(false, -1, "", "bwrap is not installed. Install bubblewrap (https://github.com/containers/bubblewrap).");

        var args = BuildArgs(spec);
        var run = await Subprocess.RunAsync("bwrap", args, spec.WorkingDir, ct, spec.TimeoutMs, closeStdin: true).ConfigureAwait(false);
        return new SandboxRun(run.Started, run.ExitCode, run.StdOut, run.StdErr);
    }

    // System dirs to bind read-only when present (not all exist on every distro).
    private static readonly string[] SystemRoBinds = { "/usr", "/lib", "/lib64", "/bin", "/sbin", "/etc" };

    /// <summary>Build the bwrap argv (testable, pure — no filesystem checks except the well-known system dirs).</summary>
    public static IReadOnlyList<string> BuildArgs(SandboxSpec spec)
    {
        var a = new List<string>();
        foreach (var dir in SystemRoBinds)
            if (Directory.Exists(dir)) { a.Add("--ro-bind"); a.Add(dir); a.Add(dir); }

        foreach (var ro in spec.ReadOnlyPaths) { a.Add("--ro-bind"); a.Add(ro); a.Add(ro); }
        foreach (var rw in spec.ReadWritePaths) { a.Add("--bind"); a.Add(rw); a.Add(rw); }

        a.Add("--proc"); a.Add("/proc");
        a.Add("--dev"); a.Add("/dev");
        a.Add("--tmpfs"); a.Add("/tmp");
        a.Add("--unshare-all");
        if (spec.AllowNetwork) a.Add("--share-net");
        a.Add("--die-with-parent");

        a.Add(spec.Program);
        a.AddRange(spec.Args);
        return a;
    }
}
