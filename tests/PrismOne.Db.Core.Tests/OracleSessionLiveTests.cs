using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// 실서버 검증. Oracle 은 SQLite 와 달리 파일 DB 가 아니라 인스턴스가 필요하므로
/// <c>AURUM_ORACLE_TEST_HOST</c> 가 있을 때만 돈다 (없으면 조용히 통과) —
/// 접속 정보를 코드에 박지 않기 위한 것이다 (MongoSessionLiveTests 와 동일 패턴).
///
/// 로컬에서 돌리는 법 (PowerShell):
///   $env:AURUM_ORACLE_TEST_HOST = "10.0.6.135"
///   $env:AURUM_ORACLE_TEST_PORT = "1521"
///   $env:AURUM_ORACLE_TEST_SERVICE = "prismone"
///   $env:AURUM_ORACLE_TEST_USER = "prismone"
///   $env:AURUM_ORACLE_TEST_PASSWORD = "..."
/// </summary>
public class OracleSessionLiveTests
{
    private static string? Host => Environment.GetEnvironmentVariable("AURUM_ORACLE_TEST_HOST");

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("AURUM_ORACLE_TEST_PORT"), out var p) ? p : 1521;

    private static ConnectionProfile Profile => new(
        Host!,
        Port,
        Environment.GetEnvironmentVariable("AURUM_ORACLE_TEST_SERVICE") ?? "",
        Environment.GetEnvironmentVariable("AURUM_ORACLE_TEST_USER") ?? "",
        Environment.GetEnvironmentVariable("AURUM_ORACLE_TEST_PASSWORD") ?? "",
        Kind: DbKind.Oracle);

    [Fact]
    public async Task ExecuteAsync_PlSqlBlock_DeliversDbmsOutputViaNoticeReceived()
    {
        if (Host is null) return;

        await using var session = await QuerySession.CreateAsync(Profile);
        var received = new List<string>();
        session.NoticeReceived += received.Add;

        await session.ExecuteAsync(
            "begin\n  dbms_output.put_line('aurum-test-1');\n  dbms_output.put_line('aurum-test-2');\nend;");

        Assert.Equal(["aurum-test-1", "aurum-test-2"], received);
    }

    [Fact]
    public async Task ExecuteAsync_PlainSelect_DoesNotInvokeNoticeReceived()
    {
        if (Host is null) return;

        await using var session = await QuerySession.CreateAsync(Profile);
        var received = new List<string>();
        session.NoticeReceived += received.Add;

        await session.ExecuteAsync("select 1 from dual");

        Assert.Empty(received);
    }

    [Fact]
    public async Task GetOracleCompileErrorsAsync_AfterBadProcedure_ReturnsUserErrorsRows()
    {
        if (Host is null) return;

        await using var session = await QuerySession.CreateAsync(Profile);
        const string procName = "aurum_live_test_bad_proc";
        try
        {
            await session.ExecuteAsync(
                $"create or replace procedure {procName} as\nbegin\n  this_does_not_exist_call();\nend;");

            var errors = await session.GetOracleCompileErrorsAsync(procName, "PROCEDURE");

            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Text.Contains("PLS-00201"));
        }
        finally
        {
            await session.ExecuteAsync($"drop procedure {procName}");
        }
    }

    [Fact]
    public async Task GetRoutinesAsync_ListsCreatedProcedure()
    {
        if (Host is null) return;

        await using var session = await QuerySession.CreateAsync(Profile);
        var catalog = (OracleErdCatalog)Profile.Provider.CreateErdCatalog(Profile);
        const string procName = "aurum_live_test_routine";
        var owner = Environment.GetEnvironmentVariable("AURUM_ORACLE_TEST_USER")!.ToUpperInvariant();
        try
        {
            await session.ExecuteAsync(
                $"create or replace procedure {procName} as\nbegin\n  null;\nend;");

            var routines = await catalog.GetRoutinesAsync([owner]);

            Assert.Contains(routines, r =>
                r.Name.Equals(procName, StringComparison.OrdinalIgnoreCase) && r.ObjectType == "PROCEDURE");
        }
        finally
        {
            await session.ExecuteAsync($"drop procedure {procName}");
        }
    }

    [Fact]
    public async Task GetSourceAsync_ReturnsExecutableCreateOrReplace()
    {
        if (Host is null) return;

        await using var session = await QuerySession.CreateAsync(Profile);
        var catalog = (OracleErdCatalog)Profile.Provider.CreateErdCatalog(Profile);
        const string procName = "aurum_live_test_source";
        var owner = Environment.GetEnvironmentVariable("AURUM_ORACLE_TEST_USER")!.ToUpperInvariant();
        try
        {
            await session.ExecuteAsync(
                $"create or replace procedure {procName} as\nbegin\n  dbms_output.put_line('x');\nend;");

            // 소문자로 넘겨도(대소문자 정규화 없으면 조용히 빈 결과가 되던 버그의 회귀 테스트)
            var source = await catalog.GetSourceAsync(owner.ToLowerInvariant(), procName.ToLowerInvariant(), "procedure");

            Assert.StartsWith("CREATE OR REPLACE", source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(procName, source, StringComparison.OrdinalIgnoreCase);

            // 되돌아온 소스가 실제로 재컴파일 가능해야 진짜 쓸모가 있다
            await session.ExecuteAsync(source);
        }
        finally
        {
            await session.ExecuteAsync($"drop procedure {procName}");
        }
    }
}
