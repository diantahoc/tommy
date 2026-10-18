using Markdig;
using Markdig.Syntax;

namespace Tommy.App.Rendering;

/// <summary>
/// Parses accumulated markdown into a flat list of <see cref="MdBlock"/> using
/// Markdig's block AST. Inline formatting is preserved as inline-markdown source
/// on each block and rendered later; this keeps the renderer block-based rather
/// than a drop-in markdown control.
/// </summary>
public static class MarkdownBlockBuilder
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UsePipeTables()
        .Build();

    public static List<MdBlock> Build(string markdown)
    {
        var result = new List<MdBlock>();
        if (string.IsNullOrEmpty(markdown)) return result;

        var doc = Markdown.Parse(markdown, Pipeline);
        foreach (var block in doc)
            result.Add(Map(block, markdown));
        return result;
    }

    private static MdBlock Map(Block block, string source) => block switch
    {
        HeadingBlock h => new MdHeading(h.Level, Strip(Slice(source, h.Span), '#')),
        FencedCodeBlock f => new MdCode(string.IsNullOrWhiteSpace(f.Info) ? null : f.Info.Trim(), f.Lines.ToString()),
        CodeBlock c => new MdCode(null, c.Lines.ToString()),
        QuoteBlock q => new MdQuote(StripQuote(Slice(source, q.Span))),
        ThematicBreakBlock => new MdRule(),
        ListBlock list => new MdList(
            list.OfType<ListItemBlock>().Select(li => StripBullet(Slice(source, li.Span))).ToList(),
            list.IsOrdered),
        ParagraphBlock p => new MdParagraph(Slice(source, p.Span).Trim()),
        _ => new MdParagraph(Slice(source, block.Span).Trim())
    };

    private static string Slice(string source, SourceSpan span)
    {
        if (span.IsEmpty || span.Start < 0 || span.Start >= source.Length) return string.Empty;
        var end = Math.Min(span.End, source.Length - 1);
        var len = end - span.Start + 1;
        return len <= 0 ? string.Empty : source.Substring(span.Start, len);
    }

    private static string Strip(string text, char marker) =>
        text.TrimStart(marker).TrimStart();

    private static string StripQuote(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimStart();
            if (l.StartsWith('>')) l = l[1..];
            lines[i] = l.TrimStart();
        }
        return string.Join('\n', lines).Trim();
    }

    private static string StripBullet(string text)
    {
        var t = text.TrimStart();
        // unordered bullet
        if (t.Length > 1 && (t[0] is '-' or '*' or '+') && char.IsWhiteSpace(t[1]))
            return t[2..].Trim();
        // ordered "1." / "1)"
        var i = 0;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        if (i > 0 && i < t.Length && (t[i] is '.' or ')'))
            return t[(i + 1)..].Trim();
        return t.Trim();
    }
}
