using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Npgsql;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Db.Core;

/// <summary>
/// DataGrip 의 "Tx Isolation" 목록 그대로 (DatabaseBundle 의 transaction.mode.* 키).
/// PG 는 READ UNCOMMITTED 를 받아들이지만 실제 동작은 READ COMMITTED 와 같다 — DataGrip 도
/// 드라이버가 지원한다고 보고하는 대로 목록에 두므로 우리도 남긴다.
/// </summary>
public enum TransactionIsolation
{
    DatabaseDefault,
    ReadUncommitted,
    ReadCommitted,
    RepeatableRead,
    Serializable,
}

public static class TransactionIsolationExtensions
{
    /// <summary>SQL 리터럴. enum 이라 문장 조립에 써도 인젝션 여지가 없다.</summary>
    public static string ToSql(this TransactionIsolation level) => level switch
    {
        TransactionIsolation.ReadUncommitted => "READ UNCOMMITTED",
        TransactionIsolation.RepeatableRead => "REPEATABLE READ",
        TransactionIsolation.Serializable => "SERIALIZABLE",
        TransactionIsolation.ReadCommitted => "READ COMMITTED",
        _ => "DEFAULT",
    };

    /// <summary>
    /// 세션에 거는 문장. Database Default 는 GUC 를 되돌려 서버/DB 설정값을 그대로 쓰게 한다
    /// (SET SESSION CHARACTERISTICS 는 수준 지정이 필수라 RESET 을 쓴다).
    /// </summary>
    public static string ToSessionSql(this TransactionIsolation level) =>
        level == TransactionIsolation.DatabaseDefault
            ? "RESET default_transaction_isolation"
            : $"SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL {level.ToSql()}";

    /// <summary>툴바 표기 (DataGrip 영문 표기 그대로).</summary>
    public static string Display(this TransactionIsolation level) => level switch
    {
        TransactionIsolation.ReadUncommitted => "Read Uncommitted",
        TransactionIsolation.ReadCommitted => "Read Committed",
        TransactionIsolation.RepeatableRead => "Repeatable Read",
        TransactionIsolation.Serializable => "Serializable",
        _ => "Database Default",
    };

    /// <summary>드롭다운 설명 (DataGrip 의 transaction.mode.*.description 를 옮긴 것).</summary>
    public static string Description(this TransactionIsolation level) => level switch
    {
        TransactionIsolation.ReadUncommitted =>
            "커밋되지 않은 변경을 탐지할 수 있음 (PG 에선 Read Committed 와 동일하게 동작)",
        TransactionIsolation.ReadCommitted => "커밋된 변경만 탐지",
        TransactionIsolation.RepeatableRead => "동시에 발생한 변경을 탐지하지 않음",
        TransactionIsolation.Serializable => "동시 실행이 직렬 실행과 같은 결과",
        _ => "서버/DB 의 기본 격리 수준을 그대로 사용",
    };
}

/// <summary>
/// 쿼리 탭 하나가 소유하는 DB 세션 (Golden 의 창=세션 모델).
/// 실행 중 쿼리(ActiveQuery)의 reader 가 세션을 점유하므로,
/// 새 문장을 실행하기 전에 이전 ActiveQuery 를 반드시 닫아야 한다.
/// </summary>
public sealed class QuerySession : IAsyncDisposable
{
    public ConnectionProfile Profile { get; }

    /// <summary>ADO.NET 공통 타입 — 드라이버별 차이는 Provider 가 흡수한다.</summary>
    public DbConnection Connection { get; private set; }

    private IDbProvider Provider => Profile.Provider;

    public bool IsAlive => Connection.State == ConnectionState.Open;

    /// <summary>수동 커밋 모드에서 열린 트랜잭션이 있는지 (Golden 의 Commit/Rollback 대상).</summary>
    public bool InTransaction { get; private set; }

