using Microsoft.Data.Sqlite;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// DESC 를 에디터가 타는 경로(QuerySession.ExecuteAsync → ActiveQuery)로 진짜 DB 에 돌려 본다.
/// SQLite 는 파일 DB 라 CI 에서도 돈다 — PG·Oracle 쪽은 *LiveTests 가 맡는다.
/// </summary>
public sealed class SqliteDescribeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aurum-desc-{Guid.NewGuid():N}.db");

    public SqliteDescribeTests()
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
                study_key  INTEGER PRIMARY KEY,
                patient_id TEXT NOT NULL,
                study_dttm TEXT
            );
            CREATE VIEW v_study AS SELECT study_key FROM study;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { /* 임시 파일이라 남아도 무해 */ }
    }

    private Task<QuerySession> OpenAsync() =>
        QuerySession.CreateAsync(ConnectionProfile.ForFile(_path, DbKind.Sqlite));

    private static async Task<List<string>> RowsAsync(ActiveQuery query) =>
        (await query.FetchAsync(100)).Select(r => string.Join('|', r.Cells)).ToList();

    [Fact]
    public async Task Desc_ReturnsSqlPlusColumnsInOrder()
    {
        await using var session = await OpenAsync();

        await using var query = await session.ExecuteAsync("desc study");

        Assert.Equal(["Name", "Null?", "Type"], query.Columns);
        Assert.Equal(
            ["study_key||INTEGER", "patient_id|NOT NULL|TEXT", "study_dttm||TEXT"],
            await RowsAsync(query));
    }

    [Theory]
    [InlineData("DESCRIBE STUDY;")]
    [InlineData("descr main.study")]
    [InlineData("desc \"study\" /* 메모 */")]
    public async Task Desc_AcceptsAbbreviationsSchemaAndQuotes(string sql)
    {
        await using var session = await OpenAsync();

        await using var query = await session.ExecuteAsync(sql);

        Assert.Equal(3, (await RowsAsync(query)).Count);
    }

    [Fact]
    public async Task Desc_View()
    {
        await using var session = await OpenAsync();

        await using var query = await session.ExecuteAsync("desc v_study");

        Assert.Equal(["study_key||INTEGER"], await RowsAsync(query));
    }

    [Fact]
    public async Task Desc_UnknownObject_FailsLikeOra04043()
    {
        await using var session = await OpenAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ExecuteAsync("desc no_such_table"));
        Assert.Contains("no_such_table", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public async Task Desc_SessionStaysUsableAfterward()
    {
        await using var session = await OpenAsync();
        await using (var _ = await session.ExecuteAsync("desc study")) { }

        await using var query = await session.ExecuteAsync("select count(*) from study");

        Assert.Equal(["0"], await RowsAsync(query));
    }
}
