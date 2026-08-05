using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace PrismOne.Db.Core.Mongo;

/// <summary>
/// Mongo 를 ADO.NET 모양으로 감싸는 어댑터.
///
/// DataGrip 이 쓰는 방법과 같은 수를 .NET 으로 옮긴 것이다 — JetBrains 는 앱의 추상화를
/// 넓히는 대신 <c>DataGrip/mongo-jdbc-driver</c> 라는 JDBC 껍데기를 만들어
/// 네이티브 Mongo 드라이버를 감쌌다. JDBC 의 .NET 대응이 ADO.NET 이므로,
/// 여기서 <see cref="DbConnection"/>/<see cref="DbCommand"/>/<see cref="DbDataReader"/> 를
/// 흉내 내면 <c>QuerySession</c> 아래의 그리드·내보내기·정렬·Text 뷰가 그대로 동작한다.
///
/// <b>제약</b>: <see cref="DbDataReader"/> 는 결과셋마다 컬럼이 고정인데 Mongo 는 문서마다
/// 필드가 달라, 컬럼을 알려면 문서를 먼저 다 읽어야 한다 — 즉 결과가 <b>버퍼링</b>된다.
/// <see cref="MongoSession.DefaultLimit"/> 로 양을 묶어 두는 이유다.
/// </summary>
public sealed class MongoDbConnection : DbConnection
{
    private ConnectionProfile _profile;
    private MongoSession? _session;
    private ConnectionState _state = ConnectionState.Closed;

    public MongoDbConnection(ConnectionProfile profile) => _profile = profile;

    /// <summary>한 번에 가져올 문서 수 상한. 탭이 옵션에 맞춰 조절할 수 있다.</summary>
    public int Limit { get; set; } = MongoSession.DefaultLimit;

    internal MongoSession Session =>
        _session ?? throw new InvalidOperationException("접속이 열려 있지 않습니다.");

    /// <summary>
    /// <b>비밀번호를 담지 않는다</b> — 이 값은 오류 메시지·로그에 실릴 수 있다.
    /// 실제 접속 문자열은 <see cref="MongoSession.BuildConnectionString"/> 이 따로 만든다.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => string.IsNullOrEmpty(_profile.Database)
            ? $"mongodb://{_profile.Host}:{_profile.Port}"
            : $"mongodb://{_profile.Host}:{_profile.Port}/{_profile.Database}";
        set => throw new NotSupportedException("접속 정보는 ConnectionProfile 로 지정합니다.");
    }

    public override string Database => _profile.Database;
    public override string DataSource => $"{_profile.Host}:{_profile.Port}";
    public override string ServerVersion => "";
    public override ConnectionState State => _state;

    public override void Open()
    {
        _session = MongoSession.Open(_profile);
        // Mongo 드라이버는 지연 접속이라 여기서 한 번 두드려야 실패를 바로 알 수 있다.
        _session.PingAsync().GetAwaiter().GetResult();
        _state = ConnectionState.Open;
    }

    public override async Task OpenAsync(CancellationToken ct)
    {
        _session = MongoSession.Open(_profile);
        await _session.PingAsync(ct);
        _state = ConnectionState.Open;
    }

    public override void Close()
    {
        _session?.Dispose();
        _session = null;
        _state = ConnectionState.Closed;
    }

    /// <summary>
    /// 대상 데이터베이스를 바꾼다. Mongo 는 한 접속으로 여러 DB 를 보는 게 정상이라
    /// (Explorer 트리가 DB → 컬렉션으로 나오는 이유) ADO.NET 의 이 자리를 실제로 쓴다.
    /// </summary>
    public override void ChangeDatabase(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("데이터베이스 이름이 비어 있습니다.", nameof(databaseName));

        _profile = _profile with { Database = databaseName };
        if (_state != ConnectionState.Open) return;

        _session?.Dispose();
        _session = MongoSession.Open(_profile);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Mongo 트랜잭션은 지원하지 않습니다 (replica set 필요).");

    protected override DbCommand CreateDbCommand() => new MongoDbCommand { Connection = this };

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }
}

