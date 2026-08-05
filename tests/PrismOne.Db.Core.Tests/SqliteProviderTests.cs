using Microsoft.Data.Sqlite;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// SQLite 는 파일 DB 라 **진짜 DB 를 띄워 검증한다.** 멀티 DB 단계에서 Oracle 보다
/// 먼저 넣은 이유가 이것이다 — 카탈로그 조회 경로가 사람 눈이 아니라 테스트로 잡힌다
/// (MULTI_DB_PLAN.md §5).
/// </summary>
public sealed class SqliteProviderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aurum-test-{Guid.NewGuid():N}.db");

    public SqliteProviderTests()
    {
        // 파일을 만들어야 하므로 여기서만 생성 모드로 연다 (provider 는 생성하지 않는다)
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE patient (
                patient_key INTEGER PRIMARY KEY,
                patient_id  TEXT NOT NULL
            );
            CREATE TABLE study (
                study_key   INTEGER PRIMARY KEY,
                patient_key INTEGER NOT NULL REFERENCES patient(patient_key),
                study_dttm  TEXT
            );
            CREATE TABLE series (
                series_key INTEGER PRIMARY KEY,
                study_key  INTEGER REFERENCES study(study_key)
            );
            CREATE TABLE study_detail (
                study_key INTEGER PRIMARY KEY REFERENCES study(study_key),
                note      TEXT
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

    private ConnectionProfile Profile => ConnectionProfile.ForFile(_path, DbKind.Sqlite);

    private Task<ErdGraph> LoadAsync() =>
        new SqliteProvider().CreateErdCatalog(Profile).LoadAsync([SqliteProvider.MainSchema]);

    // ---------- provider ----------

    [Fact]
    public void RegistryExposesSqlite()
    {
        var provider = DbProviders.For(DbKind.Sqlite);

        Assert.Equal("SQLite", provider.DisplayName);
        Assert.Contains(DbProviders.All, p => p.Kind == DbKind.PostgreSql);
    }

    [Fact]
    public void CapabilitiesSayWhatSqliteCannotDo()
    {
        var caps = DbProviders.For(DbKind.Sqlite).Capabilities;

        Assert.True(caps.ForeignKeys);
        Assert.True(caps.GridEditing);
        Assert.False(caps.IsolationLevels);
        Assert.False(caps.SessionMonitor);
        Assert.False(caps.BulkExport);
        Assert.False(caps.Schemas);
    }

    [Fact]
    public void UnsupportedKindIsReportedNotSilentlyAccepted()
    {
        // 등록된 종류가 아니면 조용히 넘어가지 않고 바로 알린다.
        // (한때 MongoDB 가 이 자리였지만 2026-08-05 에 provider 가 붙었다 —
        //  이제 네 종류가 모두 등록돼 있어 enum 밖의 값으로 확인한다)
        var unregistered = (DbKind)999;

        Assert.False(DbProviders.IsSupported(unregistered));
        Assert.Throws<NotSupportedException>(() => DbProviders.For(unregistered));
    }

    [Fact]
    public void AllKnownKindsAreRegistered()
    {
        foreach (var kind in Enum.GetValues<DbKind>())
            Assert.True(DbProviders.IsSupported(kind), $"{kind} provider 가 없습니다.");
    }

    [Fact]
    public void FileDbIsDescribedByFileName()
        => Assert.Equal(Path.GetFileName(_path), Profile.DisplayName);

    [Fact]
    public async Task OpensTheExistingFile()
    {
        await using var conn = await Profile.OpenDbAsync();

        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
    }

    [Fact]
    public async Task DoesNotCreateAMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"aurum-missing-{Guid.NewGuid():N}.db");
        var profile = ConnectionProfile.ForFile(missing, DbKind.Sqlite);

        await Assert.ThrowsAnyAsync<Exception>(() => profile.OpenDbAsync());
        Assert.False(File.Exists(missing), "없는 경로를 열었을 때 빈 DB 가 만들어지면 안 된다");
    }

    [Fact]
    public async Task PostgresOnlyPathRejectsOtherKinds()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Profile.OpenAsync());

    // ---------- 카탈로그 (실제 DB) ----------

    [Fact]
    public async Task ReadsTablesAndTheView()
    {
        var graph = await LoadAsync();

        Assert.Equal(
            ["main.patient", "main.series", "main.study", "main.study_detail", "main.v_study"],
            graph.Tables.Select(t => t.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(graph.Tables.Single(t => t.Name == "v_study").IsView);
    }

    [Fact]
    public async Task ReadsColumnsWithPkAndNullability()
    {
        var graph = await LoadAsync();
        var study = graph.Tables.Single(t => t.Name == "study");

        Assert.Equal(["study_key", "patient_key", "study_dttm"], study.Columns.Select(c => c.Name));
        Assert.True(study.Columns.Single(c => c.Name == "study_key").IsPk);
        Assert.True(study.Columns.Single(c => c.Name == "patient_key").NotNull);
        Assert.False(study.Columns.Single(c => c.Name == "study_dttm").NotNull);
    }

    [Fact]
    public async Task MarksForeignKeyColumns()
    {
        var graph = await LoadAsync();
        var study = graph.Tables.Single(t => t.Name == "study");

        Assert.True(study.Columns.Single(c => c.Name == "patient_key").IsFk);
        Assert.False(study.Columns.Single(c => c.Name == "study_dttm").IsFk);
    }

    [Fact]
    public async Task ReadsRelationsWithBothEnds()
    {
        var graph = await LoadAsync();

        var studyToPatient = graph.Relations.Single(r =>
            r.ChildKey == "main.study" && r.ParentKey == "main.patient");
        Assert.Equal(["patient_key"], studyToPatient.ChildColumns);
        Assert.Equal(["patient_key"], studyToPatient.ParentColumns);
    }

    [Fact]
    public async Task NullableForeignKeyIsOptional()
    {
        var graph = await LoadAsync();

        var seriesToStudy = graph.Relations.Single(r => r.ChildKey == "main.series");
        Assert.True(seriesToStudy.ChildOptional, "series.study_key 는 nullable 이라 0..N 이어야 한다");

        var studyToPatient = graph.Relations.Single(r => r.ChildKey == "main.study");
        Assert.False(studyToPatient.ChildOptional);
    }

    [Fact]
    public async Task ForeignKeyOnThePrimaryKeyIsOneToOne()
    {
        var graph = await LoadAsync();

        var detail = graph.Relations.Single(r => r.ChildKey == "main.study_detail");
        Assert.True(detail.ChildUnique, "study_detail.study_key 는 PK 이므로 1:1 이어야 한다");

        var series = graph.Relations.Single(r => r.ChildKey == "main.series");
        Assert.False(series.ChildUnique);
    }

    [Fact]
    public async Task SchemasAreJustMain()
    {
        var schemas = await new SqliteProvider().CreateErdCatalog(Profile).GetSchemasAsync();

        Assert.Equal(["main"], schemas);
    }

    // ---------- 쿼리 실행 경로 (QuerySession 이 드라이버 중립인지) ----------

    [Fact]
    public async Task RunsAStatementThroughQuerySession()
    {
        await using var session = await QuerySession.CreateAsync(Profile);

        await using var query = await session.ExecuteAsync("select 1 as one, 'x' as two");

        Assert.True(query.HasGrid);
        Assert.Equal(["one", "two"], query.Columns);
        var batch = await query.FetchAsync(10);
        Assert.Single(batch);
        Assert.Equal("1", batch[0].Cells[0]);
    }

    [Fact]
    public async Task CommitsThroughTheProviderTransactionStatements()
    {
        await using var session = await QuerySession.CreateAsync(Profile);

        await session.EnsureTransactionAsync();
        Assert.True(session.InTransaction);
        var affected = await session.ExecuteEditAsync(
            new EditStatement("insert into patient (patient_key, patient_id) values (1, 'P0001')", []));
        await session.CommitAsync();

        Assert.Equal(1, affected);
        Assert.False(session.InTransaction);

        await using var check = await session.ExecuteAsync("select patient_id from patient");
        var rows = await check.FetchAsync(10);
        Assert.Equal("P0001", rows[0].Cells[0]);
    }

    [Fact]
    public async Task IsolationIsSkippedWhereUnsupported()
    {
        await using var session = await QuerySession.CreateAsync(Profile);

        // SQLite 는 세션 격리 수준을 걸 수 없다 — 문장을 보내지 않고 조용히 기록만 한다
        await session.ApplyIsolationAsync(TransactionIsolation.Serializable);

        Assert.Equal(TransactionIsolation.Serializable, session.Isolation);
    }

    [Fact]
    public async Task GraphFeedsTheExistingLayoutUnchanged()
    {
        // provider 만 갈아끼우면 ERD 렌더가 그대로 동작해야 한다 — 경계가 먹는지 확인
        var diagram = ErdLayout.Compute(await LoadAsync());

        Assert.Equal(5, diagram.Boxes.Count);
        Assert.Equal(3, diagram.Edges.Count);
    }
}
