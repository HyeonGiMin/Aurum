using System.Text.Json;
using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class GridExporterTests
{
    private static readonly string[] Columns = ["study_key", "study_id", "note"];
    private static readonly string?[][] Rows =
    [
        ["1001", "ST-0007", "it's fine"],
        ["1002", "ST-0008", null],
        ["1003", "with,comma", "line1\nline2"],
    ];

    [Fact]
    public void Csv_QuotesCommasQuotesNewlines()
    {
        var csv = GridExporter.Build(GridExportFormat.Csv, Columns, Rows);
        Assert.Contains("study_key,study_id,note", csv);
        Assert.Contains("\"with,comma\"", csv);
        Assert.Contains("\"line1\nline2\"", csv);
    }

    [Fact]
    public void Tsv_FlattensTabsAndNewlines()
    {
        var tsv = GridExporter.Build(GridExportFormat.Tsv, Columns, Rows);
        Assert.Contains("study_key\tstudy_id\tnote", tsv);
        Assert.Contains("line1 line2", tsv);
        Assert.DoesNotContain("\"with,comma\"", tsv);   // TSV 는 인용하지 않는다
    }

    [Fact]
    public void Insert_EscapesQuotesAndKeepsNumbersAndNull()
    {
        var sql = GridExporter.Build(GridExportFormat.Insert, Columns, Rows, "prismone.study");
        Assert.Contains("INSERT INTO prismone.study (study_key, study_id, note) VALUES (1001, 'ST-0007', 'it''s fine');", sql);
        Assert.Contains("VALUES (1002, 'ST-0008', NULL);", sql);
    }

    [Fact]
    public void Insert_BlankLineOptionAddsSeparator()
    {
        var sql = GridExporter.Build(GridExportFormat.Insert, Columns, Rows, "t", blankLineBetweenStatements: true);
        Assert.Contains(");\n\n", sql.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Json_ProducesArrayOfObjects_OneRowPerElement()
    {
        var json = GridExporter.Build(GridExportFormat.Json, Columns, Rows);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
        Assert.Equal("1001", doc.RootElement[0].GetProperty("study_key").GetString());
    }

    [Fact]
    public void Json_RepresentsNullCells_AsJsonNull()
    {
        var json = GridExporter.Build(GridExportFormat.Json, Columns, Rows);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("note").ValueKind);
    }

    [Fact]
    public void Json_EscapesCommasAndNewlines_ViaStandardJsonEncoding()
    {
        var json = GridExporter.Build(GridExportFormat.Json, Columns, Rows);
        using var doc = JsonDocument.Parse(json);   // 파싱 자체가 이스케이프 정확성 검증

        Assert.Equal("with,comma", doc.RootElement[2].GetProperty("study_id").GetString());
        Assert.Equal("line1\nline2", doc.RootElement[2].GetProperty("note").GetString());
    }
}
