namespace Tommy.Core.Memory;

/// <summary>
/// Conventions for a file-based memory directory: an always-loaded
/// <c>index.md</c> table of contents plus <c>&lt;topic&gt;.md</c> sub-files loaded on
/// demand. Shared so the tools and the prompt-assembly reader agree.
/// </summary>
public static class MemoryLayout
{
    public const string IndexFile = "index.md";

    public static string TopicFileName(string key) => Sanitize(key) + ".md";

    public static string Sanitize(string key) =>
        new(key.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-').ToArray());
}
