using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using AvInline = Avalonia.Controls.Documents.Inline;

namespace Tommy.App.Rendering;

/// <summary>
/// Attached property that renders inline markdown (bold, italic, inline code,
/// links, line breaks) into a <see cref="SelectableTextBlock"/>'s Inlines, so
/// paragraph/heading/quote/list blocks keep their inline formatting without a
/// full markdown control.
/// </summary>
public static class MarkdownInline
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, string?>(
            "Text", typeof(MarkdownInline));

    public static void SetText(SelectableTextBlock element, string? value) =>
        element.SetValue(TextProperty, value);

    public static string? GetText(SelectableTextBlock element) =>
        element.GetValue(TextProperty);

    static MarkdownInline()
    {
        TextProperty.Changed.AddClassHandler<SelectableTextBlock>((tb, e) =>
        {
            tb.Inlines?.Clear();
            var text = e.NewValue as string;
            if (string.IsNullOrEmpty(text)) return;

            var doc = Markdown.Parse(text);
            foreach (var block in doc)
            {
                if (block is LeafBlock { Inline: { } container })
                    foreach (var inline in RenderInlines(container))
                        tb.Inlines!.Add(inline);
            }
        });
    }

    private static IEnumerable<AvInline> RenderInlines(ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    yield return new Run(literal.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.Bold;
                    else span.FontStyle = FontStyle.Italic;
                    foreach (var child in RenderInlines(emphasis))
                        span.Inlines.Add(child);
                    yield return span;
                    break;

                case CodeInline code:
                    yield return new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace")
                    };
                    break;

                case LinkInline link:
                    var linkSpan = new Span { Foreground = new SolidColorBrush(Color.Parse("#4C8DFF")) };
                    foreach (var child in RenderInlines(link))
                        linkSpan.Inlines.Add(child);
                    yield return linkSpan;
                    break;

                case LineBreakInline:
                    yield return new LineBreak();
                    break;

                // Raw inline HTML (e.g. a stray "<function=…>" the model emitted as text):
                // Markdig keeps the literal tag on .Tag. Without this we fall to the default
                // branch and ToString() leaks the class name "Markdig.Syntax.Inlines.HtmlInline".
                case HtmlInline html:
                    yield return new Run(html.Tag);
                    break;

                case HtmlEntityInline entity:
                    yield return new Run(entity.Transcoded.ToString());
                    break;

                case AutolinkInline auto:
                    yield return new Run(auto.Url);
                    break;

                // Unknown leaf inlines: recurse into containers, otherwise emit nothing —
                // never ToString() an inline (it returns the type name, not the text).
                default:
                    if (inline is ContainerInline nested)
                        foreach (var child in RenderInlines(nested))
                            yield return child;
                    break;
            }
        }
    }
}
