using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Tommy.Core.Tools;
using Tommy.Tools;

namespace Tommy.Tests;

public class ArchiveToolsTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public ArchiveToolsTests()
    {
        _root = Directory.CreateTempSubdirectory("tommy-arc-").FullName;
        _ctx = new ToolContext(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string rel) => Path.Combine(_root, rel);
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private void MakeZip(string name, params (string entry, string content)[] entries)
    {
        using var zip = ZipFile.Open(Path_(name), ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var e = zip.CreateEntry(entry);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
    }

    private void MakeTarGz(string name, params (string entry, string content)[] entries)
    {
        using var file = File.Create(Path_(name));
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gz);
        foreach (var (entry, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var e = new PaxTarEntry(TarEntryType.RegularFile, entry) { DataStream = new MemoryStream(bytes) };
            tar.WriteEntry(e);
        }
    }

    [Fact]
    public async Task List_shows_zip_entries()
    {
        MakeZip("a.zip", ("hello.txt", "hi"), ("dir/world.txt", "world"));
        var result = await new ArchiveListTool().ExecuteAsync(Args("""{"path":"a.zip"}"""), _ctx, default);
        Assert.True(result.Success);
        Assert.Contains("hello.txt", result.Content);
        Assert.Contains("dir/world.txt", result.Content);
    }

    [Fact]
    public async Task Extract_zip_writes_files()
    {
        MakeZip("a.zip", ("hello.txt", "hi"), ("dir/world.txt", "world"));
        var result = await new ArchiveExtractTool().ExecuteAsync(Args("""{"path":"a.zip","dest":"out"}"""), _ctx, default);
        Assert.True(result.Success);
        Assert.Equal("hi", File.ReadAllText(Path_("out/hello.txt")));
        Assert.Equal("world", File.ReadAllText(Path_("out/dir/world.txt")));
    }

    [Fact]
    public async Task Extract_defaults_dest_to_archive_name()
    {
        MakeZip("bundle.zip", ("a.txt", "x"));
        var result = await new ArchiveExtractTool().ExecuteAsync(Args("""{"path":"bundle.zip"}"""), _ctx, default);
        Assert.True(result.Success);
        Assert.True(File.Exists(Path_("bundle/a.txt")));
    }

    [Fact]
    public async Task Extract_refuses_zip_slip()
    {
        using (var zip = ZipFile.Open(Path_("evil.zip"), ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("../escaped.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("pwned");
        }

        var result = await new ArchiveExtractTool().ExecuteAsync(Args("""{"path":"evil.zip","dest":"out"}"""), _ctx, default);
        Assert.False(result.Success);
        Assert.Contains("escapes", result.Content);
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "escaped.txt")));
    }

    [Fact]
    public async Task TarGz_list_and_extract_roundtrip()
    {
        MakeTarGz("a.tar.gz", ("notes.md", "# hi"), ("src/app.cs", "class A {}"));

        var list = await new ArchiveListTool().ExecuteAsync(Args("""{"path":"a.tar.gz"}"""), _ctx, default);
        Assert.True(list.Success);
        Assert.Contains("notes.md", list.Content);

        var extract = await new ArchiveExtractTool().ExecuteAsync(Args("""{"path":"a.tar.gz","dest":"out"}"""), _ctx, default);
        Assert.True(extract.Success);
        Assert.Equal("# hi", File.ReadAllText(Path_("out/notes.md")));
        Assert.Equal("class A {}", File.ReadAllText(Path_("out/src/app.cs")));
    }

    [Fact]
    public async Task Unknown_format_fails_cleanly()
    {
        File.WriteAllText(Path_("a.rar"), "not really");
        var result = await new ArchiveListTool().ExecuteAsync(Args("""{"path":"a.rar"}"""), _ctx, default);
        Assert.False(result.Success);
        Assert.Contains("unsupported", result.Content);
    }

    [Fact]
    public void Tiers_and_discovery()
    {
        Assert.Equal(PermissionTier.Read, new ArchiveListTool().Tier);
        Assert.Equal(PermissionTier.Write, new ArchiveExtractTool().Tier);

        var registry = new ToolRegistry();
        registry.DiscoverFrom(typeof(ArchiveListTool).Assembly);
        Assert.NotNull(registry.Find("archive_list"));
        Assert.NotNull(registry.Find("archive_extract"));
    }
}
