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

    /// <summary>아직 안 읽었으면 빈 목록. UI 가 동기적으로 훑을 때 쓴다.</summary>
    public IReadOnlyList<TableInfo> LoadedTables => _snapshot?.Tables ?? [];

    public static SchemaCache ForProfile(ConnectionProfile profile) => new(async ct =>
    {
        await using var conn = await profile.OpenAsync(ct);
        var tables = await SchemaCatalog.GetTablesAsync(conn, ct);
        var columns = await SchemaCatalog.GetAllColumnsAsync(conn, ct);
        return new SchemaSnapshot(tables, columns);
    });

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
