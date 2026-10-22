using Tommy.Tools;

namespace Tommy.Tests;

/// <summary>
/// Parsing ffmpeg's probe output (`ffmpeg -i`) down to the lines that matter — container,
/// duration, and streams — which is what the video_info tool returns.
/// </summary>
public class MediaToolsTests
{
    private const string SampleStderr =
        "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':\n" +
        "  Metadata:\n" +
        "    major_brand     : isom\n" +
        "    encoder         : Lavf58.76.100\n" +
        "  Duration: 00:01:23.45, start: 0.000000, bitrate: 1234 kb/s\n" +
        "  Stream #0:0(und): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080, 1200 kb/s, 30 fps, 30 tbr\n" +
        "  Stream #0:1(eng): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s\n" +
        "At least one output file must be specified\n";

    [Fact]
    public void Summarize_keeps_container_duration_and_streams()
    {
        var summary = VideoInfoTool.Summarize(SampleStderr);

        Assert.Contains("Input #0, mov,mp4", summary);
        Assert.Contains("Duration: 00:01:23.45", summary);
        Assert.Contains("1920x1080", summary);
        Assert.Contains("Video: h264", summary);
        Assert.Contains("Audio: aac", summary);
    }

    [Fact]
    public void Summarize_drops_metadata_and_log_noise()
    {
        var summary = VideoInfoTool.Summarize(SampleStderr);

        Assert.DoesNotContain("major_brand", summary);
        Assert.DoesNotContain("encoder", summary);
        Assert.DoesNotContain("output file must be specified", summary);
    }

    [Fact]
    public void Summarize_returns_empty_for_non_media()
    {
        Assert.Equal(string.Empty, VideoInfoTool.Summarize("clip.txt: Invalid data found when processing input\n"));
    }
}
