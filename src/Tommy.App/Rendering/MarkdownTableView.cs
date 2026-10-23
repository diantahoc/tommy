using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tommy.App.Rendering;

/// <summary>
/// Attached property that builds a bordered table grid from an <see cref="MdTable"/>
/// into a host <see cref="Border"/>'s child. Cells reuse <see cref="MarkdownInline"/>
/// so bold/links/code render inside them. Pipe tables are parsed but have no built-in
/// Avalonia control, so this renders them instead of dumping raw "| … |" text.
/// </summary>
public static class MarkdownTableView
{
    public static readonly AttachedProperty<MdTable?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Border, MdTable?>("Source", typeof(MarkdownTableView));

    public static void SetSource(Border element, MdTable? value) => element.SetValue(SourceProperty, value);
    public static MdTable? GetSource(Border element) => element.GetValue(SourceProperty);

    static MarkdownTableView()
    {
        SourceProperty.Changed.AddClassHandler<Border>((host, e) =>
            host.Child = e.NewValue is MdTable table ? Build(host, table) : null);
    }

    private static Control Build(Border host, MdTable table)
    {
        var cols = table.ColumnCount;
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        if (cols == 0) return grid;

        // Proportional (star) columns share the bubble's width, so cell text wraps
        // to fit instead of overflowing/clipping the capped bubble. A small MinWidth
        // keeps a column from collapsing to nothing when a table has many columns.
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 44 });

        var row = 0;
        if (table.Headers.Count > 0)
            AddRow(host, grid, table.Headers, cols, row++, header: true);
        foreach (var cells in table.Rows)
            AddRow(host, grid, cells, cols, row++, header: false);

        return grid;
    }

    private static void AddRow(Border host, Grid grid, IReadOnlyList<string> cells, int cols, int row, bool header)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var lastRow = false; // bottom border on every row keeps interior lines uniform
        for (var c = 0; c < cols; c++)
        {
            var text = c < cells.Count ? cells[c] : string.Empty;
            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 4)
            };
            if (header) tb.FontWeight = FontWeight.Bold;
            MarkdownInline.SetText(tb, text);
            tb.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("Fg"));

            var cell = new Border
            {
                Child = tb,
                BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, lastRow ? 0 : 1)
            };
            cell.Bind(Border.BorderBrushProperty, host.GetResourceObservable("BorderCol"));
            if (header) cell.Bind(Border.BackgroundProperty, host.GetResourceObservable("CodeBg"));

            Grid.SetColumn(cell, c);
            Grid.SetRow(cell, row);
            grid.Children.Add(cell);
        }
    }
}
