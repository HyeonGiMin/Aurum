using System.Data.Common;
using PrismOne.Db.Core.Mongo;

namespace PrismOne.Db.Core.Providers;

/// <summary>
/// MongoDB. SQL 이 아니지만 <see cref="MongoDbConnection"/> 이 ADO.NET 모양으로 감싸므로
/// 나머지 앱은 다른 DB 와 똑같이 다룬다 — DataGrip 이 mongo-jdbc-driver 로 하는 것과 같은 수.
///
/// 문장은 SQL 이 아니라 Mongo 셸 구문이다: <c>db.people.find({ age: { $gt: 20 } }).limit(10)</c>.
/// 읽기 전용이라 쓰기 연산은 파서가 받지 않는다.
/// </summary>
public sealed class MongoProvider : IDbProvider
{
    public DbKind Kind => DbKind.MongoDb;

    public string DisplayName => "MongoDB";

    public DbCapabilities Capabilities { get; } = new(
        Transactions: false,      // replica set 이 있어야 하고 조회 전용이라 쓰지 않는다
        IsolationLevels: false,
        GridEditing: false,       // _id 기준 편집은 아직 (MULTI_DB_PLAN 3단계 잔여)
        ExplainPlan: true,        // explain() — queryPlanner/executionStats 를 플랜 트리로
        ServerMessages: false,
        SessionMonitor: true,     // currentOp / killOp
        BulkExport: false,        // COPY 같은 서버측 내보내기 없음 — 행 단위
        ForeignKeys: false,       // FK 개념이 없다 → ERD 관계선 없음
        Schemas: false);

    public IReadOnlyList<TransactionIsolation> SupportedIsolations { get; } = [];

    /// <summary>행 특정용 의사 컬럼이 없다 (편집을 켜면 <c>_id</c> 를 써야 한다 — 아직 미지원).</summary>
    public string? RowIdColumn => null;

    public string? RowIdSelect(string qualifier) => null;

    public string RowIdPredicate(int oneBasedIndex) =>
        throw new NotSupportedException("MongoDB 는 그리드 편집을 지원하지 않습니다.");

    public string? BeginTransactionSql => null;

    public string? SessionIsolationSql(TransactionIsolation level) => null;

    /// <summary>DB 를 안 적었으면(전체 DB 대상) 슬래시도 안 붙인다.</summary>
    private static string HostPort(ConnectionProfile profile) =>
        string.IsNullOrEmpty(profile.Database)
            ? $"{profile.Host}:{profile.Port}"
            : $"{profile.Host}:{profile.Port}/{profile.Database}";

    /// <summary>
    /// 표시·진단용. <b>비밀번호를 담지 않는다</b> —
    /// 실제 접속에 쓰는 문자열은 <see cref="MongoSession.BuildConnectionString"/> 이 만든다.
    /// </summary>
    public string BuildConnectionString(ConnectionProfile profile) => $"mongodb://{HostPort(profile)}";

    public async Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var connection = new MongoDbConnection(profile);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>Mongo 는 파라미터를 쓰지 않는다 — 필터가 이미 문서다.</summary>
    public string ParameterPlaceholder(int oneBasedIndex) =>
        throw new NotSupportedException("Mongo 경로는 파라미터를 쓰지 않습니다.");

    /// <summary>컬렉션 이름에 점이 들어갈 수 있어 큰따옴표로 감싼다 (셸 표기와 맞춘다).</summary>
    public string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\\\"") + '"';

    public string Describe(ConnectionProfile profile)
    {
        var who = string.IsNullOrEmpty(profile.Username) ? "" : $"{profile.Username}@";
        return $"{who}{HostPort(profile)}";
    }

    public IErdCatalog CreateErdCatalog(ConnectionProfile profile) => new MongoErdCatalog(profile);
}

/// <summary>
/// <b>데이터베이스 → 컬렉션</b>을 ERD 그래프로 준다 (Studio3T·DataGrip 의 트리 모양).
/// Mongo 의 "데이터베이스"가 다른 DB 의 스키마 자리에 대응한다 —
/// 접속한 DB 하나만 보여주면 다른 DB 에 든 컬렉션을 찾을 수 없어서 서버 전체를 읽는다.
///
/// FK 가 없으므로 <b>관계는 항상 비어 있다</b> — ERD 창이 "FK 제약이 없어 관계선이
/// 없습니다" 로 알린다. Explorer·Object Browser·자동완성에는 이걸로 충분하다.
/// </summary>
public sealed class MongoErdCatalog(ConnectionProfile profile) : IErdCatalog
{
    /// <summary>
    /// 접속 시 DB 를 <b>적었으면 그 DB 만</b>, <b>안 적었으면 서버의 DB 전부</b>.
    /// Mongo 는 DB 를 정하지 않고 붙는 게 자연스러워서 후자를 기본으로 쓸 수 있게 했다.
    /// </summary>
    public async Task<List<string>> GetSchemasAsync(CancellationToken ct = default)
    {
        using var session = MongoSession.Open(profile);
        var databases = (await session.ListDatabaseNamesAsync(ct)).ToList();

        // 지정했으면 그 DB 만. 아직 문서가 없어 서버 목록에 안 잡혀도 보여준다.
        return string.IsNullOrWhiteSpace(profile.Database) ? databases : [profile.Database];
    }

    public Task<ErdGraph> LoadAsync(IReadOnlyList<string> schemas, CancellationToken ct = default) =>
        LoadTablesAsync(schemas, ct);

    public async Task<ErdGraph> LoadTablesAsync(
        IReadOnlyList<string> schemas, CancellationToken ct = default)
    {
        using var session = MongoSession.Open(profile);
        var targets = schemas.Count > 0 ? schemas : await GetSchemasAsync(ct);

        var tables = new List<ErdTable>();
        foreach (var database in targets)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var collection in await session.ListCollectionsAsync(database, ct))
            {
                ct.ThrowIfCancellationRequested();
                var fields = await session.InferFieldsAsync(database, collection, ct: ct);
                // 스키마가 없으니 타입·NOT NULL 을 단정할 수 없다 — 이름만 싣는다.
                var columns = fields
                    .Select(f => new ErdColumn(f, "", NotNull: false, IsPk: f == "_id", IsFk: false))
                    .ToList();
                tables.Add(new ErdTable(database, collection, IsView: false, columns));
            }
        }

        return new ErdGraph(tables, []);
    }
}
