using Microsoft.Data.Sqlite;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public sealed class CsvParserTests
{
    [Fact]
    public void ParsesSimpleCsv()
        => Assert.Equal([["a", "b"], ["1", "2"]],
            CsvParser.Parse("a,b\n1,2\n", ','));

    [Fact]
    public void QuotedFieldKeepsDelimiterAndNewline()
    {
        var rows = CsvParser.Parse("name,note\n\"Kim, MD\",\"line1\nline2\"\n", ',');

        Assert.Equal("Kim, MD", rows[1][0]);
        Assert.Equal("line1\nline2", rows[1][1]);
    }

    [Fact]
    public void EscapedQuoteInsideQuotedField()
        => Assert.Equal("say \"hi\"", CsvParser.Parse("\"say \"\"hi\"\"\"", ',')[0][0]);

    [Fact]
    public void CrLfAndTrailingNewlineDoNotAddRows()
        => Assert.Equal(2, CsvParser.Parse("a,b\r\n1,2\r\n", ',').Count);

    [Fact]
    public void EmptyFieldsSurvive()
        => Assert.Equal(["1", "", "3"], CsvParser.Parse("1,,3", ',')[0]);

    [Theory]
    [InlineData("a\tb\tc\n", '\t')]
    [InlineData("a,b,c\n", ',')]
    [InlineData("a;b;c\n", ';')]
    [InlineData("plain\n", ',')]
    public void DetectsDelimiter(string text, char expected)
        => Assert.Equal(expected, CsvParser.DetectDelimiter(text));

    [Fact]
    public void DelimiterInsideQuotesDoesNotCount()
        => Assert.Equal('\t', CsvParser.DetectDelimiter("\"a,b,c\"\tx\n"));
}

public sealed class CsvImporterTests : IDisposable
{
    private static readonly List<ColumnInfo> StudyColumns =
    [
        new(1, "study_key", "INTEGER", "no", "P1", ""),
        new(2, "study_id", "TEXT", "no", "", ""),
        new(3, "modality", "TEXT", "yes", "", ""),
    ];

    // ---------- 매핑 ----------

    [Fact]
    public void HeaderMappingIsCaseInsensitiveAndReportsUnmatched()
    {
        var mapping = CsvImporter.MapByHeader(["STUDY_KEY", "bogus", "modality"], StudyColumns);

        Assert.Equal([(0, "study_key"), (2, "modality")],
            mapping.Columns.Select(m => (m.FileIndex, m.Column.Name)));
        Assert.Equal(["bogus"], mapping.UnmatchedHeaders);
    }

    [Fact]
    public void DuplicateHeaderUsesFirstOccurrence()
    {
        var mapping = CsvImporter.MapByHeader(["study_key", "study_key"], StudyColumns);

        Assert.Single(mapping.Columns);
        Assert.Equal(["study_key"], mapping.UnmatchedHeaders);
    }

    [Fact]
    public void PositionalMappingFollowsTableOrder()
    {
        var mapping = CsvImporter.MapByPosition(2, StudyColumns);

        Assert.Equal([(0, "study_key"), (1, "study_id")],
            mapping.Columns.Select(m => (m.FileIndex, m.Column.Name)));
    }

    [Fact]
    public void RowWithWrongFieldCountIsRejectedBeforeAnyInsert()
    {
        var mapping = CsvImporter.MapByHeader(["study_key", "study_id"], StudyColumns);

        var bad = CsvImporter.ValidateRows(mapping, [["1", "S1"], ["2"]]);

        Assert.Equal(2, bad!.Value.Row);
    }

    // ---------- INSERT 문 ----------

    [Fact]
    public void BuildsProviderSpecificPlaceholders()
    {
        var mapping = CsvImporter.MapByHeader(["study_key", "study_id"], StudyColumns);
        var row = new[] { "1", "S1" };

        Assert.Contains("VALUES ($1, $2)",
            CsvImporter.BuildInsert(DbProviders.For(DbKind.PostgreSql), "prismone", "study", mapping, row, true).Sql);
        Assert.Contains("VALUES (@p1, @p2)",
            CsvImporter.BuildInsert(DbProviders.For(DbKind.Sqlite), "", "study", mapping, row, true).Sql);
        Assert.Contains("VALUES (:p1, :p2)",
            CsvImporter.BuildInsert(DbProviders.For(DbKind.Oracle), "PRISMONE", "STUDY", mapping, row, true).Sql);
    }

