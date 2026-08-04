using Npgsql;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Db.Core;

/// <summary>
/// 접속 정보. Studio(GUI)와 Cli 가 공용.
///
/// <see cref="Kind"/> 는 **맨 뒤에 기본값과 함께** 두었다 — 기존 호출부와
/// connections.json(필드 없음)이 그대로 PostgreSQL 로 동작하게 하기 위해서다.
/// SQLite 처럼 파일 DB 면 <see cref="Database"/> 가 파일 경로다.
/// </summary>
public sealed record ConnectionProfile(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    bool ReadOnly = false,
    DbKind Kind = DbKind.PostgreSql)
{
    public static ConnectionProfile Default { get; } =
        new("localhost", 5432, "prismone", "postgres", "");

    /// <summary>SQLite 등 파일 DB 용 — 경로만 있으면 된다.</summary>
    public static ConnectionProfile ForFile(string path, DbKind kind, bool readOnly = false) =>
        new("", 0, path, "", "", readOnly, kind);

    public IDbProvider Provider => DbProviders.For(Kind);

    public string ToConnectionString() => Provider.BuildConnectionString(this);

    /// <summary>상태 표시용. PG 는 postgres@localhost:5432/prismone, SQLite 는 파일 이름.</summary>
    public string DisplayName => Provider.Describe(this);

    /// <summary>드라이버를 모르는 호출부용 — ADO.NET 공통 타입으로 연다.</summary>
    public Task<System.Data.Common.DbConnection> OpenDbAsync(CancellationToken ct = default) =>
        Provider.OpenAsync(this, ct);

    /// <summary>
    /// PostgreSQL 전용 경로 (QuerySession·카탈로그가 Npgsql 고유 기능을 쓴다).
    /// 다른 DB 는 열 수 없다 — 멀티 DB 는 <see cref="OpenDbAsync"/> 쪽으로 옮겨간다.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (Kind != DbKind.PostgreSql)
            throw new InvalidOperationException(
                $"{Kind} 접속은 OpenAsync(PostgreSQL 전용)로 열 수 없습니다. OpenDbAsync 를 쓰세요.");
        var conn = new NpgsqlConnection(ToConnectionString());
        await conn.OpenAsync(ct);
        return conn;
    }
}
