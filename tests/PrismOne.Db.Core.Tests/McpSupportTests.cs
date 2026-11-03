using System.Text.Json;
using PrismOne.Db.Core.Mcp;

namespace PrismOne.Db.Core.Tests;

public class McpSqlPolicyTests
{
    [Theory]
    [InlineData("select * from t")]
    [InlineData("  -- comment\nSELECT 1;")]
    [InlineData("with x as (select 1) select * from x")]
    [InlineData("select 1; select 2;")]
    [InlineData("show search_path")]
    public void ReadOnlyStatements_AreReadOnly(string sql) =>
        Assert.Equal(McpSqlKind.ReadOnly, McpSqlPolicy.Classify(sql));

    [Theory]
    [InlineData("insert into t values (1)")]
    [InlineData("update t set a = 1")]
    [InlineData("select 1; delete from t;")]
    [InlineData("drop table t")]
    [InlineData("select * from t for update")]
    [InlineData("select * from t for no key update")]
    [InlineData("select * into new_t from t")]
    [InlineData("explain analyze delete from t")]
    [InlineData("with d as (delete from t returning *) select * from d")]
    [InlineData("begin dbms_output.put_line('x'); end;")]
    [InlineData("")]
    public void WritingOrUnknownStatements_AreWrite(string sql) =>
        Assert.Equal(McpSqlKind.Write, McpSqlPolicy.Classify(sql));

    // 보안 리뷰: 정책과 실행기가 문장을 다르게 나누면 승인 없이 쓰기가 통과한다
    [Theory]
    [InlineData("select 1 from dual\n/\ndelete from t")]              // SQL*Plus '/' 종결자 (Oracle 세션만 끊는다)
    [InlineData("select $q$; delete from t; $q$ as x from dual")]    // PG 달러 인용 — Oracle 은 '$' 를 인용으로 안 본다
    [InlineData("select * from t for/**/update")]                     // 주석으로 키워드 사이를 띄움
    [InlineData("select * from t for --x\nupdate")]
    [InlineData("select nextval('seq')")]                             // 트랜잭션 밖에서 효과가 남는 함수
    [InlineData("select pg_advisory_lock(1)")]
    [InlineData("select pg_terminate_backend(123)")]
    [InlineData("select set_config('search_path', 'x', false)")]
    [InlineData("select dbms_lock.sleep(5) from dual")]
    [InlineData("select utl_http.request('http://x') from dual")]
    [InlineData("select q'[a']' from t for update --'")]               // Oracle q-인용: 단순 '…' 짝맞춤이면 FOR UPDATE 가 가려진다
    [InlineData("select nq'[a']' from t for update --'")]
    [InlineData("select $$ -- $$ from t for update")]                   // PG 달러 인용 안의 -- 는 주석이 아니다
    [InlineData("select \"--\" from t for update")]                     // 인용 식별자 안의 -- 도 주석이 아니다
    [InlineData("select \"nextval\"('seq')")]                          // 인용으로 함수명을 감싸도 잡는다
    [InlineData("select U&\"nextv\\0061l\"('seq')")]                   // 유니코드 이스케이프로 이름 숨기기
    public void SplitterAndSideEffectBypasses_AreWrite(string sql) =>
        Assert.Equal(McpSqlKind.Write, McpSqlPolicy.Classify(sql));

    [Theory]
    [InlineData("select * from t where note = 'for update'")]         // 문자열 안의 키워드는 무시
    [InlineData("select 'insert into' as x from t")]
    [InlineData("select count(*) from t -- for update")]
    [InlineData("select 1 from dual\n/\nselect 2 from dual")]
    public void KeywordsInsideLiteralsOrComments_StayReadOnly(string sql) =>
        Assert.Equal(McpSqlKind.ReadOnly, McpSqlPolicy.Classify(sql));
}

public class McpAuditLogTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "prismone-tests", $"mcp-audit-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Append_WritesOneJsonLinePerEntry()
    {
        // Arrange
        var log = new McpAuditLog(_path);
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        // Act
        log.Append(new McpAuditEntry(at, "run_query", 2, "select 1", "auto", 1));
        log.Append(new McpAuditEntry(at, "run_query", 2, "delete from t", "declined", null));

        // Assert
        var lines = File.ReadAllLines(_path);
        Assert.Equal(2, lines.Length);
        var first = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("2026-01-02T03:04:05+00:00", first.GetProperty("At").GetString());
        Assert.Equal("auto", first.GetProperty("Decision").GetString());
        Assert.Equal(1, first.GetProperty("RowsReturned").GetInt32());
        Assert.Equal("declined", JsonDocument.Parse(lines[1]).RootElement.GetProperty("Decision").GetString());
    }
}

