using Microsoft.Data.Sqlite;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// 점진 fetch(ActiveQuery) 회귀 테스트 — "실행 즉시 전체 fetch + 5만 행 안전 상한"이
/// 기본이 되면서(02d8d78) fetch 경로가 핵심 경로가 됐다. UI 의 FetchUntilDoneAsync 는
/// "진행이 없으면 멈춘다"는 약속에 기대므로, 여기서 그 전제(완료 후 빈 배치 반환,
/// lookahead 무손실, 배치 경계에서의 Completed 판정)를 실제 DB(SQLite)로 못박는다.
/// </summary>
public sealed class ActiveQueryFetchTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aurum-fetch-{Guid.NewGuid():N}.db");

    public ActiveQueryFetchTests()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (n INTEGER PRIMARY KEY)";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { /* 임시 파일 */ }
    }

    private ConnectionProfile Profile => ConnectionProfile.ForFile(_path, DbKind.Sqlite);

    /// <summary>1..count 를 돌려주는 SELECT (재귀 CTE — 테이블 적재 없이 대량 행 생성).</summary>
    private static string Rows(int count) =>
        $"WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM cnt WHERE x < {count}) " +
        "SELECT x FROM cnt";

    [Fact]
    public async Task ExactBatchBoundaryCompletesWithoutExtraFetch()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        await using var query = await session.ExecuteAsync(Rows(500));

        var batch = await query.FetchAsync(500);

        Assert.Equal(500, batch.Count);
        // 행 수 = 배치 크기일 때 lookahead 가 끝을 확인해야 한다 — 아니면 UI 는
        // "(more)" 를 띄우고 한 번 더 fetch 를 돌게 된다
        Assert.True(query.Completed);
    }

    [Fact]
    public async Task LookaheadRowIsNeitherLostNorDuplicated()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        await using var query = await session.ExecuteAsync(Rows(501));

        var first = await query.FetchAsync(500);
        Assert.Equal(500, first.Count);
        Assert.False(query.Completed);

        var second = await query.FetchAsync(500);
        Assert.Single(second);
        Assert.True(query.Completed);

        // lookahead 로 미리 읽은 501번째 행이 그대로 이어져야 한다
        Assert.Equal("500", first[^1].Cells[0]);
        Assert.Equal("501", second[0].Cells[0]);
    }

    [Fact]
    public async Task FetchAfterCompletionReturnsEmptyForever()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        await using var query = await session.ExecuteAsync(Rows(3));

        await query.FetchAsync(10);

        // FetchUntilDoneAsync 는 "행이 안 늘면 종료"로 무한 루프를 피한다 —
        // 완료된 쿼리는 몇 번을 불러도 조용히 빈 배치를 줘야 그 약속이 성립한다
        Assert.Empty(await query.FetchAsync(10));
        Assert.Empty(await query.FetchAsync(10));
    }

    [Fact]
    public async Task SafetyCapScenarioLeavesReaderOpenAndAbortable()
    {
        // 상한(5만)보다 큰 결과를 UI 와 같은 방식(배치 500)으로 상한까지만 가져온다.
        // 상한 도달 시 reader 는 열린 채 남는데, 그 상태에서 Abort 가 깨끗해야
        // 다음 문장 실행이 가능하다 (공유 세션: 접속 하나에 reader 하나)
        await using var session = await QuerySession.CreateAsync(Profile);
        var query = await session.ExecuteAsync(Rows(AppOptions.FetchAllSafetyCap + 1_000));

        var fetched = 0;
        while (fetched < AppOptions.FetchAllSafetyCap)
        {
            var want = Math.Min(500, AppOptions.FetchAllSafetyCap - fetched);
            var batch = await query.FetchAsync(want);
            if (batch.Count == 0) break;
            fetched += batch.Count;
        }

        Assert.Equal(AppOptions.FetchAllSafetyCap, fetched);
        Assert.False(query.Completed);

        // 새 문장을 실행하면 이전 결과가 닫히고(공유 세션 시맨틱) 정상 동작해야 한다
        await using var next = await session.ExecuteAsync("select 42");
        Assert.True(query.Completed);
        Assert.Equal("42", (await next.FetchAsync(1))[0].Cells[0]);
    }

    [Fact]
    public async Task CancelledFetchThrowsAndSessionRecovers()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        var query = await session.ExecuteAsync(Rows(1_000));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => query.FetchAsync(500, cts.Token));

        // UI 의 RecoverAsync 경로 — Abort 후 세션이 살아 있으면 그대로 재사용한다
        await query.AbortAsync();
        await session.EnsureAliveAsync();
        await using var next = await session.ExecuteAsync("select 1");
        Assert.Single(await next.FetchAsync(10));
    }

    [Fact]
    public async Task DoubleAbortIsSafe()
    {
        await using var session = await QuerySession.CreateAsync(Profile);
        var query = await session.ExecuteAsync(Rows(100));

        await query.AbortAsync();
        await query.AbortAsync();   // Dispose 경로에서 한 번 더 불린다

        Assert.True(query.Completed);
    }
}
