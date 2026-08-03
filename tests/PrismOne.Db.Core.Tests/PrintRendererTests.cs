using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class PrintRendererTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 8, 3, 14, 5, 0, TimeSpan.FromHours(9));

    [Fact]
    public void SqlPageKeepsTextAndStampsHeader()
    {
        var html = PrintRenderer.RenderSql(
            "select 1;\n  select 2;", "study.sql", "prismone@localhost/prismone", Stamp, auto: false);

        Assert.Contains("<pre class=\"sql\">select 1;\n  select 2;</pre>", html);
        Assert.Contains("prismone@localhost/prismone · 2026-08-03 14:05:00", html);
        Assert.Contains("<title>study.sql</title>", html);
    }

    [Fact]
    public void AutoPrintOnlyInjectsScriptWhenRequested()
    {
        var print = PrintRenderer.RenderSql("select 1", "t", "s", Stamp, auto: true);
        var preview = PrintRenderer.RenderSql("select 1", "t", "s", Stamp, auto: false);

        Assert.Contains("window.print()", print);
        Assert.DoesNotContain("window.print()", preview);
    }

    [Fact]
    public void GridPageRendersHeaderRowNumbersAndNulls()
    {
        string[] columns = ["study_key", "note"];
        List<string?[]> rows = [["1001", null], ["1002", "ok"]];

        var html = PrintRenderer.RenderGrid(columns, rows, "Result", "prismone", Stamp, auto: false);

        Assert.Contains("<th>study_key</th><th>note</th>", html);
        Assert.Contains("<td class=\"num\">1</td><td>1001</td><td class=\"null\">NULL</td>", html);
        Assert.Contains("2 record(s)", html);
    }

    [Fact]
    public void EscapesMarkupSoCellValuesCannotBreakThePage()
    {
        string[] columns = ["a<b>"];
        List<string?[]> rows = [["<script>alert(1)</script>"]];

        var html = PrintRenderer.RenderGrid(columns, rows, "R&D", "s", Stamp, auto: false);

        Assert.Contains("a&lt;b&gt;", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<title>R&amp;D</title>", html);
    }

    [Fact]
    public void ShortRowsDoNotThrowWhenColumnsAreWider()
    {
        string[] columns = ["a", "b", "c"];
        List<string?[]> rows = [["1"]];

        var html = PrintRenderer.RenderGrid(columns, rows, "t", "s", Stamp, auto: false);

        Assert.Contains("1 record(s)", html);
    }
}