public class McpResultTextTests
{
    private static readonly string[] Columns = ["id", "name"];

    [Fact]
    public void Page_ReturnsRequestedSliceWithTotals()
    {
        // Arrange
        var rows = Enumerable.Range(1, 10).Select(i => new string?[] { i.ToString(), $"n{i}" }).ToList();

        // Act
        var json = JsonDocument.Parse(McpResultText.Page(Columns, rows, offset: 2, limit: 3)).RootElement;

        // Assert
        Assert.Equal(10, json.GetProperty("totalLoaded").GetInt32());
        Assert.Equal(2, json.GetProperty("offset").GetInt32());
        Assert.Equal(3, json.GetProperty("returned").GetInt32());
        Assert.True(json.GetProperty("more").GetBoolean());
        Assert.Equal("3", json.GetProperty("rows")[0][0].GetString());
        Assert.Equal("name", json.GetProperty("columns")[1].GetString());
    }

    [Fact]
    public void Page_KeepsNullDistinctFromEmpty()
    {
        var rows = new List<string?[]> { new string?[] { null, "" } };

        var json = JsonDocument.Parse(McpResultText.Page(Columns, rows, 0, 10)).RootElement;

        Assert.Equal(JsonValueKind.Null, json.GetProperty("rows")[0][0].ValueKind);
        Assert.Equal("", json.GetProperty("rows")[0][1].GetString());
        Assert.False(json.GetProperty("more").GetBoolean());
    }

    [Fact]
    public void Page_TruncatesLongCells()
    {
        var rows = new List<string?[]> { new string?[] { "1", new string('x', McpResultText.MaxCellLength + 50) } };

        var json = JsonDocument.Parse(McpResultText.Page(Columns, rows, 0, 10)).RootElement;

        var cell = json.GetProperty("rows")[0][1].GetString()!;
        Assert.StartsWith(new string('x', McpResultText.MaxCellLength), cell);
        Assert.EndsWith("…(+50 chars)", cell);
    }

    [Fact]
    public void Page_KeepsKoreanAndQuotesReadable()
    {
        // AI 가 읽는 텍스트라 \uXXXX 로 이스케이프되면 한글 데이터를 못 읽는다
        var rows = new List<string?[]> { new string?[] { "1", "환자 \"홍길동\"" } };

        var text = McpResultText.Page(Columns, rows, 0, 10);

        Assert.Contains("환자", text);
        Assert.DoesNotContain("\\u", text);
    }

    [Fact]
    public void Page_OffsetPastEnd_ReturnsEmpty()
    {
        var rows = new List<string?[]> { new string?[] { "1", "a" } };

        var json = JsonDocument.Parse(McpResultText.Page(Columns, rows, 5, 10)).RootElement;

        Assert.Equal(0, json.GetProperty("returned").GetInt32());
    }
}

public class McpSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "prismone-tests", $"mcp-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_MissingFile_GivesDisabledDefaults()
    {
        var settings = McpSettings.Load(_path);

        Assert.False(settings.Enabled);
        Assert.Equal(McpSettings.DefaultPort, settings.Port);
        Assert.Null(settings.ProtectedToken);
    }

    [Fact]
    public void WithNewToken_StoresTokenEncryptedAndRoundTrips()
    {
        // Act
        var settings = McpSettings.Load(_path).WithNewToken();
        settings.Save(_path);
        var reloaded = McpSettings.Load(_path);

        // Assert
        Assert.StartsWith("enc:v1:", reloaded.ProtectedToken);
        Assert.Equal(settings.Token, reloaded.Token);
        Assert.True(reloaded.Token!.Length >= 32);
        Assert.DoesNotContain(reloaded.Token, File.ReadAllText(_path));
    }

    [Fact]
    public void WithNewToken_ChangesTheToken()
    {
        var first = McpSettings.Load(_path).WithNewToken();
        var second = first.WithNewToken();

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public void ClaudeAddCommand_UsesHttpTransportAndBearerHeader()
    {
        var settings = new McpSettings { Port = 47901 };

        var command = settings.ClaudeAddCommand("tok123");

        Assert.Equal(
            "claude mcp add --transport http aurum http://localhost:47901/mcp --header \"Authorization: Bearer tok123\"",
            command);
    }
}
