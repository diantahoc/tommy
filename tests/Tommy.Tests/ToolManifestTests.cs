using Tommy.Engine.Provisioning;

namespace Tommy.Tests;

public class ToolManifestTests
{
    private const string Sample = """
    {
      // a comment and a trailing comma, both tolerated
      "schemaVersion": 1,
      "archives": {
        "node": {
          "kind": "runtime",
          "version": "24.22.0",
          "dest": "runtimes",
          "stripComponents": 0,
          "platforms": {
            "win-x64": { "url": "https://ex/node.zip", "sha256": "aa" },
            "linux-x64": { "url": "https://ex/node.txz", "sha256": "bb" },
          }
        },
        "mcp-server-mysql": {
          "kind": "mcp",
          "version": "2.0.9",
          "dest": "mcp/mcp-server-mysql-2.0.9",
          "stripComponents": 1,
          "npmInstall": ["--omit=dev", "--ignore-scripts"],
          "platforms": { "any": { "url": "https://ex/m.tgz", "sha256": "cc" } }
        }
      },
      "searxng": {
        "repo": "https://github.com/searxng/searxng.git",
        "commit": "81371861f67226091d1c4df2e3908a11d3b54eb9",
        "overlay": "searxng"
      },
      "scripts": {
        "pdf-extract": { "source": "pdf_extract.py", "dest": "helpers/pdf_extract.py", "pip": ["PyMuPDF"] }
      }
    }
    """;

    [Fact]
    public void Parses_archives_scripts_and_knobs()
    {
        var m = ToolManifest.Parse(Sample);

        Assert.Equal(1, m.SchemaVersion);
        Assert.True(m.Archives.ContainsKey("node"));
        Assert.Equal("runtimes", m.Archives["node"].Dest);
        Assert.Equal(1, m.Archives["mcp-server-mysql"].StripComponents);
        Assert.Equal(new[] { "--omit=dev", "--ignore-scripts" }, m.Archives["mcp-server-mysql"].NpmInstall);
        Assert.Equal(new[] { "PyMuPDF" }, m.Scripts["pdf-extract"].Pip);
    }

    [Fact]
    public void Parses_pinned_searxng_source()
    {
        var m = ToolManifest.Parse(Sample);
        Assert.NotNull(m.Searxng);
        Assert.Equal("https://github.com/searxng/searxng.git", m.Searxng!.Repo);
        Assert.Equal(40, m.Searxng.Commit.Length);
        Assert.Equal("searxng", m.Searxng.Overlay);
    }

    [Fact]
    public void AssetForHere_picks_platform_or_any_or_null()
    {
        var m = ToolManifest.Parse(Sample);

        // node has an entry for the current platform (win-x64 or linux-x64).
        Assert.NotNull(m.Archives["node"].AssetForHere());

        // mysql is platform-independent ("any").
        var mysql = m.Archives["mcp-server-mysql"].AssetForHere();
        Assert.NotNull(mysql);
        Assert.Equal("cc", mysql!.Sha256);

        // a spec with no matching platform resolves to null.
        var orphan = new ArchiveSpec { Platforms = { ["mac-arm64"] = new PlatformAsset { Url = "x", Sha256 = "y" } } };
        Assert.Null(orphan.AssetForHere());
    }

    [Fact]
    public void PlatformKey_is_win_or_linux()
    {
        var key = ToolManifest.PlatformKey;
        Assert.True(key is "win-x64" or "linux-x64");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"archives\": [ }")]
    public void Malformed_throws_InvalidOperation(string json)
    {
        Assert.Throws<InvalidOperationException>(() => ToolManifest.Parse(json));
    }

    [Fact]
    public void Shipped_manifest_parses_if_present()
    {
        // The real kit lives at <repo>/src/scripts/tools.manifest.json; only assert when found.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "scripts", "tools.manifest.json");
            if (File.Exists(candidate))
            {
                var m = ToolManifest.Load(candidate);
                Assert.True(m.Archives.ContainsKey("node"));
                Assert.True(m.Archives.ContainsKey("python"));
                Assert.True(m.Archives.ContainsKey("mingit"));
                Assert.Equal("msi", m.Archives["libreoffice"].Install);
                Assert.Equal("windows", m.Scripts["office-document-helper"].Os);
                Assert.True(m.Scripts.ContainsKey("pdf-extract"));
                Assert.NotNull(m.Searxng);
                Assert.Equal(40, m.Searxng!.Commit.Length);
                return;
            }
        }
    }
}
