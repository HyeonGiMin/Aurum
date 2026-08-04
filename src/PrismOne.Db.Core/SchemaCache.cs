namespace PrismOne.Db.Core;

/// <summary>카탈로그를 한 번 읽은 결과 — 테이블 목록 + (schema.table → 컬럼).</summary>
public sealed record SchemaSnapshot(
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyDictionary<string, List<ColumnInfo>> Columns)
{
    public static SchemaSnapshot Empty { get; } =
        new([], new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal));
}

/// <summary>
/// DataGrip 식 introspection 캐시. 접속당 하나 두고 테이블·컬럼을 **한 번만** 읽어
/// 이후 describe·자동완성·SQL Builder 는 메모리에서 답한다.
/// (이전에는 테이블을 고를 때마다 새 접속을 열어 컬럼을 조회했다.)
///
/// 디스크에 남기지 않는다 — 스키마와 접속 흔적을 파일로 흘리지 않기 위해서다.
/// DDL 을 실행했으면 <see cref="Invalidate"/> 로 버리고 다음 조회 때 다시 읽는다.
/// </summary>
public sealed class SchemaCache(SchemaCache.Loader load)
{
    /// <summary>실제 조회 — 테스트에서 가짜로 갈아끼울 수 있게 주입받는다.</summary>
    public delegate Task<SchemaSnapshot> Loader(CancellationToken ct);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SchemaSnapshot? _snapshot;

    /// <summary>지금까지 실제로 읽은 횟수 — 캐시가 먹는지 확인용.</summary>
    public int LoadCount { get; private set; }

    public bool IsLoaded => _snapshot is not null;

    /// <summary>읽어둔 스냅샷 — 없으면 null. SQL 검증처럼 동기로 봐야 하는 쪽이 쓴다.</summary>
    public SchemaSnapshot? Loaded => _snapshot;

    /// <summary>아직 안 읽었으면 빈 목록. UI 가 동기적으로 훑을 때 쓴다.</summary>
    public IReadOnlyList<TableInfo> LoadedTables => _snapshot?.Tables ?? [];

    public static SchemaCache ForProfile(ConnectionProfile profile) =>
        profile.Kind == Providers.DbKind.PostgreSql
            ? new SchemaCache(async ct =>
            {
                // PG 전용 경로 — 테이블 목록과 전 컬럼을 각각 한 쿼리로 읽는다
                await using var conn = await profile.OpenAsync(ct);
                var tables = await SchemaCatalog.GetTablesAsync(conn, ct);
                var columns = await SchemaCatalog.GetAllColumnsAsync(conn, ct);
                return new SchemaSnapshot(tables, columns);
            })
            : new SchemaCache(ct => FromErdCatalogAsync(profile, ct));

    /// <summary>
    /// PG 이외는 ERD 카탈로그(IErdCatalog)를 재활용한다 — 이미 테이블·컬럼·FK 를
    /// DB 중립 모델로 읽고 있어 자동완성·describe 에 그대로 쓸 수 있다.
    /// </summary>
    private static async Task<SchemaSnapshot> FromErdCatalogAsync(
        ConnectionProfile profile, CancellationToken ct)
    {
        var catalog = profile.Provider.CreateErdCatalog(profile);
        var schemas = await catalog.GetSchemasAsync(ct);
        if (schemas.Count == 0) return SchemaSnapshot.Empty;

        // 관계는 필요 없다 — Oracle 은 관계까지 읽으면 수십 초가 걸린다
        var graph = await catalog.LoadTablesAsync(schemas, ct);
        var tables = new List<TableInfo>(graph.Tables.Count);
        var columns = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);

        foreach (var table in graph.Tables)
        {
            tables.Add(new TableInfo(table.Schema, table.Name, table.IsView));
            columns[table.Key] = table.Columns
                .Select((c, i) => new ColumnInfo(
                    i + 1,
                    c.Name,
                    c.Type,
                    c.NotNull ? "no" : "yes",
                    c.IsPk ? "P1" : "",
                    c.IsFk ? "F1" : ""))
                .ToList();
        }
        return new SchemaSnapshot(tables, columns);
    }

    public async Task<SchemaSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (_snapshot is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            // 기다리는 동안 다른 호출이 채웠을 수 있다.
            if (_snapshot is { } filled) return filled;
            var loaded = await load(ct);
            LoadCount++;
            _snapshot = loaded;
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct = default)
        => (await GetAsync(ct)).Tables;

    /// <summary>컬럼 조회 — 캐시에 없는 테이블(권한·타이밍)은 빈 목록을 준다.</summary>
    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        TableInfo table, CancellationToken ct = default)
    {
        var snapshot = await GetAsync(ct);
        return snapshot.Columns.TryGetValue($"{table.Schema}.{table.Name}", out var columns)
            ? columns
            : [];
    }

    /// <summary>DDL 실행 후 등 — 다음 조회 때 다시 읽는다.</summary>
    public void Invalidate() => _snapshot = null;

    /// <summary>명시적 갱신 (Refresh).</summary>
    public Task<SchemaSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        Invalidate();
        return GetAsync(ct);
    }
}
