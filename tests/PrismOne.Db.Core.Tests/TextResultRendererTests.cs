using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class TextResultRendererTests
{
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void NoColumnsSaysSo()
        => Assert.Equal("No columns.", TextResultRenderer.Render([], []));

    [Fact]
    public void AlignsColumnsToTheWidestValue()
    {
        var text = TextResultRenderer.Render(
            ["id", "modality"],
            [["1", "CT"], ["1000", "MR"]]);

        var lines = Lines(text);
        Assert.Equal("id   modality", lines[0]);
        Assert.Equal("---- --------", lines[1]);
        Assert.Equal("1    CT", lines[2]);
        Assert.Equal("1000 MR", lines[3]);
    }

    [Fact]
    public void NullIsShownDistinctlyFromBlank()
    {
        var text = TextResultRenderer.Render(["a", "b"], [[null, ""]]);

        Assert.Contains(TextResultRenderer.NullText, Lines(text)[2]);
    }

    [Fact]
    public void OverlongValuesAreTruncatedWithEllipsis()
    {
        var text = TextResultRenderer.Render(["note"], [[new string('x', 40)]], maxColumnWidth: 10);

        var row = Lines(text)[2];
        Assert.Equal(10, row.Length);
        Assert.EndsWith("…", row);
    }

    [Fact]
    public void ReportsRowCount()
    {
        var text = TextResultRenderer.Render(["a"], [["1"], ["2"]]);

        Assert.EndsWith("2 row(s)", text);
    }

    [Fact]
    public void EmptyResultStillShowsHeaderAndSaysNoRows()
    {
        var text = TextResultRenderer.Render(["study_key", "modality"], []);

        var lines = Lines(text);
        Assert.Equal("study_key modality", lines[0]);
        Assert.EndsWith("no rows", text);
    }

    [Fact]
    public void ShortRowsArePaddedInsteadOfThrowing()
    {
        var text = TextResultRenderer.Render(["a", "b"], [["1"]]);

        Assert.Contains(TextResultRenderer.NullText, Lines(text)[2]);
    }
}
