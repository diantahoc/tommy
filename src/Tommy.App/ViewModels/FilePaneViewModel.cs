using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Tommy.App.Rendering;
using Tommy.Core.Tools;

namespace Tommy.App.ViewModels;

/// <summary>
/// A file opened in the file pane via <c>present_file</c>. Images render as a bitmap;
/// markdown renders through the same block pipeline as chat; any other text is shown
/// monospace. Read-only — it just displays what the agent produced.
/// </summary>
public sealed class FilePaneViewModel : ObservableObject
{
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public FilePaneViewModel(PresentedFile file)
    {
        Path = file.Path;
        Title = string.IsNullOrWhiteSpace(file.Title) ? System.IO.Path.GetFileName(file.Path) : file.Title!;
        var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();

        if (Array.IndexOf(ImageExts, ext) >= 0)
        {
            IsImage = true;
            try { Image = new Bitmap(file.Path); }
            catch (Exception ex) { Error = $"Couldn't load image: {ex.Message}"; }
            return;
        }

        try
        {
            var text = File.ReadAllText(file.Path);
            if (ext is ".md" or ".markdown")
                foreach (var block in MarkdownBlockBuilder.Build(text)) Blocks.Add(block);
            else
                Blocks.Add(new MdCode(ext.TrimStart('.'), text)); // monospace, with a copy button
        }
        catch (Exception ex)
        {
            Error = $"Couldn't read file: {ex.Message}";
        }
    }

    public string Title { get; }
    public string Path { get; }
    public bool IsImage { get; }
    public bool IsText => !IsImage;
    public Bitmap? Image { get; }
    public ObservableCollection<MdBlock> Blocks { get; } = new();
    public string? Error { get; }
    public bool HasError => Error is not null;
}
