using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Runtime;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Shared helpers for the Exec-tier scratchpads and shell: a per-workspace
/// scratch directory and output shaping. These run real processes, so they sit
/// at the Exec tier (gated by the autonomy grid) and refuse a read-only
/// workspace. No OS sandbox yet — containment is the permission tier plus the
/// working-directory sandbox; a stronger sandbox is a later, separable layer.
/// </summary>
internal static class Scratch
{
    public const string Dir = ".tommy-scratch";
    private const int TimeoutMs = 120_000;

    public static string EnsureDir(ToolContext ctx)
    {
        var dir = Path.Combine(Path.GetFullPath(ctx.WorkingDirectory), Dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ToolResult Shape(Subprocess.Result run, string label)
    {
        if (!run.Started)
            return ToolResult.Fail($"{label} could not be started.");

        var sb = new StringBuilder();
        if (run.StdOut.Trim().Length > 0) sb.Append(run.StdOut.TrimEnd());
        if (run.StdErr.Trim().Length > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("[stderr]\n").Append(run.StdErr.TrimEnd());
        }

        var text = GitShared.Cap(sb.ToString().Trim());
        if (run.Ok)
            return ToolResult.Ok(text.Length == 0 ? "(no output)" : text, ToolResultKind.Text, label);

        return ToolResult.Fail(text.Length == 0 ? $"{label} exited with code {run.ExitCode}." : $"{text}\n\n(exit code {run.ExitCode})");
    }

    public static int Timeout => TimeoutMs;
}

/// <summary>Run a Python snippet in the workspace (bundled Python; optional packages via a per-workspace venv).</summary>
[Tool]
public sealed class PythonScratchpadTool : ITool
{
    public string Name => "python_run";
    public string Description => "Run a short Python script in the workspace and return its output. Optionally install packages into a per-workspace virtualenv first. Prefer the typed file/data tools for simple tasks.";
    public PermissionTier Tier => PermissionTier.Exec;
    public IReadOnlyList<string> Aliases { get; } = new[] { "python", "run_python" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("code", ToolSchema.Str("Python source to execute."), true),
        ("packages", ToolSchema.Str("Optional space-separated pip packages to install into the workspace venv first."), false));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var pkgs = Args.Str(args, "packages");
        var summary = string.IsNullOrWhiteSpace(pkgs) ? "Run Python snippet" : $"Install [{pkgs}] then run Python snippet";
        return Task.FromResult<ToolPreview?>(new ToolPreview(summary, Args.Str(args, "code")));
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return ToolResult.Fail("working directory is read-only; scratchpads are disabled here.");
        var code = Args.Str(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            return ToolResult.Fail("code is required.");

        var basePython = IntegratedTools.FindPython()
            ?? (NativeProbe.IsOnPath("python") ? "python" : NativeProbe.IsOnPath("python3") ? "python3" : null);
        if (basePython is null)
            return ToolResult.Fail("Python scratchpad unavailable: no bundled Python and none on PATH.");

        var scratch = Scratch.EnsureDir(ctx);
        var packages = (Args.Str(args, "packages") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var python = basePython;
        if (packages.Length > 0)
        {
            var (venvPython, error) = await EnsureVenvAsync(basePython, scratch, ct).ConfigureAwait(false);
            if (venvPython is null)
                return ToolResult.Fail($"could not create workspace venv: {error}");
            python = venvPython;

            var install = await Subprocess.RunAsync(python, new[] { "-m", "pip", "install", "--disable-pip-version-check" }.Concat(packages).ToArray(),
                ctx.WorkingDirectory, ct, Scratch.Timeout).ConfigureAwait(false);
            if (!install.Ok)
                return ToolResult.Fail($"pip install failed: {install.StdErr.Trim()}");
        }

        var scriptPath = Path.Combine(scratch, $"snippet-{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(scriptPath, code, ct).ConfigureAwait(false);
            var run = await Subprocess.RunAsync(python, new[] { scriptPath }, ctx.WorkingDirectory, ct, Scratch.Timeout).ConfigureAwait(false);
            return Scratch.Shape(run, "python");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Create (once) a per-workspace venv and return its interpreter, or an error if the base Python can't make one.</summary>
    private static async Task<(string? Python, string? Error)> EnsureVenvAsync(string basePython, string scratch, CancellationToken ct)
    {
        var venv = Path.Combine(scratch, "venv");
        var venvPython = OperatingSystem.IsWindows()
            ? Path.Combine(venv, "Scripts", "python.exe")
            : Path.Combine(venv, "bin", "python3");

        if (File.Exists(venvPython)) return (venvPython, null);

        var make = await Subprocess.RunAsync(basePython, new[] { "-m", "venv", venv }, null, ct, Scratch.Timeout).ConfigureAwait(false);
        if (!make.Started) return (null, "Python could not be started.");
        if (!make.Ok || !File.Exists(venvPython))
            return (null, make.StdErr.Trim().Length > 0 ? make.StdErr.Trim() : "the bundled Python has no venv module.");
        return (venvPython, null);
    }
}

/// <summary>Run a JavaScript snippet in the workspace with the bundled Node.</summary>
[Tool]
public sealed class JavaScriptScratchpadTool : ITool
{
    public string Name => "js_run";
    public string Description => "Run a short JavaScript (Node, ES module) snippet in the workspace and return its output. Prefer the typed file/data tools for simple tasks.";
    public PermissionTier Tier => PermissionTier.Exec;
    public IReadOnlyList<string> Aliases { get; } = new[] { "javascript", "node_run" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("code", ToolSchema.Str("JavaScript source to execute with Node."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult<ToolPreview?>(new ToolPreview("Run JavaScript snippet", Args.Str(args, "code")));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return ToolResult.Fail("working directory is read-only; scratchpads are disabled here.");
        var code = Args.Str(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            return ToolResult.Fail("code is required.");

        var node = IntegratedTools.FindNode() ?? (NativeProbe.IsOnPath("node") ? "node" : null);
        if (node is null)
            return ToolResult.Fail("JavaScript scratchpad unavailable: no bundled Node and none on PATH.");

        var scratch = Scratch.EnsureDir(ctx);
        var scriptPath = Path.Combine(scratch, $"snippet-{Guid.NewGuid():N}.mjs");
        try
        {
            await File.WriteAllTextAsync(scriptPath, code, ct).ConfigureAwait(false);
            var run = await Subprocess.RunAsync(node, new[] { scriptPath }, ctx.WorkingDirectory, ct, Scratch.Timeout).ConfigureAwait(false);
            return Scratch.Shape(run, "node");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Minimal shell command execution — the deliberate last resort. Present so the
/// model has an escape hatch, but de-emphasized in favor of the typed tools.
/// </summary>
[Tool]
public sealed class ShellTool : ITool
{
    public string Name => "shell";
    public string Description => "Run a shell command in the workspace (last resort — prefer the typed file, search, git, and scratchpad tools, which are more reliable).";
    public PermissionTier Tier => PermissionTier.Exec;
    public IReadOnlyList<string> Aliases { get; } = new[] { "run_command" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("command", ToolSchema.Str("The command line to run."), true));

    public Task<ToolPreview?> PreviewAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult<ToolPreview?>(new ToolPreview($"Run: {Args.Str(args, "command")}"));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ReadOnly)
            return ToolResult.Fail("working directory is read-only; the shell is disabled here.");
        var command = Args.Str(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command is required.");

        var (exe, pre, _) = Shell();
        var run = await Subprocess.RunAsync(exe, pre.Append(command).ToArray(), ctx.WorkingDirectory, ct, Scratch.Timeout).ConfigureAwait(false);
        return Scratch.Shape(run, "shell");
    }

    /// <summary>
    /// The shell commands run through on this OS: PowerShell on Windows (always present, and
    /// a far better default than cmd.exe — pipes, Select-String, operators), bash on Linux/Mac
    /// (falling back to sh where bash is absent). <c>-NoProfile</c> keeps a user profile from
    /// changing behaviour and <c>-NonInteractive</c> avoids a prompt that would hang a headless run.
    /// </summary>
    internal static (string Exe, string[] Pre, string Name) Shell()
    {
        if (OperatingSystem.IsWindows())
            return ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command" }, "PowerShell");
        return File.Exists("/bin/bash")
            ? ("/bin/bash", new[] { "-c" }, "bash")
            : ("/bin/sh", new[] { "-c" }, "sh");
    }

    /// <summary>The shell's name for the system prompt, so the model writes the right dialect.</summary>
    public static string ShellDisplayName => Shell().Name;
}
