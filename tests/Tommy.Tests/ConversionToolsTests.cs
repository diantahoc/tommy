using System.Buffers.Binary;
using System.Text.Json;
using Tommy.Core.Tools;
using Tommy.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tests;

public class ConversionToolsTests : IDisposable
{
    private readonly string _root;

    public ConversionToolsTests() => _root = Directory.CreateTempSubdirectory("tommy-conv-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Make(string name, byte[] bytes)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, bytes);
        return name;
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Task<ToolResult> Read(string rel, bool vision = false) =>
        new ImageReadTool().ExecuteAsync(Args($$"""{"path":"{{rel}}"}"""), new ToolContext(_root, VisionEnabled: vision), default);

    private static byte[] Png(int w, int h)
    {
        var b = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        // bytes 8-15: chunk length + "IHDR"; width at 16, height at 20 (big-endian).
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16, 4), w);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20, 4), h);
        return b;
    }

    private static byte[] Gif(int w, int h)
    {
        var b = new byte[24];
        "GIF89a"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6, 2), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8, 2), (ushort)h);
        return b;
    }

    private static byte[] Bmp(int w, int h)
    {
        var b = new byte[26];
        "BM"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18, 4), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22, 4), h);
        return b;
    }

    private static byte[] Jpeg(int w, int h)
    {
        // SOI + SOF0(len=17, precision=8, height, width) — enough for the header reader.
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08 };
        b.Add((byte)(h >> 8)); b.Add((byte)(h & 0xFF));
        b.Add((byte)(w >> 8)); b.Add((byte)(w & 0xFF));
        while (b.Count < 32) b.Add(0); // pad past the initial 24-byte read
        return b.ToArray();
    }

    [Fact]
    public async Task Image_read_reports_png_dimensions()
    {
        var result = await Read(Make("a.png", Png(800, 600)));
        Assert.True(result.Success);
        Assert.Contains("PNG", result.Content);
        Assert.Contains("800×600", result.Content);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("bmp")]
    [InlineData("jpg")]
    public async Task Image_read_reports_dimensions_across_formats(string kind)
    {
        var rel = kind switch
        {
            "gif" => Make("a.gif", Gif(320, 240)),
            "bmp" => Make("a.bmp", Bmp(320, 240)),
            _ => Make("a.jpg", Jpeg(320, 240))
        };
        var result = await Read(rel);
        Assert.True(result.Success);
        Assert.Contains("320×240", result.Content);
    }

    [Fact]
    public async Task Image_read_routes_by_vision_capability()
    {
        var off = await Read(Make("off.png", Png(10, 10)), vision: false);
        Assert.Contains("Vision is off", off.Content);
        Assert.Contains("ocr", off.Content);

        var on = await Read(Make("on.png", Png(10, 10)), vision: true);
        Assert.Contains("Vision is on", on.Content);
    }

    [Fact]
    public async Task Image_read_rejects_non_image()
    {
        var result = await Read(Make("a.png", new byte[] { 1, 2, 3, 4, 5 }));
        Assert.False(result.Success);
    }

    [Fact]
    public void Vision_routing_picks_sink()
    {
        Assert.Equal(VisionSink.Model, VisionRouting.Route(true));
        Assert.Equal(VisionSink.Ocr, VisionRouting.Route(false));
    }

    [Fact]
    public void NativeProbe_rejects_bogus_executable()
    {
        Assert.False(NativeProbe.IsOnPath("tommy-definitely-not-real-xyz"));
    }

    [Fact]
    public async Task Ocr_without_tesseract_degrades_clearly()
    {
        if (NativeProbe.IsOnPath("tesseract")) return; // installed here — the degrade path can't be exercised

        var result = await new OcrTool().ExecuteAsync(Args("""{"path":"x.png"}"""), new ToolContext(_root), default);
        Assert.False(result.Success);
        // Missing file is caught first; create one so we hit the dependency check.
        Make("img.png", Png(10, 10));
        var result2 = await new OcrTool().ExecuteAsync(Args("""{"path":"img.png"}"""), new ToolContext(_root), default);
        Assert.False(result2.Success);
        Assert.Contains("Tesseract", result2.Content);
    }

    [Fact]
    public async Task Pdf_extract_reports_missing_file()
    {
        var result = await new PdfExtractTool().ExecuteAsync(Args("""{"path":"nope.pdf"}"""), new ToolContext(_root), default);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task Office_extract_reports_missing_file()
    {
        var result = await new OfficeConvertTool().ExecuteAsync(Args("""{"path":"nope.docx"}"""), new ToolContext(_root), default);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task Office_extract_rejects_unsupported_type()
    {
        var rel = Make("notes.txt", System.Text.Encoding.UTF8.GetBytes("hi"));
        var result = await new OfficeConvertTool().ExecuteAsync(Args($$"""{"path":"{{rel}}"}"""), new ToolContext(_root), default);
        Assert.False(result.Success);
        Assert.Contains("unsupported", result.Content);
    }

    [Fact]
    public void Conversion_tools_are_discovered_with_read_tier()
    {
        var registry = new ToolRegistry();
        registry.DiscoverFrom(typeof(ImageReadTool).Assembly);
        foreach (var name in new[] { "image_read", "ocr", "pdf_extract", "office_extract" })
        {
            var tool = registry.Find(name);
            Assert.NotNull(tool);
            Assert.Equal(PermissionTier.Read, tool!.Tier);
        }
    }
}
