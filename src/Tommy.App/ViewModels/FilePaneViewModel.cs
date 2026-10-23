using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.App.Rendering;
using Tommy.Core.Runtime;
using Tommy.Core.Tools;

namespace Tommy.App.ViewModels;

/// <summary>How a presented file is displayed in the file pane.</summary>
public enum FileKind { Image, Markdown, Pdf, Html, Code }

/// <summary>
/// A file opened in the file pane via <c>present_file</c>, classified by extension:
/// images render as a bitmap; PDFs as rasterized page images (via bundled PyMuPDF);
/// markdown through the chat block pipeline; HTML and code/shell/text as a
/// syntax-highlighted block (HTML also gets an "Open in browser" action). Read-only.
/// </summary>
public sealed partial class FilePaneViewModel : ObservableObject
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public FilePaneViewModel(PresentedFile file)
    {
        Path = file.Path;
        Title = string.IsNullOrWhiteSpace(file.Title) ? System.IO.Path.GetFileName(file.Path) : file.Title!;
        var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();

        Kind = Classify(ext);

        switch (Kind)
        {
            case FileKind.Image:
                try { Image = new Bitmap(file.Path); }
                catch (Exception ex) { Error = $"Couldn't load image: {ex.Message}"; }
                break;

            case FileKind.Pdf:
                _ = RenderPdfAsync(file.Path); // async; populates Pages / Error
                break;

            default:
                LoadText(file.Path, ext);
                break;
        }
    }

    private static FileKind Classify(string ext) => ext switch
    {
        _ when ImageExts.Contains(ext) => FileKind.Image,
        ".md" or ".markdown" => FileKind.Markdown,
        ".pdf" => FileKind.Pdf,
        ".html" or ".htm" => FileKind.Html,
        _ => FileKind.Code // code/shell/text — highlighted if the language is known, plain otherwise
    };

    private void LoadText(string path, string ext)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (Kind == FileKind.Markdown)
                foreach (var block in MarkdownBlockBuilder.Build(text)) Blocks.Add(block);
            else
                Blocks.Add(new MdCode(ext.TrimStart('.'), text)); // CodeView highlights by language/extension
        }
        catch (Exception ex)
        {
            Error = $"Couldn't read file: {ex.Message}";
        }
    }

    // --- PDF rendering (bundled Python + PyMuPDF) ---------------------------

    [ObservableProperty] private bool _pdfLoading;

    private async Task RenderPdfAsync(string pdfPath)
    {
        PdfLoading = true;
        try
        {
            var python = IntegratedTools.FindPython();
            var script = IntegratedTools.Helper("pdf_render.py");
            if (python is null || !File.Exists(script))
            {
                SetError("PDF preview needs the bundled Python (still provisioning?). Use pdf_extract for the text.");
                return;
            }

            var outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tommy-pdf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            try
            {
                var pngs = await Task.Run(() => RunPdfRender(python, script, pdfPath, outDir)).ConfigureAwait(false);
                if (pngs.Count == 0)
                {
                    SetError("Couldn't render the PDF (it may be empty or damaged). Use pdf_extract for the text.");
                    return;
                }

                var bitmaps = new List<Bitmap>();
                foreach (var png in pngs)
                    try { bitmaps.Add(new Bitmap(png)); } catch { /* skip a bad page */ }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var bmp in bitmaps) Pages.Add(bmp);
                });
            }
            finally
            {
                try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            SetError($"PDF preview failed: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => PdfLoading = false);
        }
    }

    /// <summary>Run the render script and return the produced PNG paths (in page order).</summary>
    private static IReadOnlyList<string> RunPdfRender(string python, string script, string pdfPath, string outDir)
    {
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // -I isolates the interpreter so a module beside the PDF can't shadow a dependency.
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(pdfPath);
        psi.ArgumentList.Add(outDir);

        using var proc = Process.Start(psi);
        if (proc is null) return Array.Empty<string>();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(120_000);

        var paths = new List<string>();
        foreach (var line in stdout.Split('\n'))
        {
            var p = line.Trim();
            if (p.Length > 0 && File.Exists(p)) paths.Add(p);
        }
        return paths;
    }

    private void SetError(string message) =>
        Dispatcher.UIThread.Post(() => Error = message);

    // --- Open externally (Open in browser, for HTML) ------------------------

    public bool CanOpenExternally => Kind is FileKind.Html;

    [RelayCommand]
    private void OpenExternally()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", $"\"{Path}\"");
            else
                Process.Start("xdg-open", $"\"{Path}\"");
        }
        catch { /* best effort */ }
    }

    // --- bindable surface ---------------------------------------------------

    public string Title { get; }
    public string Path { get; }
    public FileKind Kind { get; }

    public bool IsImage => Kind == FileKind.Image;
    public bool IsPdf => Kind == FileKind.Pdf;
    public bool IsBlocks => Kind is FileKind.Markdown or FileKind.Html or FileKind.Code;

    public Bitmap? Image { get; }
    public ObservableCollection<Bitmap> Pages { get; } = new();
    public ObservableCollection<MdBlock> Blocks { get; } = new();

    [ObservableProperty] private string? _error;
    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
