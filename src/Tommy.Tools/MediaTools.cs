using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Runtime;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Reports media metadata — container, duration, bitrate, and each video/audio/subtitle
/// stream — for a file in the workspace, using the bundled ffmpeg (shipped alongside the
/// browser tooling) or a system ffmpeg. Read-only: it probes with <c>ffmpeg -i</c> and
/// never transcodes or writes.
/// </summary>
[Tool]
public sealed class VideoInfoTool : ITool
{
    public string Name => "video_info";

    public string Description =>
        "Inspect a media file (video or audio) in the workspace and report its container, duration, " +
        "bitrate, and streams (codec, resolution, frame rate, audio channels). Read-only — probes with " +
        "ffmpeg, never transcodes.";

    public PermissionTier Tier => PermissionTier.Read;

    public IReadOnlyList<string> Aliases => new[] { "media_info", "ffprobe" };

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Media file path relative to the working directory."), true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path"), out var path, out var error))
            return ToolResult.Fail(error!);
        if (!File.Exists(path))
            return ToolResult.Fail($"file not found: {Args.Str(args, "path")}");

        var ffmpeg = IntegratedTools.FindFfmpeg()
            ?? (NativeProbe.IsOnPath("ffmpeg") ? "ffmpeg" : null);
        if (ffmpeg is null)
            return ToolResult.Fail("ffmpeg is not available (no bundled ffmpeg and none on PATH).");

        // `ffmpeg -i <file>` with no output destination prints the stream info to stderr and
        // exits 1 — that's expected, so we read the metadata from stderr whatever the exit code.
        var run = await Subprocess.RunAsync(
            ffmpeg, new[] { "-hide_banner", "-i", path }, ctx.WorkingDirectory, ct, timeoutMs: 30_000)
            .ConfigureAwait(false);

        if (!run.Started)
            return ToolResult.Fail("ffmpeg could not be started.");

        var summary = Summarize(run.StdErr);
        if (summary.Length == 0)
            return ToolResult.Fail(
                $"could not read media info for {PathSandbox.Relative(ctx, path)}. ffmpeg said:\n{GitShared.Cap(run.StdErr.Trim())}");

        return ToolResult.Ok(summary, ToolResultKind.Text, $"media: {PathSandbox.Relative(ctx, path)}");
    }

    /// <summary>
    /// Pulls the container, duration, and per-stream lines out of ffmpeg's probe output
    /// (everything else is log noise). Returns an empty string when the file isn't media.
    /// </summary>
    public static string Summarize(string ffmpegStderr)
    {
        var sb = new StringBuilder();
        foreach (var raw in ffmpegStderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Input #", StringComparison.Ordinal)
                || line.StartsWith("Duration:", StringComparison.Ordinal)
                || line.StartsWith("Stream #", StringComparison.Ordinal))
                sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }
}
