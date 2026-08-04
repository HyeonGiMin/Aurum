using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PrismOne.Db.Core.Providers;

/// <summary>
/// SQLite. 파일 DB 라 <see cref="ConnectionProfile.Database"/> 가 **파일 경로**다
/// (Host/Port/Username/Password 는 쓰지 않는다).
///
/// 멀티 DB 단계에서 Oracle 보다 먼저 넣는 이유는 기능 요구가 아니라 검증 때문이다 —
/// 파일 DB 라 단위 테스트에서 진짜 DB 를 띄울 수 있다 (MULTI_DB_PLAN.md §5).
/// </summary>
public sealed class SqliteProvider : IDbProvider
{
    /// <summary>SQLite 의 유일한 "스키마" 이름.</summary>
    public const string MainSchema = "main";

    public DbKind Kind => DbKind.Sqlite;

    public string DisplayName => "SQLite";

    public DbCapabilities Capabilities { get; } = new(
        Transactions: true,
        IsolationLevels: false,   // 격리 수준을 세션에 걸 수 없다
        GridEditing: true,        // rowid 로 행 특정
        ExplainPlan: true,        // EXPLAIN QUERY PLAN
        ServerMessages: false,    // RAISE NOTICE 같은 개념 없음
        SessionMonitor: false,    // 서버가 없다
        BulkExport: false,        // COPY 없음 — 행 단위
        ForeignKeys: true,        // PRAGMA foreign_key_list
        Schemas: false);          // 스키마 개념 없음 (main 하나)

    public string? RowIdColumn => "rowid";

    /// <summary>
    /// 없는 파일을 열면 SQLite 는 빈 DB 를 만들어버린다 — 경로 오타로 빈 DB 가 생기는
    /// 사고를 막으려고 <c>Mode=ReadWrite</c> 로 고정한다(생성하지 않음).
    /// </summary>
    public string BuildConnectionString(ConnectionProfile profile) => new SqliteConnectionStringBuilder
    {
        DataSource = profile.Database,
        Mode = profile.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
    }.ConnectionString;

    public async Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var conn = new SqliteConnection(BuildConnectionString(profile));
        await conn.OpenAsync(ct);
        return conn;
    }

    public string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"") + '"';

    /// <summary>파일 이름만 — 전체 경로는 길어서 툴바에서 잘린다.</summary>
    public string Describe(ConnectionProfile profile)
    {
        var name = Path.GetFileName(profile.Database);
        return string.IsNullOrEmpty(name) ? profile.Database : name;
    }

    public IErdCatalog CreateErdCatalog(ConnectionProfile profile) => new SqliteErdCatalog(profile);
}
