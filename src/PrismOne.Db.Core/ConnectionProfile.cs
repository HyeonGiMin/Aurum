using Npgsql;

namespace PrismOne.Db.Core;

/// <summary>PostgreSQL 접속 정보. Studio(GUI)와 Cli가 공용.</summary>
public sealed record ConnectionProfile(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    bool ReadOnly = false)
{
    public static ConnectionProfile Default { get; } =
        new("localhost", 5432, "prismone", "postgres", "");

    public string ToConnectionString() => new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = Port,
        Database = Database,
        Username = Username,
        Password = Password,
        // Golden 스타일 툴 특성상 세션 하나를 계속 쓰므로 풀링 불필요
        Pooling = false,
        Timeout = 10,
        // 쿼리는 사용자가 Stop으로 끊을 때까지 무제한
        CommandTimeout = 0,
        // Golden 로그인의 Read Only 체크박스 대응
        Options = ReadOnly ? "-c default_transaction_read_only=on" : null,
    }.ConnectionString;

    /// <summary>상태 표시용: postgres@localhost:5432/prismone</summary>
    public string DisplayName => $"{Username}@{Host}:{Port}/{Database}";

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(ToConnectionString());
        await conn.OpenAsync(ct);
        return conn;
    }
}
