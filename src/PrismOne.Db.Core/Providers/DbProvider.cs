using System.Data.Common;

namespace PrismOne.Db.Core.Providers;

/// <summary>접속 대상 DB 종류. 저장된 접속 항목에 남으므로 이름을 바꾸지 말 것.</summary>
public enum DbKind
{
    PostgreSql = 0,
    Sqlite = 1,
    Oracle = 2,
    MongoDb = 3,
}

/// <summary>
/// DB 가 실제로 할 수 있는 것. UI 는 이걸 보고 없는 기능을 비활성화한다 —
/// "버튼은 있는데 안 되는" 상태를 만들지 않기 위해서다 (MULTI_DB_PLAN.md §3).
/// </summary>
/// <param name="GridEditing">행을 특정할 의사 컬럼이 있는가 (ctid/rowid/ROWID).</param>
/// <param name="ServerMessages">실행 중 서버 메시지 (PG RAISE NOTICE, Oracle DBMS_OUTPUT).</param>
/// <param name="BulkExport">서버 측 대량 내보내기 (PG COPY). 없으면 행 단위로 떨어진다.</param>
/// <param name="ForeignKeys">FK 를 카탈로그에서 읽을 수 있는가 (ERD 관계선).</param>
/// <param name="Schemas">스키마(네임스페이스) 개념이 있는가. SQLite 는 없다.</param>
public sealed record DbCapabilities(
    bool Transactions,
    bool IsolationLevels,
    bool GridEditing,
    bool ExplainPlan,
    bool ServerMessages,
    bool SessionMonitor,
    bool BulkExport,
    bool ForeignKeys,
    bool Schemas);

/// <summary>
/// DB 별 차이를 한 곳에 모으는 경계. PG 전용 코드가 더 굳기 전에 세운다
/// (MULTI_DB_PLAN.md 0단계). 새 DB 는 이 인터페이스만 구현하면 된다.
/// </summary>
public interface IDbProvider
{
    DbKind Kind { get; }

    /// <summary>로그온 창 등에 보일 이름.</summary>
    string DisplayName { get; }

    DbCapabilities Capabilities { get; }

    /// <summary>
    /// 세션에 실제로 걸 수 있는 격리 수준. Capabilities.IsolationLevels 가 false 면 빈 목록.
    /// DB 마다 지원 범위가 달라서(Oracle 은 RC/Serializable 만) 불리언 하나로는 부족하다 —
    /// 드롭다운에 안 되는 항목을 띄우지 않기 위한 것.
    /// </summary>
    IReadOnlyList<TransactionIsolation> SupportedIsolations { get; }

    /// <summary>행 특정용 의사 컬럼. 없으면 null (그리드 편집 불가).</summary>
    string? RowIdColumn { get; }

    /// <summary>
    /// 명시적으로 트랜잭션을 여는 문장. Oracle 은 DML 이 암시적으로 열고 BEGIN 은
    /// PL/SQL 블록 시작이라 보내면 안 되므로 null 이다.
    /// </summary>
    string? BeginTransactionSql { get; }

    /// <summary>
    /// 세션 격리 수준을 거는 문장. 지원하지 않거나 해당 수준이 없으면 null.
    /// enum 에서 파생되므로 사용자 입력이 문장에 섞이지 않는다.
    /// </summary>
    string? SessionIsolationSql(TransactionIsolation level);

    string BuildConnectionString(ConnectionProfile profile);

    /// <summary>ADO.NET 공통 타입으로 연다 — 호출부가 드라이버를 몰라도 되게.</summary>
    Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct = default);

    /// <summary>식별자 인용. 필요할 때만 감싼다.</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>
    /// n번째(1부터) 파라미터 자리표시자 — PG <c>$1</c>, SQLite <c>?</c>, Oracle <c>:p1</c>.
    /// 값은 이름 없이 **추가한 순서대로** 바인딩한다 (ExecuteEditAsync 경로).
    /// </summary>
    string ParameterPlaceholder(int oneBasedIndex);

    /// <summary>상태바·창 제목용 짧은 표기.</summary>
    string Describe(ConnectionProfile profile);

    /// <summary>ERD 카탈로그. FK 를 못 읽는 DB 는 관계 없는 그래프를 준다.</summary>
    IErdCatalog CreateErdCatalog(ConnectionProfile profile);
}

/// <summary>구현체 레지스트리.</summary>
public static class DbProviders
{
    private static readonly IDbProvider[] Registered =
    [
        new PostgresProvider(),
        new OracleProvider(),
        new SqliteProvider(),
        new MongoProvider(),
    ];

    /// <summary>지금 붙일 수 있는 DB 들 (로그온 창 목록).</summary>
    public static IReadOnlyList<IDbProvider> All => Registered;

    public static IDbProvider For(DbKind kind) =>
        Registered.FirstOrDefault(p => p.Kind == kind)
        ?? throw new NotSupportedException($"{kind} 는 아직 지원하지 않습니다.");

    public static IDbProvider For(ConnectionProfile profile) => For(profile.Kind);

    public static bool IsSupported(DbKind kind) => Registered.Any(p => p.Kind == kind);
}
