using Tommy.Engine.Research.Searxng;

namespace Tommy.Tests;

/// <summary>
/// The side-effect-free parts of the managed-SearXNG install: locating the server dir, the
/// venv interpreter path, and the install/update marker logic. (Download/venv/pip and the live
/// process are covered by manual/opt-in end-to-end verification.)
/// </summary>
public class SearxngManagedTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tommy-searxng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string MakeVenvPython(string serverDir)
    {
        var dir = Path.Combine(serverDir, ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin");
        Directory.CreateDirectory(dir);
        var py = Path.Combine(dir, OperatingSystem.IsWindows() ? "python.exe" : "python3");
        File.WriteAllText(py, "");
        return py;
    }

    [Fact]
    public void FindServerDir_locates_the_directory_with_serve_waitress()
    {
        var root = TempDir();
        try
        {
            var server = Path.Combine(root, "src", "searxng");
            Directory.CreateDirectory(server);
            File.WriteAllText(Path.Combine(server, "serve_waitress.py"), "# stub");

            Assert.Equal(server, new SearxngInstaller(root).FindServerDir());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void IsInstalled_requires_both_a_venv_and_the_marker()
    {
        var root = TempDir();
        try
        {
            var server = Path.Combine(root, "src");
            Directory.CreateDirectory(server);
            File.WriteAllText(Path.Combine(server, "serve_waitress.py"), "# stub");
            var installer = new SearxngInstaller(root);

            Assert.False(installer.IsInstalled);         // nothing built yet

            MakeVenvPython(server);
            Assert.False(installer.IsInstalled);         // venv but no marker

            File.WriteAllText(Path.Combine(root, ".installed"), "81371861f67226091d1c4df2e3908a11d3b54eb9");
            Assert.True(installer.IsInstalled);
            Assert.Equal("81371861f67226091d1c4df2e3908a11d3b54eb9", installer.InstalledFrom);
            Assert.False(installer.UpdateAvailable("81371861f67226091d1c4df2e3908a11d3b54eb9")); // same commit
            Assert.True(installer.UpdateAvailable("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"));   // bumped commit
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void VenvPython_path_is_platform_correct()
    {
        var py = SearxngInstaller.VenvPython(OperatingSystem.IsWindows() ? @"C:\srv" : "/srv");
        Assert.Contains(".venv", py);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "python.exe" : "python3", py);
    }

    [Fact]
    public void ManagedSearxng_url_uses_the_configured_port()
    {
        using var m = new ManagedSearxng("/srv", "/srv/.venv/bin/python3", 8888);
        Assert.Equal("http://127.0.0.1:8888", m.Url);
    }
}
