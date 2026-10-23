using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tommy.Engine.Provisioning;

/// <summary>
/// The pinned provisioning manifest (<c>scripts/tools.manifest.json</c>): the
/// archives Tommy downloads + hash-verifies, and the scripts it installs from the
/// beside-app <c>scripts/</c> kit. Deserialized with <see cref="Parse"/>, which is
/// pure and unit-testable (no filesystem).
/// </summary>
public sealed class ToolManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("archives")] public Dictionary<string, ArchiveSpec> Archives { get; init; } = new();
    [JsonPropertyName("scripts")] public Dictionary<string, ScriptSpec> Scripts { get; init; } = new();

    /// <summary>The pinned SearXNG source (cloned at a commit + overlaid), or null if not configured.</summary>
    [JsonPropertyName("searxng")] public SearxngSpec? Searxng { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parse manifest JSON. Throws <see cref="InvalidOperationException"/> on malformed input.</summary>
    public static ToolManifest Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ToolManifest>(json, Options)
                ?? throw new InvalidOperationException("tools.manifest.json parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"tools.manifest.json is malformed: {ex.Message}", ex);
        }
    }

    public static ToolManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The current OS/arch key used to pick a platform asset (<c>win-x64</c> / <c>linux-x64</c>).</summary>
    public static string PlatformKey =>
        OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
}

/// <summary>A downloadable, SHA-256-pinned archive extracted under the integrated-tools root.</summary>
public sealed class ArchiveSpec
{
    /// <summary>Informational: runtime | mcp.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "runtime";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    /// <summary>Destination relative to the integrated-tools root (e.g. <c>runtimes</c>).</summary>
    [JsonPropertyName("dest")] public string Dest { get; init; } = "";
    [JsonPropertyName("stripComponents")] public int StripComponents { get; init; }
    /// <summary>Post-download install method: null = extract archive; <c>"msi"</c> = <c>msiexec /a</c> admin-extract.</summary>
    [JsonPropertyName("install")] public string? Install { get; init; }
    /// <summary>Extra args for a post-extract <c>npm install</c> (MCP packages); empty = none.</summary>
    [JsonPropertyName("npmInstall")] public string[]? NpmInstall { get; init; }
    /// <summary>Per-platform assets, keyed by platform (or <c>any</c> for platform-independent packages).</summary>
    [JsonPropertyName("platforms")] public Dictionary<string, PlatformAsset> Platforms { get; init; } = new();

    /// <summary>The asset for the current platform, or the <c>any</c> asset, or null if unsupported here.</summary>
    public PlatformAsset? AssetForHere() =>
        Platforms.TryGetValue(ToolManifest.PlatformKey, out var a) ? a
        : Platforms.TryGetValue("any", out var any) ? any
        : null;
}

/// <summary>A single downloadable asset: its URL and expected SHA-256.</summary>
public sealed class PlatformAsset
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
}

/// <summary>
/// The managed-SearXNG source: an upstream repo cloned at a pinned commit (the commit SHA is the
/// integrity pin), with our overlay (patch + settings + launcher) from the scripts/ kit laid on top.
/// </summary>
public sealed class SearxngSpec
{
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("commit")] public string Commit { get; init; } = "";
    /// <summary>Overlay subdirectory within the scripts/ kit (e.g. <c>searxng</c>).</summary>
    [JsonPropertyName("overlay")] public string Overlay { get; init; } = "searxng";
}

/// <summary>A tool installed from the beside-app <c>scripts/</c> kit into integrated-tools.</summary>
public sealed class ScriptSpec
{
    /// <summary>Path within the scripts/ kit (a folder for a Node program, a file for a single script).</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    /// <summary>Destination relative to the integrated-tools root (e.g. <c>helpers/web-fetch-js</c>).</summary>
    [JsonPropertyName("dest")] public string Dest { get; init; } = "";
    /// <summary>Restrict to an OS (<c>windows</c> / <c>linux</c>); null = any. Skipped on a mismatch.</summary>
    [JsonPropertyName("os")] public string? Os { get; init; }
    /// <summary>When set, run <c>npm install</c> in the destination (with these extra args) using the bundled Node.</summary>
    [JsonPropertyName("npmInstall")] public string[]? NpmInstall { get; init; }
    /// <summary>When set (e.g. <c>chromium</c>), run <c>playwright install &lt;browser&gt;</c> into the bundled browsers dir.</summary>
    [JsonPropertyName("playwright")] public string? Playwright { get; init; }
    /// <summary>When set, <c>pip install</c> these packages into the bundled Python.</summary>
    [JsonPropertyName("pip")] public string[]? Pip { get; init; }
}
