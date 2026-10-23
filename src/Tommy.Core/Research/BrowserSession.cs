namespace Tommy.Core.Research;

/// <summary>
/// A long-lived, interactive browser the agent drives: navigate, read the current
/// page (readable text + a numbered <c>ref</c> map of interactive elements), click
/// or type by ref, and screenshot. One session per chat, with its own isolated,
/// persistent profile. Implemented by the engine over Playwright; the interface
/// lives in Core so tools and the UI can share it without a Playwright dependency.
/// </summary>
public interface IBrowserSession
{
    /// <summary>The current page URL, or null before the first navigation.</summary>
    string? CurrentUrl { get; }

    Task NavigateAsync(string url, CancellationToken ct);

    /// <summary>Read the current page: readable text + a ref map of interactive elements.</summary>
    Task<BrowserPage> ReadAsync(CancellationToken ct);

    /// <summary>Click the element with the given ref (from the last <see cref="ReadAsync"/>).</summary>
    Task ClickAsync(int reference, CancellationToken ct);

    /// <summary>Type into the element with the given ref; optionally press Enter to submit.</summary>
    Task TypeAsync(int reference, string text, bool submit, CancellationToken ct);

    /// <summary>Capture a PNG screenshot of the current viewport.</summary>
    Task<byte[]> ScreenshotAsync(CancellationToken ct);
}

/// <summary>A read of the current page: its URL/title, readable text, and interactive elements.</summary>
public sealed record BrowserPage(string Url, string Title, string Text, IReadOnlyList<BrowserElement> Elements);

/// <summary>One interactive element the agent can target by <see cref="Ref"/>.</summary>
public sealed record BrowserElement(int Ref, string Kind, string Name);
