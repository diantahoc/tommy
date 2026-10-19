namespace Tommy.Tools.Internal;

/// <summary>
/// Line-ending-aware text editing helpers. Edits match on LF-normalized text so
/// the model never has to know or guess a file's line endings (CRLF vs LF), and
/// writes preserve the file's original ending. This removes the main reason
/// multi-line anchors failed on Windows files.
/// </summary>
internal static class TextEdit
{
    /// <summary>The file's dominant newline: CRLF if any is present, else LF.</summary>
    public static string DetectEol(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>Normalize all line endings to LF for matching.</summary>
    public static string ToLf(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>Re-apply the file's original ending to LF text before writing.</summary>
    public static string ApplyEol(string lf, string eol) =>
        eol == "\n" ? lf : lf.Replace("\n", eol);

    /// <summary>Leading-whitespace prefix of a line.</summary>
    public static string LeadingWs(string line) =>
        line[..(line.Length - line.TrimStart().Length)];

    public static int Occurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>Split into lines, dropping a single trailing empty element (the final newline).</summary>
    public static string[] SplitLines(string lf)
    {
        var lines = lf.Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public enum MatchKind { Exact, TrimmedEnd, Trimmed, None }

    /// <summary>
    /// Find a unique occurrence of <paramref name="oldLf"/> (LF-normalized) in
    /// <paramref name="contentLf"/>, degrading from exact to trailing-whitespace
    /// to full-whitespace tolerance. Returns the line window on a unique match.
    /// </summary>
    public static MatchKind FindBlock(string contentLf, string oldLf, out int startLine, out int lineCount, out int occurrences)
    {
        startLine = 0; lineCount = 0; occurrences = 0;

        // Exact substring match is cheapest and preserves everything.
        var exact = Occurrences(contentLf, oldLf);
        if (exact >= 1)
        {
            occurrences = exact;
            if (exact > 1) return MatchKind.Exact; // caller treats >1 as not-unique
            // Resolve to a line window for a uniform replace path.
            var before = contentLf[..contentLf.IndexOf(oldLf, StringComparison.Ordinal)];
            startLine = before.Count(c => c == '\n');
            lineCount = SplitLines(oldLf).Length;
            return MatchKind.Exact;
        }

        var fileLines = contentLf.Split('\n');
        var oldLines = SplitLines(oldLf);
        if (oldLines.Length == 0) return MatchKind.None;

        foreach (var kind in new[] { MatchKind.TrimmedEnd, MatchKind.Trimmed })
        {
            Func<string, string> norm = kind == MatchKind.TrimmedEnd ? s => s.TrimEnd() : s => s.Trim();
            var o = oldLines.Select(norm).ToArray();
            var hits = new List<int>();
            for (var i = 0; i + o.Length <= fileLines.Length; i++)
            {
                var ok = true;
                for (var k = 0; k < o.Length; k++)
                    if (norm(fileLines[i + k]) != o[k]) { ok = false; break; }
                if (ok) hits.Add(i);
            }
            if (hits.Count == 1)
            {
                startLine = hits[0];
                lineCount = o.Length;
                occurrences = 1;
                return kind;
            }
            if (hits.Count > 1) { occurrences = hits.Count; return kind; }
        }

        return MatchKind.None;
    }

    /// <summary>
    /// Re-indent a replacement block so its first line aligns to
    /// <paramref name="targetIndent"/>, preserving each line's indentation
    /// relative to the block's own first line.
    /// </summary>
    public static string[] Reindent(string[] newLines, string sourceFirstIndent, string targetIndent)
    {
        if (sourceFirstIndent == targetIndent || newLines.Length == 0) return newLines;
        var baseNew = LeadingWs(newLines[0]);
        return newLines.Select(l =>
        {
            var rel = l.StartsWith(baseNew, StringComparison.Ordinal) ? l[baseNew.Length..] : l.TrimStart();
            return rel.Length == 0 ? string.Empty : targetIndent + rel;
        }).ToArray();
    }

    /// <summary>A short hint pointing at the nearest likely region when a match fails.</summary>
    public static string NearestHint(string contentLf, string oldLf)
    {
        var oldLines = SplitLines(oldLf);
        if (oldLines.Length == 0) return string.Empty;
        var anchor = oldLines[0].Trim();
        if (anchor.Length == 0) return string.Empty;

        var fileLines = contentLf.Split('\n');
        for (var i = 0; i < fileLines.Length; i++)
        {
            if (!fileLines[i].Contains(anchor, StringComparison.Ordinal)) continue;
            var from = Math.Max(0, i - 1);
            var to = Math.Min(fileLines.Length - 1, i + oldLines.Length);
            var region = string.Join('\n', fileLines[from..(to + 1)].Select((l, n) => $"{from + n + 1}: {l}"));
            return $" Nearest text is around line {i + 1} — the file has:\n{region}\nCheck indentation/exact characters, or use replace_lines/insert_file.";
        }
        return " Use read_file_range to copy the exact text, or use insert_file / replace_lines.";
    }
}
