using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Runtime;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>Where a page/image's content is routed, given the engine's detected vision capability.</summary>
public enum VisionSink
{
    /// <summary>Vision on — hand the image straight to the model.</summary>
    Model,

    /// <summary>Vision off — extract text via OCR and keep a human preview.</summary>
    Ocr
}

/// <summary>
/// Vision is a detected capability, not a fixed limit: the same rendered image
/// has two possible sinks, chosen by whether the engine advertised image input
/// (<c>input_modalities</c> at connect). See File conversion &amp; capabilities.
/// </summary>
public static class VisionRouting
{
    public static VisionSink Route(bool visionEnabled) => visionEnabled ? VisionSink.Model : VisionSink.Ocr;

    public static string Describe(bool visionEnabled) => visionEnabled
        ? "Vision is on for this engine — this image can be sent straight to the model for layout, figures, or handwriting."
        : "Vision is off for this engine — use the `ocr` tool to extract text from this image.";
}

/// <summary>Read an image's format and dimensions (no native decoder) and say how to get its content.</summary>
[Tool]
public sealed class ImageReadTool : ITool
{
    public string Name => "image_read";
    public string Description => "Report an image's format and pixel dimensions, and how to read its content (model vs OCR) for this engine.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Path to the image file (png, jpeg, gif, or bmp)."), true));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return Task.FromResult(ToolResult.Fail("path is required."));
        if (!PathSandbox.TryResolve(ctx, rel, out var path, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail("image not found."));

        if (!ImageHeader.TryRead(path, out var info))
            return Task.FromResult(ToolResult.Fail("not a recognized image (expected png, jpeg, gif, or bmp)."));

        var text = $"{info.Format} image, {info.Width}×{info.Height} px.\n{VisionRouting.Describe(ctx.VisionEnabled)}";
        return Task.FromResult(ToolResult.Ok(text, ToolResultKind.Text, $"{info.Format} {info.Width}×{info.Height}"));
    }
}

/// <summary>Extract text from an image via Tesseract OCR, degrading cleanly if it isn't installed.</summary>
[Tool]
public sealed class OcrTool : ITool
{
    public string Name => "ocr";
    public string Description => "Extract text from an image using OCR (Tesseract). Reports clearly if Tesseract isn't installed.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Path to the image file to read."), true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return ToolResult.Fail("path is required.");
        if (!PathSandbox.TryResolve(ctx, rel, out var path, out var error))
            return ToolResult.Fail(error!);
        if (!File.Exists(path))
            return ToolResult.Fail("image not found.");

        if (!NativeProbe.IsOnPath("tesseract"))
            return ToolResult.Fail("OCR unavailable: Tesseract is not installed or not on PATH. Install tesseract-ocr to enable OCR.");

        var run = await Subprocess.RunAsync("tesseract", new[] { path, "stdout" }, null, ct).ConfigureAwait(false);
        if (!run.Started)
            return ToolResult.Fail("OCR unavailable: Tesseract could not be started.");
        if (!run.Ok)
            return ToolResult.Fail($"OCR failed: {run.StdErr.Trim()}");

        var text = run.StdOut.Trim();
        return ToolResult.Ok(text.Length == 0 ? "(no text found)" : text, ToolResultKind.Text, "ocr");
    }
}

/// <summary>Extract text from a PDF via an out-of-process Python (PyMuPDF) script, degrading cleanly if absent.</summary>
[Tool]
public sealed class PdfExtractTool : ITool
{
    // The PyMuPDF script ships as a helper file beside the app; the C# side
    // orchestrates and the bundled Python does the work.
    private const string ScriptName = "pdf_extract.py";

    public string Name => "pdf_extract";
    public string Description => "Extract text from a PDF, page by page. Needs Python with PyMuPDF; reports clearly if either is missing.";
    public PermissionTier Tier => PermissionTier.Read;
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Path to the PDF file."), true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return ToolResult.Fail("path is required.");
        if (!PathSandbox.TryResolve(ctx, rel, out var path, out var error))
            return ToolResult.Fail(error!);
        if (!File.Exists(path))
            return ToolResult.Fail("PDF not found.");

        var scriptPath = IntegratedTools.Helper(ScriptName);
        if (!File.Exists(scriptPath))
            return ToolResult.Fail($"PDF extraction unavailable: helper script is missing ({ScriptName}).");

        // Prefer the bundled full Python (ships with PyMuPDF); fall back to a
        // system interpreter on PATH so a dev machine still works.
        var python = IntegratedTools.FindPython()
            ?? (NativeProbe.IsOnPath("python") ? "python"
                : NativeProbe.IsOnPath("python3") ? "python3"
                : null);
        if (python is null)
            return ToolResult.Fail("PDF extraction unavailable: no bundled Python and none on PATH. Install Python and PyMuPDF (pip install PyMuPDF).");

        // -I isolates the interpreter so a module planted beside the PDF can't be
        // imported in place of a real dependency; the interpreter's own
        // site-packages (where PyMuPDF lives) stay available.
        var run = await Subprocess.RunAsync(python, new[] { "-I", scriptPath, path }, null, ct, timeoutMs: 120_000).ConfigureAwait(false);
        if (!run.Started)
            return ToolResult.Fail("PDF extraction unavailable: Python could not be started.");
        if (!run.Ok)
            return ToolResult.Fail($"PDF extraction failed: {run.StdErr.Trim()}");

        var text = run.StdOut.Trim();
        return ToolResult.Ok(text.Length == 0 ? "(no extractable text — the PDF may be scanned; try ocr)" : GitShared.Cap(text),
            ToolResultKind.Text, "pdf extract");
    }
}

