using Npgsql;
using PrismOne.Db.Core.Providers;
using PrismOne.Db.Core.Ssh;

namespace PrismOne.Db.Core;

/// <summary>
/// 접속 정보. Studio(GUI)와 Cli 가 공용.
///
/// <see cref="Kind"/> 와 <see cref="Ssh"/> 는 **맨 뒤에 기본값과 함께** 두었다 — 기존 호출부와
/// connections.json(필드 없음)이 그대로 PostgreSQL·직접 접속으로 동작하게 하기 위해서다.
/// SQLite 처럼 파일 DB 면 <see cref="Database"/> 가 파일 경로다.
///
/// <see cref="Ssh"/> 가 있으면 <see cref="Host"/>/<see cref="Port"/> 는 **SSH 서버에서 본**
/// DB 주소다 (흔히 localhost:5432). 실제 접속은 <see cref="SshTunnelPool"/> 이 세운
/// 로컬 포워딩 포트로 나간다 — DataGrip 의 "SSH tunnel" 과 같은 동작이다.
/// </summary>
public sealed record ConnectionProfile(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    bool ReadOnly = false,
    DbKind Kind = DbKind.PostgreSql,
    SshOptions? Ssh = null)
{
    public static ConnectionProfile Default { get; } =
        new("localhost", 5432, "prismone", "postgres", "");

    /// <summary>SQLite 등 파일 DB 용 — 경로만 있으면 된다.</summary>
    public static ConnectionProfile ForFile(string path, DbKind kind, bool readOnly = false) =>
        new("", 0, path, "", "", readOnly, kind);

    /// <summary>
    /// 이 프로필이 <b>이미 터널을 통과한</b> 것인지. <see cref="SshTunnelPool"/> 만 켠다.
    ///
    /// Mongo 드라이버가 replica set 토폴로지를 재탐색해 터널 밖 주소로 다시 붙는 것을
    /// 막는 데 쓴다(<c>directConnection=true</c>). 그 외에는 표시용이다.
    /// </summary>
    public bool ViaTunnel { get; private init; }

    /// <summary>
    /// 터널을 통과한 사본 — DB 주소를 로컬 포워딩 끝점으로 바꾸고 <see cref="Ssh"/> 를 비운다.
    /// Ssh 를 비우는 것이 중요하다: 이 사본을 받은 드라이버 경로가 터널을 또 세우려 하지 않는다.
    /// </summary>
    internal ConnectionProfile ThroughTunnel(string localHost, int localPort) =>
        this with { Host = localHost, Port = localPort, Ssh = null, ViaTunnel = true };

    public IDbProvider Provider => DbProviders.For(Kind);

    /// <summary>
    /// 표시·진단용 접속 문자열. <b>터널을 세우지 않는다</b> — SSH 를 쓰는 프로필은
    /// 여기 적힌 host/port 로 직접 붙지 않는다. 실제 접속은 <see cref="OpenDbAsync"/> 로 연다.
    /// </summary>
    public string ToConnectionString() => Provider.BuildConnectionString(this);

    /// <summary>상태 표시용. PG 는 postgres@localhost:5432/prismone, SQLite 는 파일 이름.</summary>
    public string DisplayName => Provider.Describe(this);

    /// <summary>SSH 를 쓰면 "ssh user@jump", 아니면 null. 창 제목·상태바가 덧붙인다.</summary>
    public string? SshLabel => Ssh is null ? null : $"ssh {Ssh.Describe}";

    /// <summary>드라이버를 모르는 호출부용 — ADO.NET 공통 타입으로 연다.</summary>
    public async Task<System.Data.Common.DbConnection> OpenDbAsync(CancellationToken ct = default)
    {
        var effective = await SshTunnelPool.ResolveAsync(this, ct);
        return await effective.Provider.OpenAsync(effective, ct);
    }

    /// <summary>
    /// PostgreSQL 전용 경로 (QuerySession·카탈로그가 Npgsql 고유 기능을 쓴다).
    /// 다른 DB 는 열 수 없다 — 멀티 DB 는 <see cref="OpenDbAsync"/> 쪽으로 옮겨간다.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (Kind != DbKind.PostgreSql)
            throw new InvalidOperationException(
                $"{Kind} 접속은 OpenAsync(PostgreSQL 전용)로 열 수 없습니다. OpenDbAsync 를 쓰세요.");
        var effective = await SshTunnelPool.ResolveAsync(this, ct);
        var conn = new NpgsqlConnection(effective.ToConnectionString());
        await conn.OpenAsync(ct);
        return conn;
    }
}
