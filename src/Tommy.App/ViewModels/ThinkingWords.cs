using System;

namespace Tommy.App.ViewModels;

/// <summary>
/// A little variety for the "working" status the UI shows while the model runs —
/// so it reads as "Processing…", "Pondering…", "Mulling it over…" rather than a
/// flat "Thinking…" every time. Picked at meaningful transitions, not per token.
/// </summary>
internal static class ThinkingWords
{
    private static readonly string[] Words =
    {
        "Thinking…",
        "Processing…",
        "Pondering…",
        "Mulling it over…",
        "Chewing on it…",
        "Noodling…",
        "Cogitating…",
        "Ruminating…",
        "Working it out…",
        "Turning it over…"
    };

    /// <summary>A randomly chosen working word (thread-safe).</summary>
    public static string Next() => Words[Random.Shared.Next(Words.Length)];
}
