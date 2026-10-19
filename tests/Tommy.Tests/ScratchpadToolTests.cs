using System.Text.Json;
using Tommy.Core.Tools;
using Tommy.Tools;

namespace Tommy.Tests;

public class ScratchpadToolTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public ScratchpadToolTests()
    {
        _root = Directory.CreateTempSubdirectory("tommy-scratch-").FullName;
        _ctx = new ToolContext(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Scratchpad_tools_are_exec_tier()
    {
        Assert.Equal(PermissionTier.Exec, new PythonScratchpadTool().Tier);
        Assert.Equal(PermissionTier.Exec, new JavaScriptScratchpadTool().Tier);
        Assert.Equal(PermissionTier.Exec, new ShellTool().Tier);
    }

    [Fact]
    public async Task Shell_runs_echo()
    {
        var r = await new ShellTool().ExecuteAsync(El(new { command = "echo tommy_ok" }), _ctx, default);
        Assert.True(r.Success, r.Content);
        Assert.Contains("tommy_ok", r.Content);
    }

    [Fact]
    public async Task Shell_refuses_readonly()
    {
        var ro = new ToolContext(_root, ReadOnly: true);
        var r = await new ShellTool().ExecuteAsync(El(new { command = "echo x" }), ro, default);
        Assert.False(r.Success);
        Assert.Contains("read-only", r.Content);
    }

    [Fact]
    public async Task Shell_requires_command()
    {
        var r = await new ShellTool().ExecuteAsync(El(new { }), _ctx, default);
        Assert.False(r.Success);
        Assert.Contains("required", r.Content);
    }

    [Fact]
    public async Task Python_runs_when_available_else_degrades()
    {
        var r = await new PythonScratchpadTool().ExecuteAsync(El(new { code = "print('py_ok')" }), _ctx, default);
        if (r.Success) Assert.Contains("py_ok", r.Content);
        else Assert.Contains("unavailable", r.Content);
    }

    [Fact]
    public async Task Js_runs_when_available_else_degrades()
    {
        var r = await new JavaScriptScratchpadTool().ExecuteAsync(El(new { code = "console.log('js_ok')" }), _ctx, default);
        if (r.Success) Assert.Contains("js_ok", r.Content);
        else Assert.Contains("unavailable", r.Content);
    }

    [Fact]
    public async Task Python_refuses_readonly()
    {
        var ro = new ToolContext(_root, ReadOnly: true);
        var r = await new PythonScratchpadTool().ExecuteAsync(El(new { code = "print(1)" }), ro, default);
        Assert.False(r.Success);
        Assert.Contains("read-only", r.Content);
    }
}