/// <summary>Mongo 셸 문장 하나. 파라미터는 쓰지 않는다 — 필터가 이미 문서라서.</summary>
public sealed class MongoDbCommand : DbCommand
{
    private MongoDbConnection? _connection;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value as MongoDbConnection;
    }

    protected override DbParameterCollection DbParameterCollection { get; } = new MongoParameterCollection();
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() =>
        throw new NotSupportedException("Mongo 경로는 파라미터를 쓰지 않습니다 — 필터를 문서로 씁니다.");

    private MongoDbConnection Required =>
        _connection ?? throw new InvalidOperationException("Connection 이 없습니다.");

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken ct)
    {
        // 읽기 전용이라 "영향받은 행"이 없다. 실행은 해서 오류는 그대로 드러낸다.
        await Required.Session.ExecuteAsync(CommandText, Required.Limit, ct);
        return -1;
    }

    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<object?> ExecuteScalarAsync(CancellationToken ct)
    {
        var result = await Required.Session.ExecuteAsync(CommandText, Required.Limit, ct);
        return result.Table.Rows.Count > 0 && result.Table.Columns.Count > 0
            ? result.Table.Rows[0][0]
            : null;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken ct)
    {
        var result = await Required.Session.ExecuteAsync(CommandText, Required.Limit, ct);
        return new MongoDbDataReader(result);
    }
}

/// <summary>
/// 평탄화된 표를 <see cref="DbDataReader"/> 로 흘려보낸다.
/// 이미 메모리에 있는 표라 읽기는 커서 전진뿐이다.
/// </summary>
public sealed class MongoDbDataReader(MongoResult result) : DbDataReader
{
    private int _index = -1;
    private bool _closed;

    /// <summary>상태바에 쓸 요약 ("3 document(s)").</summary>
    public string Summary => result.Summary;

    public override int FieldCount => result.Table.Columns.Count;
    public override bool HasRows => result.Table.Rows.Count > 0;
    public override bool IsClosed => _closed;
    public override int Depth => 0;

    /// <summary>읽기 전용이므로 영향받은 행은 없다 (ADO.NET 관례상 -1).</summary>
    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_index + 1 >= result.Table.Rows.Count) return false;
        _index++;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(Read());

    public override bool NextResult() => false;

    public override string GetName(int ordinal) => result.Table.Columns[ordinal];

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < result.Table.Columns.Count; i++)
            if (string.Equals(result.Table.Columns[i], name, StringComparison.Ordinal))
                return i;
        throw new IndexOutOfRangeException(name);
    }

    public override object GetValue(int ordinal) => Raw(ordinal) ?? DBNull.Value;

    private object? Raw(int ordinal)
    {
        if (_index < 0) throw new InvalidOperationException("Read() 를 먼저 호출해야 합니다.");
        return result.Table.Rows[_index][ordinal];
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++) values[i] = GetValue(i);
        return count;
    }

    public override bool IsDBNull(int ordinal) => Raw(ordinal) is null;

    /// <summary>
    /// 값이 없는 칸(문서에 그 필드가 없던 경우)이 많아 컬럼 타입을 하나로 못 박을 수 없다 —
    /// object 로 두고 표시·정렬은 실제 값의 타입을 따르게 한다.
    /// </summary>
    public override Type GetFieldType(int ordinal) => typeof(object);

    public override string GetDataTypeName(int ordinal) => "bson";

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetValue(ordinal).ToString()!);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => GetValue(ordinal).ToString() ?? "";

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException("Mongo 결과에서 바이트 스트림 읽기는 지원하지 않습니다.");

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException("Mongo 결과에서 문자 스트림 읽기는 지원하지 않습니다.");

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override void Close() => _closed = true;
}

/// <summary>Mongo 는 파라미터를 쓰지 않으므로 항상 비어 있다 (ADO.NET 계약을 채우기 위한 것).</summary>
internal sealed class MongoParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot { get; } = new();

    public override int Add(object value) => throw Unsupported();
    public override void AddRange(Array values) => throw Unsupported();
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => false;
    public override bool Contains(string value) => false;
    public override void CopyTo(Array array, int index) { }
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => -1;
    public override int IndexOf(string parameterName) => -1;
    public override void Insert(int index, object value) => throw Unsupported();
    public override void Remove(object value) { }
    public override void RemoveAt(int index) { }
    public override void RemoveAt(string parameterName) { }

    protected override DbParameter GetParameter(int index) => throw Unsupported();
    protected override DbParameter GetParameter(string parameterName) => throw Unsupported();
    protected override void SetParameter(int index, DbParameter value) => throw Unsupported();
    protected override void SetParameter(string parameterName, DbParameter value) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("Mongo 경로는 파라미터를 쓰지 않습니다 — 필터를 문서로 씁니다.");
}