/// <summary>
/// Extract text from an Office document (Word/Excel/PowerPoint/OpenDocument) by
/// converting it through bundled headless LibreOffice. C# orchestrates, soffice
/// does the heavy lifting — no per-format native .NET parsers. Degrades cleanly
/// when LibreOffice isn't present. (The LM Studio sandbox wrapper can be slotted
/// in later; for now the conversion runs directly.)
/// </summary>
[Tool]
public sealed class OfficeConvertTool : ITool
{
    // Target conversion filter per source extension: Writer-ish → text, Calc → csv.
    private static readonly Dictionary<string, (string Filter, string Ext)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = ("txt:Text", "txt"),
        [".doc"] = ("txt:Text", "txt"),
        [".odt"] = ("txt:Text", "txt"),
        [".rtf"] = ("txt:Text", "txt"),
        [".xlsx"] = ("csv", "csv"),
        [".xls"] = ("csv", "csv"),
        [".ods"] = ("csv", "csv"),
        [".pptx"] = ("txt:Text", "txt"),
        [".ppt"] = ("txt:Text", "txt"),
        [".odp"] = ("txt:Text", "txt")
    };

    public string Name => "office_extract";
    public string Description => "Extract text from a Word/Excel/PowerPoint/OpenDocument file via headless LibreOffice. Needs LibreOffice; reports clearly if it's missing.";
    public PermissionTier Tier => PermissionTier.Read;
    public IReadOnlyList<string> Aliases { get; } = new[] { "office_read", "word_extract", "excel_extract" };
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Path to the .docx/.xlsx/.pptx/.odt/… file."), true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rel = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return ToolResult.Fail("path is required.");
        if (!PathSandbox.TryResolve(ctx, rel, out var path, out var error))
            return ToolResult.Fail(error!);
        if (!File.Exists(path))
            return ToolResult.Fail("file not found.");

        if (!ByExtension.TryGetValue(Path.GetExtension(path), out var conv))
            return ToolResult.Fail($"unsupported document type: {Path.GetExtension(path)}");

        var soffice = IntegratedTools.FindSoffice()
            ?? (NativeProbe.IsOnPath("soffice") ? "soffice" : null);
        if (soffice is null)
            return ToolResult.Fail("Office extraction unavailable: LibreOffice is not bundled or on PATH.");

        // Untrusted documents are normally parsed inside a sandbox (AppContainer on Windows, bwrap on
        // Linux). When the user opts into unsandboxed office (Settings), run LibreOffice directly and
        // skip the sandbox entirely — on Windows the AppContainer path can't complete LibreOffice's
        // first-run user installation (see developer-docs/appcontainer-smoke-test-findings.md), so the
        // opt-in is what makes office_extract work there today.
        var sandbox = Sandbox.ForThisPlatform();
        var sandboxed = sandbox.IsAvailable && !ctx.AllowUnsandboxedOffice;
        if (!sandboxed && !ctx.AllowUnsandboxedOffice)
            return ToolResult.Fail(
                "Office extraction requires a sandbox and none is available. " +
                (OperatingSystem.IsLinux()
                    ? "Install bubblewrap (https://github.com/containers/bubblewrap), "
                    : "Provision the sandbox helper, ") +
                "or enable \"Run LibreOffice unsandboxed\" in Settings to run it unsandboxed.");

        // Isolate the LibreOffice user profile per run so concurrent/background conversions don't
        // trip "soffice is already running". When sandboxed, the work dir is the only writable root
        // and the input is copied into it (the original's folder isn't exposed to the sandbox).
        var work = Path.Combine(Path.GetTempPath(), $"tommy-office-{Guid.NewGuid():N}");
        var outDir = Path.Combine(work, "out");
        var profile = Path.Combine(work, "profile");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(profile);

        try
        {
            var input = path;
            if (sandboxed)
            {
                input = Path.Combine(work, "input" + Path.GetExtension(path));
                File.Copy(path, input, overwrite: true);
            }

            var sofficeArgs = new[]
            {
                "--headless", "--norestore", "--nolockcheck",
                $"-env:UserInstallation={new Uri(profile).AbsoluteUri}",
                "--convert-to", conv.Filter,
                "--outdir", outDir,
                input
            };

            bool started; string stderr;
            if (sandboxed)
            {
                var roRoots = IntegratedTools.SofficeRoot(soffice) is { } root ? new[] { root } : Array.Empty<string>();
                var run = await sandbox.RunAsync(new SandboxSpec(
                    soffice, sofficeArgs, roRoots, new[] { work }, AllowNetwork: false, TimeoutMs: 120_000), ct).ConfigureAwait(false);
                started = run.Started; stderr = run.StdErr;
            }
            else
            {
                var run = await Subprocess.RunAsync(soffice, sofficeArgs, null, ct, timeoutMs: 120_000).ConfigureAwait(false);
                started = run.Started; stderr = run.StdErr;
            }

            if (!started)
                return ToolResult.Fail("Office extraction unavailable: LibreOffice could not be started.");

            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "." + conv.Ext);
            if (!File.Exists(produced))
                return ToolResult.Fail($"Office extraction failed: {stderr.Trim()}".TrimEnd(':', ' '));

            var text = (await File.ReadAllTextAsync(produced, ct).ConfigureAwait(false)).Trim();
            var body = text.Length == 0 ? "(no extractable text)" : GitShared.Cap(text);
            if (!sandboxed) body = "(warning: ran LibreOffice unsandboxed — no sandbox backend available)\n\n" + body;
            return ToolResult.Ok(body, ToolResultKind.Text, "office extract");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
