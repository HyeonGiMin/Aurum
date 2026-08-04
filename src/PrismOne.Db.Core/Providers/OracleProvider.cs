using System.Data.Common;
using Oracle.ManagedDataAccess.Client;

namespace PrismOne.Db.Core.Providers;

/// <summary>
/// Oracle. 100% 매니지드 드라이버라 Oracle Client 설치가 필요 없다.
/// <see cref="ConnectionProfile.Database"/> 는 **서비스 이름**으로 쓴다
/// (DataSource = host:port/service).
///
/// 미구현으로 남긴 것 — 착수 시 ConnectionProfile 확장이 필요하다:
/// - **AS SYSDBA** (Golden 로그온의 옵션). 드라이버는 DBAPrivilege 로 지원한다
/// - **Read Only**: Oracle 은 접속 수준 읽기전용이 없다. 트랜잭션마다
///   `SET TRANSACTION READ ONLY` 를 걸어야 해서 profile.ReadOnly 를 접속에 반영하지 못한다
/// </summary>
public sealed class OracleProvider : IDbProvider
{
    public DbKind Kind => DbKind.Oracle;

    public string DisplayName => "Oracle";

    public DbCapabilities Capabilities { get; } = new(
        Transactions: true,
        IsolationLevels: true,
        GridEditing: true,        // ROWID — Golden 이 원래 쓰던 방식
        ExplainPlan: true,        // EXPLAIN PLAN + DBMS_XPLAN
        ServerMessages: true,     // DBMS_OUTPUT
        SessionMonitor: true,     // V$SESSION
        BulkExport: false,        // COPY 같은 서버 측 대량 내보내기 없음
        ForeignKeys: true,
        Schemas: true);           // owner 가 스키마 역할

    /// <summary>Oracle 은 READ COMMITTED 와 SERIALIZABLE 만 세션에 걸 수 있다.</summary>
    public IReadOnlyList<TransactionIsolation> SupportedIsolations { get; } =
    [
        TransactionIsolation.DatabaseDefault,
        TransactionIsolation.ReadCommitted,
        TransactionIsolation.Serializable,
    ];

    public string? RowIdColumn => "ROWID";

    public string BuildConnectionString(ConnectionProfile profile) => new OracleConnectionStringBuilder
    {
        DataSource = $"{profile.Host}:{profile.Port}/{profile.Database}",
        UserID = profile.Username,
        Password = profile.Password,
        // Golden 스타일 툴 특성상 세션 하나를 계속 쓰므로 풀링 불필요
        Pooling = false,
        ConnectionTimeout = 10,
    }.ConnectionString;

    public async Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var conn = new OracleConnection(BuildConnectionString(profile));
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Oracle 은 인용하지 않은 식별자를 대문자로 접는다. 이미 대문자·숫자·밑줄로만 되어
    /// 있고 숫자로 시작하지 않으면 그대로 두고, 아니면 큰따옴표로 감싼다.
    /// </summary>
    public string QuoteIdentifier(string identifier) =>
        identifier.Length > 0
        && !char.IsDigit(identifier[0])
        && identifier.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? identifier
            : '"' + identifier.Replace("\"", "\"\"") + '"';

    /// <summary>비밀번호는 넣지 않는다.</summary>
    public string Describe(ConnectionProfile profile) =>
        $"{profile.Username}@{profile.Host}:{profile.Port}/{profile.Database}";

    public IErdCatalog CreateErdCatalog(ConnectionProfile profile) => new OracleErdCatalog(profile);
}
