using System.Data;
using System.Diagnostics;
using Npgsql;

namespace PrismOne.Db.Core;

/// <summary>
/// 쿼리 탭 하나가 소유하는 DB 세션 (Golden 의 창=세션 모델).
/// 실행 중 쿼리(ActiveQuery)의 reader 가 세션을 점유하므로,
/// 새 문장을 실행하기 전에 이전 ActiveQuery 를 반드시 닫아야 한다.
/// </summary>
public sealed class QuerySession : IAsyncDisposable
{
    public ConnectionProfile Profile { get; }
    public NpgsqlConnection Connection { get; private set; }
    public bool IsAlive => Connection.State == ConnectionState.Open;

    /// <summary>수동 커밋 모드에서 열린 트랜잭션이 있는지 (Golden 의 Commit/Rollback 대상).</summary>
    public bool InTransaction { get; private set; }

    /// <summary>RAISE NOTICE/WARNING 등 서버 메시지 (pgAdmin 의 Messages 탭).</summary>
    public event Action<string>? NoticeReceived;

    private QuerySession(ConnectionProfile profile, NpgsqlConnection conn)
    {
        Profile = profile;
        Connection = conn;
        HookNotices(conn);
    }

    public static async Task<QuerySession> CreateAsync(ConnectionProfile profile, CancellationToken ct = default)
        => new(profile, await profile.OpenAsync(ct));

    private void HookNotices(NpgsqlConnection conn) =>
        conn.Notice += (_, e) =>
            NoticeReceived?.Invoke($"{e.Notice.Severity}: {e.Notice.MessageText}");

    public async Task<ActiveQuery> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        var cmd = new NpgsqlCommand(sql, Connection);
        var sw = Stopwatch.StartNew();
        try
        {
            var reader = await cmd.ExecuteReaderAsync(ct);
            sw.Stop();
            return await ActiveQuery.CreateAsync(cmd, reader, sw.Elapsed);
        }
        catch
        {
            await cmd.DisposeAsync();
            throw;
        }
    }

    /// <summary>취소/오류로 세션이 끊겼으면 같은 프로파일로 다시 연다. (트랜잭션은 소멸)</summary>
    public async Task EnsureAliveAsync(CancellationToken ct = default)
    {
        if (IsAlive) return;
        try { await Connection.DisposeAsync(); } catch { /* 이미 죽은 접속 */ }
        Connection = await Profile.OpenAsync(ct);
        HookNotices(Connection);
        InTransaction = false;
    }

    // ---------- 트랜잭션 제어 (Golden: AutoCommit off 가 기본) ----------

    public async Task EnsureTransactionAsync(CancellationToken ct = default)
    {
        if (InTransaction) return;
        await ExecSimpleAsync("BEGIN", ct);
        InTransaction = true;
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await ExecSimpleAsync("COMMIT", ct);
        InTransaction = false;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        await ExecSimpleAsync("ROLLBACK", ct);
        InTransaction = false;
    }

    /// <summary>읽기 전용 문장인지 — PG 에선 SELECT 에까지 트랜잭션을 열면 idle-in-transaction
    /// 세션이 스냅샷을 붙들어 VACUUM 을 방해하므로, 수동 커밋 모드여도 읽기는 autocommit 으로 둔다.</summary>
    public static bool IsReadOnlyStatement(string sql)
    {
        var head = sql.TrimStart();
        return head.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("VALUES", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("TABLE ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>사용자가 직접 BEGIN/COMMIT/ROLLBACK 을 친 경우 상태를 따라간다.</summary>
    public void NoteStatement(string sql)
    {
        var head = sql.TrimStart();
        if (head.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("START TRANSACTION", StringComparison.OrdinalIgnoreCase))
            InTransaction = true;
        else if (head.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase) ||
                 head.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
                 head.StartsWith("END", StringComparison.OrdinalIgnoreCase))
            InTransaction = false;
    }

    private async Task ExecSimpleAsync(string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, Connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Connection.DisposeAsync(); } catch { /* shutdown */ }
    }
}

/// <summary>
/// 실행된 문장 하나. SELECT 면 reader 를 열어둔 채 배치 단위로 점진 fetch 한다.
/// 한 행을 미리 읽어(look-ahead) 다음 배치 존재 여부를 정확히 안다.
/// </summary>
public sealed class ActiveQuery : IAsyncDisposable
{
    private readonly NpgsqlCommand _cmd;
    private readonly NpgsqlDataReader _reader;
    private string?[]? _lookahead;

    public IReadOnlyList<string> Columns { get; }
    public bool HasGrid => Columns.Count > 0;
    public bool Completed { get; private set; }
    /// <summary>결과셋 없는 문장의 영향 행 수 (완료 후 유효, SELECT 는 -1).</summary>
    public int RowsAffected { get; private set; } = -1;
    /// <summary>첫 행이 도착할 때까지의 실행 시간.</summary>
    public TimeSpan ExecuteElapsed { get; }

    private ActiveQuery(NpgsqlCommand cmd, NpgsqlDataReader reader, string[] columns, TimeSpan elapsed)
    {
        _cmd = cmd;
        _reader = reader;
        Columns = columns;
        ExecuteElapsed = elapsed;
    }

    internal static async Task<ActiveQuery> CreateAsync(NpgsqlCommand cmd, NpgsqlDataReader reader, TimeSpan elapsed)
    {
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);
        var query = new ActiveQuery(cmd, reader, columns, elapsed);
        if (reader.FieldCount == 0)
            await query.FinishAsync();   // DML/DDL — 즉시 완료
        return query;
    }

    public async Task<List<string?[]>> FetchAsync(int maxRows, CancellationToken ct = default)
    {
        var rows = new List<string?[]>();
        if (Completed) return rows;

        if (_lookahead is not null)
        {
            rows.Add(_lookahead);
            _lookahead = null;
        }
        while (rows.Count < maxRows)
        {
            if (!await _reader.ReadAsync(ct)) { await FinishAsync(); return rows; }
            rows.Add(ReadRow());
        }
        if (await _reader.ReadAsync(ct)) _lookahead = ReadRow();
        else await FinishAsync();
        return rows;
    }

    private string?[] ReadRow()
    {
        var row = new string?[_reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
            row[i] = ValueFormatter.Format(_reader.GetValue(i));
        return row;
    }

    private async Task FinishAsync()
    {
        Completed = true;
        await _reader.CloseAsync();
        RowsAffected = _reader.RecordsAffected;
    }

    /// <summary>남은 행을 기다리지 않고 닫는다 — 서버에 cancel 을 보내 drain 을 짧게 만든다.</summary>
    public async Task AbortAsync()
    {
        if (!Completed)
        {
            try { _cmd.Cancel(); } catch { /* 이미 끝났거나 접속 끊김 */ }
            Completed = true;
        }
        try { await _reader.DisposeAsync(); } catch { /* cancel 로 인한 예외는 정상 */ }
        try { await _cmd.DisposeAsync(); } catch { }
    }

    public async ValueTask DisposeAsync() => await AbortAsync();
}
