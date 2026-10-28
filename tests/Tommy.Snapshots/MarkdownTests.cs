using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Tommy.App.Rendering;
using Xunit;

namespace Tommy.Snapshots;

public class MarkdownInlineTests
{
    [AvaloniaFact]
    public void Raw_inline_html_renders_as_its_literal_tag_not_the_class_name()
    {
        // A model sometimes emits a tool call as plain text; Markdig parses the
        // "<function=…>" / "<parameter=…>" tags as inline HTML. The renderer must
        // show the literal tag, never leak "Markdig.Syntax.Inlines.HtmlInline".
        var tb = new SelectableTextBlock();
        MarkdownInline.SetText(tb, "call <function=read_range> on path <parameter=path> now");

        var text = Flatten(tb.Inlines);
        Assert.Contains("<function=read_range>", text);
        Assert.Contains("<parameter=path>", text);
        Assert.DoesNotContain("HtmlInline", text);
        Assert.DoesNotContain("Markdig", text);
    }

    [AvaloniaFact]
    public void Ordinary_inline_formatting_still_renders()
    {
        var tb = new SelectableTextBlock();
        MarkdownInline.SetText(tb, "a **bold** and `code` word");

        var text = Flatten(tb.Inlines);
        Assert.Contains("bold", text);
        Assert.Contains("code", text);
    }

    private static string Flatten(InlineCollection? inlines)
    {
        var sb = new StringBuilder();
        if (inlines is null) return string.Empty;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run: sb.Append(run.Text); break;
                case Span span: sb.Append(Flatten(span.Inlines)); break;
                case LineBreak: sb.Append('\n'); break;
            }
        }
        return sb.ToString();
    }
}

public class MarkdownBlockBuilderTests
{
    [Fact]
    public void Pipe_table_maps_to_an_MdTable()
    {
        const string md = """
            | A | B |
            |---|---|
            | 1 | 2 |
            | 3 | 4 |
            """;

        var blocks = MarkdownBlockBuilder.Build(md);

        var table = Assert.IsType<MdTable>(Assert.Single(blocks));
        Assert.Equal(new[] { "A", "B" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "1", "2" }, table.Rows[0]);
        Assert.Equal(new[] { "3", "4" }, table.Rows[1]);
        Assert.Equal(2, table.ColumnCount);
    }

    [Fact]
    public void Non_table_text_is_not_a_table()
    {
        var blocks = MarkdownBlockBuilder.Build("Just a paragraph with a | pipe.");
        Assert.DoesNotContain(blocks, b => b is MdTable);
    }
}
