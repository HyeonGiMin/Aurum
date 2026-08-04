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
    /// <summary>Mongo 에는 스키마가 없다 — 컬렉션이 곧 최상위다. 표시용 이름.</summary>
    public const string CollectionsSchema = "collections";

    public DbKind Kind => DbKind.MongoDb;

    public string DisplayName => "MongoDB";

    public DbCapabilities Capabilities { get; } = new(
        Transactions: false,      // replica set 이 있어야 하고 조회 전용이라 쓰지 않는다
        IsolationLevels: false,
        GridEditing: false,       // _id 기준 편집은 아직 (MULTI_DB_PLAN 3단계 잔여)
        ExplainPlan: false,       // explain() 은 아직
        ServerMessages: false,
        SessionMonitor: false,    // currentOp 은 아직
        BulkExport: false,        // COPY 같은 서버측 내보내기 없음 — 행 단위
        ForeignKeys: false,       // FK 개념이 없다 → ERD 관계선 없음
        Schemas: false);

    public IReadOnlyList<TransactionIsolation> SupportedIsolations { get; } = [];

    /// <summary>행 특정용 의사 컬럼이 없다 (편집을 켜면 <c>_id</c> 를 써야 한다 — 아직 미지원).</summary>
    public string? RowIdColumn => null;

    public string? BeginTransactionSql => null;

    public string? SessionIsolationSql(TransactionIsolation level) => null;

    /// <summary>
    /// 표시·진단용. <b>비밀번호를 담지 않는다</b> —
    /// 실제 접속에 쓰는 문자열은 <see cref="MongoSession.BuildConnectionString"/> 이 만든다.
    /// </summary>
    public string BuildConnectionString(ConnectionProfile profile) =>
        $"mongodb://{profile.Host}:{profile.Port}/{profile.Database}";

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
        return $"{who}{profile.Host}:{profile.Port}/{profile.Database}";
    }

    public IErdCatalog CreateErdCatalog(ConnectionProfile profile) => new MongoErdCatalog(profile);
}

/// <summary>
/// 컬렉션 목록과 <b>샘플에서 추론한 필드</b>를 ERD 그래프로 준다.
/// Mongo 에는 FK 가 없으므로 <b>관계는 항상 비어 있다</b> — 상자만 있는 그림이 되며,
/// ERD 창이 "FK 제약이 없어 관계선이 없습니다" 로 알린다.
/// Object Browser·자동완성에는 이걸로 충분하다.
/// </summary>
public sealed class MongoErdCatalog(ConnectionProfile profile) : IErdCatalog
{
    public Task<List<string>> GetSchemasAsync(CancellationToken ct = default) =>
        Task.FromResult<List<string>>([MongoProvider.CollectionsSchema]);

    public Task<ErdGraph> LoadAsync(IReadOnlyList<string> schemas, CancellationToken ct = default) =>
        LoadTablesAsync(schemas, ct);

    public async Task<ErdGraph> LoadTablesAsync(
        IReadOnlyList<string> schemas, CancellationToken ct = default)
    {
        using var session = MongoSession.Open(profile);
        var collections = await session.ListCollectionsAsync(ct);

        var tables = new List<ErdTable>(collections.Count);
        foreach (var collection in collections)
        {
            ct.ThrowIfCancellationRequested();
            var fields = await session.InferFieldsAsync(collection, ct: ct);
            // 스키마가 없으니 타입·NOT NULL 을 단정할 수 없다 — 이름만 싣는다.
            var columns = fields
                .Select(f => new ErdColumn(f, "", NotNull: false, IsPk: f == "_id", IsFk: false))
                .ToList();
            tables.Add(new ErdTable(MongoProvider.CollectionsSchema, collection, IsView: false, columns));
        }

        return new ErdGraph(tables, []);
    }
}
