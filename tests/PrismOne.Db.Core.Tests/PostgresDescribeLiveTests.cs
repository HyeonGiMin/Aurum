using System;
using System.Threading.Tasks;
using Npgsql;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// DESC 가 실제 PG 에서 SQL*Plus 모양으로 나오는지 실서버 검증.
/// <c>AURUM_PG_TEST_HOST</c> 가 있을 때만 돈다 (없으면 조용히 통과 — OracleSessionLiveTests 와 동일 패턴).
/// 임시 테이블만 쓰므로 대상 DB 에 흔적이 남지 않는다.
///
/// 로컬에서 돌리는 법 (PowerShell):
///   $env:AURUM_PG_TEST_HOST = "localhost"
///   $env:AURUM_PG_TEST_PORT = "5432"
///   $env:AURUM_PG_TEST_DATABASE = "postgres"
///   $env:AURUM_PG_TEST_USER = "postgres"
///   $env:AURUM_PG_TEST_PASSWORD = "..."
/// </summary>
public class PostgresDescribeLiveTests
{
    private static string? Host => Environment.GetEnvironmentVariable("AURUM_PG_TEST_HOST");

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("AURUM_PG_TEST_PORT"), out var p) ? p : 5432;

    private static ConnectionProfile Profile => new(
        Host!,
        Port,
        Environment.GetEnvironmentVariable("AURUM_PG_TEST_DATABASE") ?? "postgres",
        Environment.GetEnvironmentVariable("AURUM_PG_TEST_USER") ?? "",
        Environment.GetEnvironmentVariable("AURUM_PG_TEST_PASSWORD") ?? "",
        Kind: DbKind.PostgreSql);

    [Fact]
    public async Task Desc_ReturnsNameNullTypeInColumnOrder()
    {
        if (Host is null) return;

        await using var session = await QuerySession.CreateAsync(Profile);
        await using (var create = await session.ExecuteAsync(
            "create temp table aurum_desc_t (id integer not null, \"Label\" varchar(20), seen timestamp)"))
        { }

        // Oracle 습관대로 대문자로 쳐도 PG 의 소문자 이름을 찾아야 한다
        await using var query = await session.ExecuteAsync("DESC AURUM_DESC_T");
        var rows = await query.FetchAsync(100);

        Assert.Equal(["Name", "Null?", "Type"], query.Columns);
        Assert.Equal(3, rows.Count);
        Assert.Equal("id|NOT NULL|integer", string.Join('|', rows[0].Cells));
        Assert.Equal("Label||character varying(20)", string.Join('|', rows[1].Cells));
        Assert.Equal("seen||timestamp without time zone", string.Join('|', rows[2].Cells));
    }

    [Fact]
    public async Task Desc_UnknownRelation_FailsLikeOra04043()
    {
        if (Host is null) return;

        await using var session = await QuerySession.CreateAsync(Profile);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => session.ExecuteAsync("desc aurum_no_such_table"));
        Assert.Equal(PostgresErrorCodes.UndefinedTable, ex.SqlState);
    }
}
