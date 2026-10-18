using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tommy.App.ViewModels;

/// <summary>
/// One attached file shown in the compose strip and in the user's message bubble.
/// Images carry a small decoded thumbnail; other files show as a named document
/// chip. The file itself is copied into the chat's working directory so the
/// model's sandboxed tools (image_read / ocr / pdf_extract / read_file) can reach
/// it by <see cref="RelativePath"/>.
/// </summary>
public partial class AttachmentViewModel : ObservableObject
{
    private static readonly string[] ImageExts =
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public AttachmentViewModel(string fileName, string? fullPath, string relativePath, bool showRemove)
    {
        FileName = fileName;
        FullPath = fullPath;
        RelativePath = relativePath;
        ShowRemove = showRemove;
        IsImage = IsImageFile(fileName);

        if (IsImage && fullPath is not null && File.Exists(fullPath))
        {
            try
            {
                using var stream = File.OpenRead(fullPath);
                Thumbnail = Bitmap.DecodeToWidth(stream, 220);
            }
            catch
            {
                // A corrupt or unsupported image just falls back to the doc chip.
                IsImage = false;
            }
        }
    }

    public string FileName { get; }
    public string? FullPath { get; }
    public string RelativePath { get; }
    public bool IsImage { get; private set; }
    public bool ShowRemove { get; }
    public Bitmap? Thumbnail { get; }

    /// <summary>Invoked by the remove button in the compose strip.</summary>
    public Action? RemoveRequested { get; set; }

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke();

    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(ImageExts, ext) >= 0;
    }

    public static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" or ".md" or ".log" => "text/plain",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
