using System.Text.Json;
using Tommy.Core.Tools;
using Tommy.Persistence.Storage;
using Tommy.Tools;

namespace Tommy.Tests;

public class MemoryToolTests : IDisposable
{
    private readonly string _mem = Directory.CreateTempSubdirectory("tommy-mem-").FullName;
    public void Dispose() => Directory.Delete(_mem, recursive: true);

    private static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);
    private ToolContext Ctx() => new("/tmp", MemoryDirs: new[] { _mem }, WriteMemoryDir: _mem);

    [Fact]
    public async Task Write_then_recall_and_index_updated()
    {
        var write = await new MemoryWriteTool().ExecuteAsync(
            El(new { topic = "build-commands", content = "# Build\nRun `dotnet build`." }), Ctx(), default);
        Assert.True(write.Success);

        var recall = await new MemoryRecallTool().ExecuteAsync(El(new { key = "build-commands" }), Ctx(), default);
        Assert.Contains("dotnet build", recall.Content);

        var index = File.ReadAllText(Path.Combine(_mem, "index.md"));
        Assert.Contains("- build-commands:", index);
    }

    [Fact]
    public async Task Search_finds_across_subfiles()
    {
        await new MemoryWriteTool().ExecuteAsync(El(new { topic = "db", content = "Postgres on port 5432." }), Ctx(), default);
        var r = await new MemorySearchTool().ExecuteAsync(El(new { query = "5432" }), Ctx(), default);
        Assert.True(r.Success);
        Assert.Contains("db:", r.Content);
    }

    [Fact]
    public async Task Recall_without_memory_fails_cleanly()
    {
        var noMem = new ToolContext("/tmp");
        var r = await new MemoryRecallTool().ExecuteAsync(El(new { key = "x" }), noMem, default);
        Assert.False(r.Success);
        Assert.Contains("no memory", r.Content);
    }

    [Fact]
    public async Task Write_requires_writable_memory()
    {
        var readOnly = new ToolContext("/tmp", MemoryDirs: new[] { _mem });
        var r = await new MemoryWriteTool().ExecuteAsync(El(new { topic = "t", content = "c" }), readOnly, default);
        Assert.False(r.Success);
        Assert.Contains("no writable memory", r.Content);
    }

    [Fact]
    public void Reindex_rebuilds_from_subfiles()
    {
        File.WriteAllText(Path.Combine(_mem, "alpha.md"), "First alpha fact.");
        File.WriteAllText(Path.Combine(_mem, "beta.md"), "# Beta\nBeta detail.");
        new MemoryStore(_mem).Reindex();

        var index = File.ReadAllText(Path.Combine(_mem, "index.md"));
        Assert.Contains("- alpha: First alpha fact.", index);
        Assert.Contains("- beta: Beta", index);
    }
}

public class MemoryToolDiscoveryTests
{
    [Fact]
    public void Memory_tools_are_discovered()
    {
        var reg = new ToolRegistry();
        reg.DiscoverFrom(typeof(MemoryRecallTool).Assembly);
        Assert.NotNull(reg.Find("memory_recall"));
        Assert.NotNull(reg.Find("memory_search"));
        Assert.NotNull(reg.Find("memory_write"));
    }
}