    /// <summary>이 세션의 기본 격리 수준 (DataGrip 의 Tx Isolation). 새 접속은 DB 기본값에서 시작.</summary>
    public TransactionIsolation Isolation { get; private set; } = TransactionIsolation.DatabaseDefault;

    /// <summary>
    /// 이 세션에서 마지막으로 연 결과셋. 공유 세션(Golden)에서는 접속 하나에 reader 하나뿐이라
    /// 다른 탭이 실행하면 이전 결과의 fetch 는 중단된다.
    /// </summary>
    public ActiveQuery? Current { get; private set; }

    /// <summary>현재 실행 중인 소유자(탭). null 이면 유휴.</summary>
    public object? RunningOwner { get; private set; }

    public bool TryBeginRun(object owner)
    {
        if (RunningOwner is not null && !ReferenceEquals(RunningOwner, owner))
            return false;
        RunningOwner = owner;
        return true;
    }

    public void EndRun(object owner)
    {
        if (ReferenceEquals(RunningOwner, owner))
            RunningOwner = null;
    }

    /// <summary>RAISE NOTICE/WARNING 등 서버 메시지 (pgAdmin 의 Messages 탭).</summary>
    public event Action<string>? NoticeReceived;

    private QuerySession(ConnectionProfile profile, DbConnection conn)
    {
        Profile = profile;
        Connection = conn;
        HookNotices(conn);
    }

    public static async Task<QuerySession> CreateAsync(ConnectionProfile profile, CancellationToken ct = default)
        => new(profile, await profile.OpenDbAsync(ct));

    /// <summary>서버 메시지는 PG 고유 기능 — 다른 드라이버는 그냥 넘어간다.</summary>
    private void HookNotices(DbConnection conn)
    {
        if (conn is NpgsqlConnection pg)
            pg.Notice += (_, e) =>
                NoticeReceived?.Invoke($"{e.Notice.Severity}: {e.Notice.MessageText}");
    }

