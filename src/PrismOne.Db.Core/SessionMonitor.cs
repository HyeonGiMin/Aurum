using Npgsql;

namespace PrismOne.Db.Core;

/// <summary>pg_stat_activity 한 행 (세션 모니터).</summary>
public sealed record ActivityRow(
    int Pid,
    string User,
    string App,
    string Client,
    string State,
    string Elapsed,
    string Wait,
    string Query);

/// <summary>
/// pgAdmin 대시보드의 서버 활동 뷰 — 현재 DB 의 세션 조회와 취소/종료.
/// PG 는 pg_stat_activity, Mongo 는 currentOp 로 같은 창을 채운다.
/// </summary>
public static class SessionMonitor
{
    public static async Task<List<ActivityRow>> GetActivityAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        if (profile.Kind == Providers.DbKind.MongoDb)
            return await Mongo.MongoSessionMonitor.GetActivityAsync(profile, ct);
        const string sql = """
            SELECT pid,
                   COALESCE(usename, ''),
                   COALESCE(application_name, ''),
                   COALESCE(client_addr::text, 'local'),
                   COALESCE(state, ''),
                   COALESCE(to_char(now() - query_start, 'HH24:MI:SS'), ''),
                   COALESCE(wait_event_type || ':' || wait_event, ''),
                   COALESCE(left(query, 300), '')
              FROM pg_stat_activity
             WHERE datname = current_database()
               AND pid <> pg_backend_pid()
             ORDER BY query_start NULLS LAST
            """;
        var rows = new List<ActivityRow>();
        await using var conn = await profile.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new ActivityRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return rows;
    }

    /// <summary>실행 중 쿼리만 취소 (세션 유지) — pg_cancel_backend, Mongo 는 killOp.</summary>
    public static Task<bool> CancelAsync(ConnectionProfile profile, int pid, CancellationToken ct = default)
        => profile.Kind == Providers.DbKind.MongoDb
            ? Mongo.MongoSessionMonitor.KillOpAsync(profile, pid, ct)
            : SignalAsync(profile, pid, "pg_cancel_backend", ct);

    /// <summary>세션 자체를 종료 — pg_terminate_backend. Mongo 는 구분이 없어 역시 killOp.</summary>
    public static Task<bool> TerminateAsync(ConnectionProfile profile, int pid, CancellationToken ct = default)
        => profile.Kind == Providers.DbKind.MongoDb
            ? Mongo.MongoSessionMonitor.KillOpAsync(profile, pid, ct)
            : SignalAsync(profile, pid, "pg_terminate_backend", ct);

    private static async Task<bool> SignalAsync(ConnectionProfile profile, int pid, string fn, CancellationToken ct)
    {
        await using var conn = await profile.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {fn}(@pid)", conn);
        cmd.Parameters.AddWithValue("pid", pid);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
