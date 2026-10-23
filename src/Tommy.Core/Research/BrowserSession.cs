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

    /// <summary>History/reload for the pane toolbar (user-driven; no approval).</summary>
    Task GoBackAsync(CancellationToken ct);
    Task GoForwardAsync(CancellationToken ct);
    Task ReloadAsync(CancellationToken ct);

    /// <summary>Read the current page: readable text + a ref map of interactive elements.</summary>
    Task<BrowserPage> ReadAsync(CancellationToken ct);

    /// <summary>Click the element with the given ref (from the last <see cref="ReadAsync"/>).</summary>
    Task ClickAsync(int reference, CancellationToken ct);

    /// <summary>Type into the element with the given ref; optionally press Enter to submit.</summary>
    Task TypeAsync(int reference, string text, bool submit, CancellationToken ct);

    /// <summary>Capture a PNG screenshot of the current viewport.</summary>
    Task<byte[]> ScreenshotAsync(CancellationToken ct);

    // --- live view (for the browser pane) ----------------------------------

    /// <summary>Start streaming viewport frames (JPEG). Replaces any running stream; off-thread.</summary>
    Task StartScreencastAsync(Action<BrowserFrame> onFrame, CancellationToken ct);

    /// <summary>Stop the frame stream.</summary>
    Task StopScreencastAsync();

    /// <summary>Forward a user input event from the pane (mouse coords are page CSS pixels).</summary>
    Task InputAsync(BrowserInput input, CancellationToken ct);
}

/// <summary>One screencast frame: JPEG bytes and the page's CSS-pixel size (for click mapping).</summary>
public sealed record BrowserFrame(byte[] Jpeg, int Width, int Height);

/// <summary>A user input event forwarded from the browser pane.</summary>
public sealed record BrowserInput(BrowserInputKind Kind, double X = 0, double Y = 0, string? Button = null, double Delta = 0, string? Key = null, string? Text = null);

public enum BrowserInputKind { Move, Down, Up, Click, Wheel, Key, Text }

/// <summary>A read of the current page: its URL/title, readable text, and interactive elements.</summary>
public sealed record BrowserPage(string Url, string Title, string Text, IReadOnlyList<BrowserElement> Elements);

/// <summary>One interactive element the agent can target by <see cref="Ref"/>.</summary>
public sealed record BrowserElement(int Ref, string Kind, string Name);