    private DbCommand NewCommand(string sql)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public async Task<ActiveQuery> ExecuteAsync(
        string sql, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? binds = null)
    {
        var cmd = NewCommand(binds is { Count: > 0 } ? BindVariables.Rewrite(sql) : sql);
        if (binds is { Count: > 0 })
        {
            foreach (var (name, value) in binds)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = (object?)value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }
        // 접속 하나에 reader 하나 — 이전 결과가 열려 있으면 먼저 닫는다 (공유 세션 시맨틱)
        if (Current is { Completed: false })
            await Current.AbortAsync();

        var sw = Stopwatch.StartNew();
        try
        {
            var reader = await cmd.ExecuteReaderAsync(ct);
            sw.Stop();
            var query = await ActiveQuery.CreateAsync(cmd, reader, sw.Elapsed);
            Current = query;
            return query;
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
        Connection = await Profile.OpenDbAsync(ct);
        HookNotices(Connection);
        InTransaction = false;
        // 새 접속은 DB 기본값으로 시작하므로 고른 격리 수준을 다시 걸어준다
        if (Isolation != TransactionIsolation.DatabaseDefault)
            await ApplyIsolationAsync(Isolation, ct);
    }

    /// <summary>
    /// 세션 기본 격리 수준 변경 (DataGrip 의 Tx isolation).
    /// PG 규약상 <c>SET SESSION CHARACTERISTICS</c> 는 <b>다음 트랜잭션부터</b> 적용되므로,
    /// 열린 트랜잭션이 있으면 그 트랜잭션은 이전 수준으로 끝난다.
    /// </summary>
    public async Task ApplyIsolationAsync(TransactionIsolation level, CancellationToken ct = default)
    {
        // 지원하지 않는 DB·수준이면 문장을 보내지 않는다 (Oracle 은 RC/Serializable 만)
        if (Provider.SessionIsolationSql(level) is { } sql)
            await ExecSimpleAsync(sql, ct);
        Isolation = level;
    }

    // ---------- 트랜잭션 제어 (Golden: AutoCommit off 가 기본) ----------

    public async Task EnsureTransactionAsync(CancellationToken ct = default)
    {
        if (InTransaction) return;
        // Oracle 은 DML 이 암시적으로 연다 — 보낼 문장이 없다
        if (Provider.BeginTransactionSql is { } sql)
            await ExecSimpleAsync(sql, ct);
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

    /// <summary>
    /// 그리드 편집(Run and Edit)이 만든 문장 하나를 실행하고 영향 행 수를 돌려준다.
    /// 값은 <c>unknown</c> 타입으로 보내 PG 가 대상 컬럼 타입으로 캐스팅하게 한다
    /// (셀에서 읽은 건 전부 문자열이라 클라이언트에서 타입을 정하면 오히려 어긋난다).
    /// </summary>
    public async Task<int> ExecuteEditAsync(EditStatement statement, CancellationToken ct = default)
    {
        // 접속 하나에 reader 하나 — 열린 결과가 있으면 먼저 닫는다
        if (Current is { Completed: false })
            await Current.AbortAsync();

        await using var cmd = NewCommand(statement.Sql);
        var index = 0;
        foreach (var value in statement.Parameters)
        {
            index++;
            // PG 는 unknown 으로 보내야 서버가 대상 컬럼 타입으로 캐스팅한다.
            // 다른 드라이버는 기본 추론에 맡긴다 (셀 값은 전부 문자열이라
            // 클라이언트에서 타입을 정하면 오히려 어긋난다).
            if (cmd.CreateParameter() is NpgsqlParameter pg)
            {
                pg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;
                pg.Value = (object?)value ?? DBNull.Value;
                cmd.Parameters.Add(pg);
            }
            else
            {
                var p = cmd.CreateParameter();
                // SQLite 는 이름 없는 파라미터를 거부한다 — placeholder(@pN/:pN)와 같은 이름으로
                p.ParameterName = $"p{index}";
                p.Value = (object?)value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 같은 문장을 값만 바꿔 반복 실행한다 (CSV import 등 대량 insert).
    /// 명령·파라미터를 **한 번 만들어 재사용**하고 가능하면 Prepare 한다 —
    /// 행마다 명령을 새로 만드는 것보다 훨씬 싸다. 실패하면 몇 번째 행인지 실어 던진다.
    /// </summary>
    public async Task<int> ExecuteBatchAsync(
        string sql, IReadOnlyList<IReadOnlyList<string?>> rows,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        // 접속 하나에 reader 하나 — 열린 결과가 있으면 먼저 닫는다
        if (Current is { Completed: false })
            await Current.AbortAsync();

        await using var cmd = NewCommand(sql);
        var parameters = new DbParameter[rows[0].Count];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = cmd.CreateParameter();
            if (p is NpgsqlParameter pg)
                pg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;   // 서버가 컬럼 타입으로 캐스팅
            else
                p.ParameterName = $"p{i + 1}";   // SQLite 는 이름 없는 파라미터를 거부한다
            parameters[i] = p;
            cmd.Parameters.Add(p);
        }
        try { await cmd.PrepareAsync(ct); } catch { /* Prepare 미지원 드라이버 — 그냥 간다 */ }

        var done = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < parameters.Length; i++)
                parameters[i].Value = (object?)(i < row.Count ? row[i] : null) ?? DBNull.Value;
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BatchRowException(done + 1, ex);
            }
            done++;
            if (done % 500 == 0)
                progress?.Report(done);
        }
        return done;
    }

    private async Task ExecSimpleAsync(string sql, CancellationToken ct)
    {
        await using var cmd = NewCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>첫 컬럼을 원문 그대로(표시용 잘라내기 없이) 이어붙여 돌려준다 — EXPLAIN JSON 용.</summary>
    public async Task<string> ExecuteTextAsync(string sql, CancellationToken ct = default)
    {
        await using var cmd = NewCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var sb = new System.Text.StringBuilder();
        while (await reader.ReadAsync(ct))
            sb.AppendLine(reader.GetValue(0)?.ToString());
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try { await Connection.DisposeAsync(); } catch { /* shutdown */ }
    }
}

/// <summary>배치 실행 중 몇 번째 행(1부터)에서 실패했는지를 실은 예외.</summary>
public sealed class BatchRowException(int rowNumber, Exception inner)
    : Exception($"{rowNumber}번째 행: {inner.Message}", inner)
{
    public int RowNumber { get; } = rowNumber;
}

/// <summary>
/// fetch 된 행 하나. Raw 는 표시용으로 잘린 셀의 원문만 담는다 (없으면 null).
/// <paramref name="RowContext"/> 는 provider 별 부가 정보를 담는 불투명한 자리다 —
/// 지금은 Mongo 의 <see cref="Mongo.MongoRowContext"/>(Edit Document 용 원본 문서)만 쓴다.
/// 다른 provider 는 항상 null.
/// </summary>
public readonly record struct FetchedRow(string?[] Cells, string?[]? Raw, object? RowContext = null);

/// <summary>
/// 실행된 문장 하나. SELECT 면 reader 를 열어둔 채 배치 단위로 점진 fetch 한다.
/// 한 행을 미리 읽어(look-ahead) 다음 배치 존재 여부를 정확히 안다.
/// </summary>
public sealed class ActiveQuery : IAsyncDisposable
{
    private readonly DbCommand _cmd;
    private readonly DbDataReader _reader;
    private FetchedRow? _lookahead;

    public IReadOnlyList<string> Columns { get; }
    public bool HasGrid => Columns.Count > 0;
    public bool Completed { get; private set; }
    /// <summary>결과셋 없는 문장의 영향 행 수 (완료 후 유효, SELECT 는 -1).</summary>
    public int RowsAffected { get; private set; } = -1;
    /// <summary>첫 행이 도착할 때까지의 실행 시간.</summary>
    public TimeSpan ExecuteElapsed { get; }

    private ActiveQuery(DbCommand cmd, DbDataReader reader, string[] columns, TimeSpan elapsed)
    {
        _cmd = cmd;
        _reader = reader;
        Columns = columns;
        ExecuteElapsed = elapsed;
    }

    internal static async Task<ActiveQuery> CreateAsync(DbCommand cmd, DbDataReader reader, TimeSpan elapsed)
    {
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);
        var query = new ActiveQuery(cmd, reader, columns, elapsed);
        if (reader.FieldCount == 0)
            await query.FinishAsync();   // DML/DDL — 즉시 완료
        return query;
    }

    public async Task<List<FetchedRow>> FetchAsync(int maxRows, CancellationToken ct = default)
    {
        var rows = new List<FetchedRow>();
        if (Completed) return rows;

        if (_lookahead is { } ahead)
        {
            rows.Add(ahead);
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

    private FetchedRow ReadRow()
    {
        var cells = new string?[_reader.FieldCount];
        string?[]? raw = null;
        for (var i = 0; i < cells.Length; i++)
        {
            var value = _reader.GetValue(i);
            cells[i] = ValueFormatter.Format(value);
            // 표시용으로 잘린 값만 원문을 보관 (cell detail 창용) — 메모리 절약
            if (cells[i] is { } display && display.Length > ValueFormatter.MaxDisplayChars)
            {
                raw ??= new string?[cells.Length];
                raw[i] = ValueFormatter.FormatFull(value);
            }
        }
        // Mongo 는 Edit Document 가 되쓸 원본 문서를 리더가 커서 위치별로 들고 있다 —
        // 다른 provider 는 이 타입이 아니므로 항상 null.
        var rowContext = _reader is Mongo.MongoDbDataReader mongoReader ? mongoReader.CurrentRowContext : null;
        return new FetchedRow(cells, raw, rowContext);
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