    [Fact]
    public void EmptyBecomesNullOnlyWhenAsked()
    {
        var mapping = CsvImporter.MapByHeader(["study_key", "modality"], StudyColumns);

        var asNull = CsvImporter.BuildInsert(DbProviders.For(DbKind.Sqlite), "", "study", mapping, ["1", ""], emptyAsNull: true);
        var asEmpty = CsvImporter.BuildInsert(DbProviders.For(DbKind.Sqlite), "", "study", mapping, ["1", ""], emptyAsNull: false);

        Assert.Null(asNull.Parameters[1]);
        Assert.Equal("", asEmpty.Parameters[1]);
    }

    // ---------- 실제 DB (SQLite) — 전량 성공 아니면 전량 롤백 ----------

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aurum-import-{Guid.NewGuid():N}.db");

    public CsvImporterTests()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE study (
                study_key INTEGER PRIMARY KEY,
                study_id  TEXT NOT NULL,
                modality  TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { /* 임시 파일 */ }
    }

    private ConnectionProfile Profile => ConnectionProfile.ForFile(_path, DbKind.Sqlite);

    private async Task<long> CountAsync(QuerySession session)
    {
        await using var q = await session.ExecuteAsync("select count(*) from study");
        return long.Parse((await q.FetchAsync(1))[0].Cells[0]!);
    }

    [Fact]
    public async Task ImportsAllRowsInOneTransaction()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        var rows = CsvParser.Parse("study_key,study_id,modality\n1,S1,CT\n2,S2,\n3,S3,MR\n", ',');
        var mapping = CsvImporter.MapByHeader(rows[0], StudyColumns);

        var result = await CsvImporter.RunAsync(session, "", "study", mapping, rows.Skip(1).ToList(), emptyAsNull: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Inserted);
        Assert.False(session.InTransaction);   // 커밋까지 끝났다
        Assert.Equal(3, await CountAsync(session));

        // 빈 값 → NULL 확인
        await using var q = await session.ExecuteAsync("select modality from study where study_key = 2");
        Assert.Null((await q.FetchAsync(1))[0].Cells[0]);
    }

    [Fact]
    public async Task FailureRollsBackEverything()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        // 3번째 행이 NOT NULL(study_id) 위반 — 앞의 2행도 남으면 안 된다
        List<string[]> rows = [["1", "S1"], ["2", "S2"], ["3", null!]];   // null → NOT NULL 위반
        var mapping = CsvImporter.MapByHeader(["study_key", "study_id"], StudyColumns);

        var result = await CsvImporter.RunAsync(session, "", "study", mapping, rows, emptyAsNull: true);

        Assert.False(result.Success);
        Assert.Equal(3, result.ErrorRow);
        Assert.Equal(0, await CountAsync(session));
    }

    [Fact]
    public async Task LargeFileGoesThroughPreparedBatchPath()
    {
        // 5천 행 — 준비된 문장 재사용 경로. 수 초씩 걸리면 회귀다 (CI 감각의 느슨한 상한)
        await using var session = await QuerySession.CreateAsync(Profile);
        var rows = Enumerable.Range(1, 5_000).Select(i => new[] { i.ToString(), $"S{i}" }).ToList();
        var mapping = CsvImporter.MapByHeader(["study_key", "study_id"], StudyColumns);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await CsvImporter.RunAsync(session, "", "study", mapping, rows, emptyAsNull: true);
        watch.Stop();

        Assert.True(result.Success, result.Error);
        Assert.Equal(5_000, result.Inserted);
        Assert.Equal(5_000, await CountAsync(session));
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"5천 행에 {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task StructuralErrorTouchesNothing()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        var mapping = CsvImporter.MapByHeader(["study_key", "study_id"], StudyColumns);

        var result = await CsvImporter.RunAsync(session, "", "study", mapping,
            [["1", "S1"], ["2"]], emptyAsNull: true);

        Assert.False(result.Success);
        Assert.Equal(2, result.ErrorRow);
        Assert.Equal(0, await CountAsync(session));
    }
}
