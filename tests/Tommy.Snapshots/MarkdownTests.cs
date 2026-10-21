using System.Linq;
using Tommy.App.Rendering;
using Xunit;

namespace Tommy.Snapshots;

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
