using System.Data.Common;
using Npgsql;

namespace PrismOne.Db.Core.Providers;

/// <summary>
/// PostgreSQL. 기존 동작을 그대로 옮긴 것이라 0단계에서 바뀌는 게 없어야 한다
/// (접속 문자열 옵션은 ConnectionProfile.ToConnectionString 과 동일).
/// </summary>
public sealed class PostgresProvider : IDbProvider
{
    public DbKind Kind => DbKind.PostgreSql;

    public string DisplayName => "PostgreSQL";

    public DbCapabilities Capabilities { get; } = new(
        Transactions: true,
        IsolationLevels: true,
        GridEditing: true,
        ExplainPlan: true,
        ServerMessages: true,
        SessionMonitor: true,
        BulkExport: true,
        ForeignKeys: true,
        Schemas: true);

    /// <summary>PG 는 네 수준을 모두 받는다 (READ UNCOMMITTED 는 READ COMMITTED 로 동작).</summary>
    public IReadOnlyList<TransactionIsolation> SupportedIsolations { get; } =
    [
        TransactionIsolation.DatabaseDefault,
        TransactionIsolation.ReadUncommitted,
        TransactionIsolation.ReadCommitted,
        TransactionIsolation.RepeatableRead,
        TransactionIsolation.Serializable,
    ];

    /// <summary>PG 는 ctid 로 행을 특정한다 (Golden 이 Oracle ROWID 를 쓰던 자리).</summary>
    public string? RowIdColumn => "ctid";

    public string? BeginTransactionSql => "BEGIN";

    /// <summary>기존 동작 그대로 — SET SESSION CHARACTERISTICS (다음 트랜잭션부터 적용).</summary>
    public string? SessionIsolationSql(TransactionIsolation level) => level.ToSessionSql();

    public string BuildConnectionString(ConnectionProfile profile) => new NpgsqlConnectionStringBuilder
    {
        Host = profile.Host,
        Port = profile.Port,
        Database = profile.Database,
        Username = profile.Username,
        Password = profile.Password,
        // Golden 스타일 툴 특성상 세션 하나를 계속 쓰므로 풀링 불필요
        Pooling = false,
        Timeout = 10,
        // 쿼리는 사용자가 Stop 으로 끊을 때까지 무제한
        CommandTimeout = 0,
        // Golden 로그인의 Read Only 체크박스 대응
        Options = profile.ReadOnly ? "-c default_transaction_read_only=on" : null,
    }.ConnectionString;

    public async Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(BuildConnectionString(profile));
        await conn.OpenAsync(ct);
        return conn;
    }

    public string ParameterPlaceholder(int oneBasedIndex) => $"${oneBasedIndex}";

    /// <summary>소문자·숫자·밑줄로만 되어 있고 숫자로 시작하지 않으면 그대로 둔다.</summary>
    public string QuoteIdentifier(string identifier) =>
        identifier.Length > 0
        && !char.IsDigit(identifier[0])
        && identifier.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? identifier
            : '"' + identifier.Replace("\"", "\"\"") + '"';

    /// <summary>비밀번호는 넣지 않는다.</summary>
    public string Describe(ConnectionProfile profile) =>
        $"{profile.Username}@{profile.Host}:{profile.Port}/{profile.Database}";

    public IErdCatalog CreateErdCatalog(ConnectionProfile profile) => new PgErdCatalog(profile);
}
