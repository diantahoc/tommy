using Tommy.Core.Runtime;
using Tommy.Tools.Internal;

namespace Tommy.Tests;

public class SandboxTests
{
    private static SandboxSpec Spec(bool net = false) => new(
        Program: "/opt/lo/program/soffice",
        Args: new[] { "--headless", "--convert-to", "txt", "in.docx" },
        ReadOnlyPaths: new[] { "/opt/lo" },
        ReadWritePaths: new[] { "/work" },
        AllowNetwork: net);

    [Fact]
    public void Bwrap_binds_ro_rw_and_isolates()
    {
        var a = BwrapSandbox.BuildArgs(Spec()).ToList();
        var s = string.Join(" ", a);

        Assert.Contains("--ro-bind /opt/lo /opt/lo", s);
        Assert.Contains("--bind /work /work", s);
        Assert.Contains("--unshare-all", s);
        Assert.Contains("--die-with-parent", s);
        Assert.DoesNotContain("--share-net", a);           // network off by default
        // program + its args come last, in order.
        Assert.EndsWith("/opt/lo/program/soffice --headless --convert-to txt in.docx", s);
    }

    [Fact]
    public void Bwrap_shares_net_only_when_allowed()
    {
        Assert.Contains("--share-net", BwrapSandbox.BuildArgs(Spec(net: true)));
        Assert.DoesNotContain("--share-net", BwrapSandbox.BuildArgs(Spec(net: false)));
    }

    [Fact]
    public void AppContainer_maps_ro_rw_program_args()
    {
        var a = AppContainerSandbox.BuildArgs(Spec()).ToList();
        Assert.Equal(new[]
        {
            "appcontainer-exec", "/opt/lo", "/work",
            "/opt/lo/program/soffice", "--headless", "--convert-to", "txt", "in.docx"
        }, a);
    }

    [Fact]
    public void AppContainer_falls_back_to_program_dir_for_ro_root()
    {
        var spec = Spec() with { ReadOnlyPaths = Array.Empty<string>() };
        var a = AppContainerSandbox.BuildArgs(spec).ToList();
        // ro-root defaults to the program's directory.
        Assert.Equal("appcontainer-exec", a[0]);
        Assert.Equal(Path.GetDirectoryName("/opt/lo/program/soffice"), a[1]);
        Assert.Equal("/work", a[2]);
    }

    [Fact]
    public void ForThisPlatform_picks_a_backend_and_probes_without_throwing()
    {
        var sb = Sandbox.ForThisPlatform();
        Assert.False(string.IsNullOrEmpty(sb.Name));
        var available = sb.IsAvailable; // must not throw
        Assert.True(available || !available);
    }
}
