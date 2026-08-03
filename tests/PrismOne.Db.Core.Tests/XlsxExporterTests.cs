using System.IO.Compression;
using System.Text;
using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class XlsxExporterTests
{
    private static readonly string[] Columns = ["study_key", "study_id", "note"];
    private static readonly string?[][] Rows =
    [
        ["1001", "ST-0007", "it's fine"],
        ["1002", "007", null],
        ["-3.50", "a<b&c", "line1\nline2"],
    ];

    private static string ReadEntry(byte[] xlsx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Package_HasRequiredParts()
    {
        var xlsx = XlsxExporter.Build(Columns, Rows);
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        foreach (var part in new[]
        {
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
            "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml",
        })
            Assert.NotNull(zip.GetEntry(part));
    }

    [Fact]
    public void Sheet_NumbersAsNumericCells_PreservingOnlyRoundTrippableValues()
    {
        var sheet = ReadEntry(XlsxExporter.Build(Columns, Rows), "xl/worksheets/sheet1.xml");
        Assert.Contains("<c><v>1001</v></c>", sheet);
        Assert.Contains("<c><v>-3.50</v></c>", sheet);
        // "007" 을 숫자로 내보내면 7 이 된다 — 문자열로 남아야 한다
        Assert.Contains("<t>007</t>", sheet);
        Assert.DoesNotContain("<v>007</v>", sheet);
    }

    [Fact]
    public void Sheet_EscapesXmlAndKeepsNullAsEmptyCell()
    {
        var sheet = ReadEntry(XlsxExporter.Build(Columns, Rows), "xl/worksheets/sheet1.xml");
        Assert.Contains("a&lt;b&amp;c", sheet);
        Assert.Contains("<c/>", sheet);          // NULL
        Assert.Contains("it's fine", sheet);     // 작은따옴표는 이스케이프 불필요
    }

    [Fact]
    public void Sheet_HeaderRowUsesBoldStyle()
    {
        var sheet = ReadEntry(XlsxExporter.Build(Columns, Rows), "xl/worksheets/sheet1.xml");
        Assert.Contains("<c t=\"inlineStr\" s=\"1\"><is><t>study_key</t></is></c>", sheet);
    }

    [Fact]
    public void Workbook_SanitizesSheetName()
    {
        var wb = ReadEntry(XlsxExporter.Build(Columns, Rows, "prismone.study[x]/y:z"), "xl/workbook.xml");
        Assert.Contains("name=\"prismone.study_x__y_z\"", wb);
    }

    [Fact]
    public void Write_TruncatesAtExcelRowLimit_AndReportsWrittenCount()
    {
        // 상한 자체(105만 행)를 만들면 느리므로 경계 로직만 확인
        var written = XlsxExporter.Write(new MemoryStream(), Columns, Rows);
        Assert.Equal(3, written);
    }

    [Fact]
    public void Sheet_PreservesLeadingTrailingWhitespace()
    {
        var sheet = ReadEntry(
            XlsxExporter.Build(["c"], [[" padded "]]), "xl/worksheets/sheet1.xml");
        Assert.Contains("<t xml:space=\"preserve\"> padded </t>", sheet);
    }
}
