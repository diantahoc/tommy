using System.Diagnostics;

namespace Tommy.Engine;

/// <summary>
/// Owns a managed Strata engine process, launched via its generated run script
/// (a <c>.bat</c> on Windows, a <c>.sh</c> on Linux — platform-generic). Stopping
/// kills the whole process tree, since the script typically spawns the engine.
/// </summary>
public sealed class ManagedEngineProcess : IDisposable
{
    private readonly string _installDir;
    private readonly string _runScript;
    private Process? _process;

    public ManagedEngineProcess(string installDir, string runScript)
    {
        _installDir = installDir;
        _runScript = runScript;
    }

    /// <summary>True while the launched process is running.</summary>
    public bool IsAlive => _process is { HasExited: false };

    public void Start()
    {
        if (IsAlive) return;

        var scriptPath = Path.Combine(_installDir, _runScript);
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"");
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh", $"\"{scriptPath}\"");
        }

        psi.WorkingDirectory = _installDir;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        _process = Process.Start(psi);
    }

    public void Stop()
    {
        if (IsAlive)
        {
            try { _process!.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
