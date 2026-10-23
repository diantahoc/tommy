using Tommy.Engine.Provisioning;

namespace Tommy.Tests;

public class ToolProvisionerTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tommy-provision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Not_provisioned_when_manifest_missing()
    {
        var root = TempDir();
        var scripts = TempDir(); // empty — no tools.manifest.json
        try
        {
            var p = new ToolProvisioner(root, scripts);
            Assert.False(p.IsFullyProvisioned());
            Assert.EndsWith("tools.manifest.json", p.ManifestPath);
        }
        finally { Directory.Delete(root, true); Directory.Delete(scripts, true); }
    }

    [Fact]
    public void Not_provisioned_when_manifest_present_but_no_markers()
    {
        var root = TempDir();
        var scripts = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(scripts, "pdf_extract.py"), "print('x')");
            File.WriteAllText(Path.Combine(scripts, "tools.manifest.json"), """
            {
              "schemaVersion": 1,
              "archives": {
                "node": {
                  "version": "1.0.0", "dest": "runtimes", "stripComponents": 0,
                  "platforms": {
                    "win-x64": { "url": "https://ex/n.zip", "sha256": "aa" },
                    "linux-x64": { "url": "https://ex/n.txz", "sha256": "bb" }
                  }
                }
              },
              "scripts": {
                "pdf-extract": { "source": "pdf_extract.py", "dest": "helpers/pdf_extract.py", "pip": ["PyMuPDF"] }
              }
            }
            """);

            var p = new ToolProvisioner(root, scripts);
            // Manifest loads, but nothing is installed yet → markers absent → not provisioned.
            Assert.False(p.IsFullyProvisioned());
        }
        finally { Directory.Delete(root, true); Directory.Delete(scripts, true); }
    }
}
