using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace PrismOne.Db.Core.Mongo;

/// <summary>한 번 실행한 결과 — 그리드에 넣을 표와 상태바에 쓸 요약.</summary>
public sealed record MongoResult(MongoTable Table, string Summary);

/// <summary>
/// 그리드 한 행이 실제로 어느 문서에서 왔는지 — Edit Document(Studio3T 대응)가
/// <c>_id</c> 로 정확히 되쓰기 위해 쓴다. <c>find</c>/<c>findOne</c> 결과에만 붙는다 —
/// <c>aggregate</c> 는 파이프라인이 문서를 재구성할 수 있어 원본을 그대로 대표하지
/// 않으므로 편집 대상에서 뺀다(SQL 의 "단일 테이블 SELECT 만 편집" 과 같은 이유).
/// </summary>
public sealed record MongoRowContext(string Database, string Collection, BsonDocument Document);

/// <summary>
/// Mongo 접속·실행. <c>QuerySession</c> 과 나란한 역할이지만 별도 타입이다 —
/// Mongo 드라이버는 ADO.NET(<c>DbConnection</c>/<c>DbDataReader</c>)이 아니라
/// 기존 SQL 경로를 재사용할 수 없다 (MULTI_DB_PLAN §2).
///
/// **타이핑한 명령은 읽기 전용이다** — drop·insert·update 같은 쓰기 연산은 셸 구문
/// 파서(<see cref="MongoQueryParser"/>)가 애초에 받지 않는다. 다만 그리드에서 문서 하나를
/// 고쳐 저장하는 것(Edit Document)은 SQL 의 Run and Edit 과 같은 급의 **구조화된 데이터
/// 편집**이라 별도 경로(<see cref="ReplaceDocumentAsync"/>)로 허용한다 — Studio 가
/// 막는 것은 스키마 패치이지(STATUS §2·3), 행 단위 데이터 편집이 아니다.
/// </summary>
public sealed class MongoSession : IDisposable
{
    /// <summary>한 번에 가져올 문서 수 상한 — 운영 컬렉션을 통째로 끌어오는 사고 방지.</summary>
    public const int DefaultLimit = 500;

    private readonly MongoClient _client;
    /// <summary>지금 조회 대상 DB. 안 정했으면 null — 예전처럼 "test" 로 조용히 넘어가지 않는다.</summary>
    private string? _databaseName;

    private MongoSession(MongoClient client, string? databaseName)
    {
        _client = client;
        _databaseName = databaseName;
    }

    public static MongoSession Open(ConnectionProfile profile)
    {
        var settings = MongoClientSettings.FromConnectionString(BuildConnectionString(profile));
        // 서버가 없을 때 몇 분씩 매달리지 않게 한다 (기본은 30초).
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);

        var client = new MongoClient(settings);
        var databaseName = string.IsNullOrWhiteSpace(profile.Database) ? null : profile.Database;
        return new MongoSession(client, databaseName);
    }

    /// <summary>지금 조회 대상 DB. 안 정했으면 null.</summary>
    public string? CurrentDatabase => _databaseName;

    /// <summary>
    /// 대상 데이터베이스를 정한다 — Explorer 에서 컬렉션을 고르거나 셸의 <c>use db</c> 명령으로.
    /// Mongo 는 없는 이름을 줘도 에러가 아니다(첫 쓰기 때 생기는 지연 생성) — 그대로 둔다.
    /// </summary>
    public void UseDatabase(string database) => _databaseName = database;

    /// <summary>
    /// 지금 조회할 DB. 안 정했으면 예전처럼 아무 DB(test)로 조용히 넘어가지 않고 여기서 바로
    /// 알린다 — 그렇게 두면 실제로는 다른 DB 에 있는 컬렉션을 빈 DB 에서 찾아 "0건"으로
    /// 착각하게 만든다(사일런트 오조회가 조용한 것보다 나쁘다).
    /// </summary>
    private IMongoDatabase RequireDatabase() => _databaseName is null
        ? throw new MongoQueryException(
            "먼저 데이터베이스를 선택하세요 — Explorer 에서 컬렉션을 더블클릭하거나 " +
            "\"use <데이터베이스 이름>\" 을 실행하세요.")
        : _client.GetDatabase(_databaseName);

    /// <summary>
    /// 접속 문자열. 비밀번호가 들어가므로 <b>로그·오류 메시지에 실으면 안 된다</b>.
    /// 사용자/비밀번호에 특수문자가 있어도 깨지지 않게 URI 인코딩한다.
    /// </summary>
    public static string BuildConnectionString(ConnectionProfile profile)
    {
        var host = string.IsNullOrWhiteSpace(profile.Host) ? "localhost" : profile.Host;
        var port = profile.Port > 0 ? profile.Port : 27017;
        var credentials = string.IsNullOrEmpty(profile.Username)
            ? ""
            : $"{Uri.EscapeDataString(profile.Username)}:{Uri.EscapeDataString(profile.Password)}@";
        return $"mongodb://{credentials}{host}:{port}";
    }

    /// <summary>
    /// 접속 확인 — 실패하면 드라이버 예외가 그대로 올라온다.
    /// ping 은 대상 DB 가 실존하는지와 무관해서(컬렉션을 안 건드린다) DB 를 안 정했어도
    /// 아무 이름으로나 보낼 수 있다 — admin 을 쓴다. 실제 조회(Find 등)와 달리
    /// "엉뚱한 DB 를 조용히 본다" 문제가 없다.
    /// </summary>
    public async Task PingAsync(CancellationToken ct = default) =>
        await _client.GetDatabase(_databaseName ?? "admin")
            .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);

    /// <summary>
    /// 서버의 데이터베이스 목록. Studio3T·DataGrip 처럼 <b>DB → 컬렉션</b> 트리를 그리려면
    /// 접속한 DB 하나가 아니라 서버 전체를 봐야 한다.
    /// 시스템 DB(admin/local/config)는 뺀다 — PG 카탈로그에서 pg_catalog 를 빼는 것과 같다.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListDatabaseNamesAsync(CancellationToken ct = default)
    {
        var cursor = await _client.ListDatabaseNamesAsync(ct);
        var names = await cursor.ToListAsync(ct);
        names.RemoveAll(n => SystemDatabases.Contains(n));
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private static readonly HashSet<string> SystemDatabases =
        new(["admin", "local", "config"], StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default) =>
        ListCollectionsAsync(RequireDatabase(), ct);

    /// <summary>접속한 DB 가 아닌 다른 DB 의 컬렉션 목록 (Explorer 트리용).</summary>
    public Task<IReadOnlyList<string>> ListCollectionsAsync(string database, CancellationToken ct = default) =>
        ListCollectionsAsync(_client.GetDatabase(database), ct);

    private static async Task<IReadOnlyList<string>> ListCollectionsAsync(
        IMongoDatabase database, CancellationToken ct)
    {
        var names = await database.ListCollectionNamesAsync(cancellationToken: ct);
        var list = await names.ToListAsync(ct);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// 컬렉션의 필드 이름을 <b>샘플에서 추론</b>한다. Mongo 는 스키마가 없어서
    /// 카탈로그를 읽을 수 없다 (MULTI_DB_PLAN §3: "컬렉션·샘플 기반 추론").
    /// 자동완성·브라우저용이라 정확할 필요는 없고 대표적이면 된다.
    /// </summary>
    public Task<IReadOnlyList<string>> InferFieldsAsync(
        string collection, int sampleSize = 50, CancellationToken ct = default) =>
        InferFieldsAsync(RequireDatabase(), collection, sampleSize, ct);

    /// <summary>다른 DB 의 컬렉션에서 필드를 추론한다 (Explorer 트리용).</summary>
    public Task<IReadOnlyList<string>> InferFieldsAsync(
        string database, string collection, int sampleSize = 50, CancellationToken ct = default) =>
        InferFieldsAsync(_client.GetDatabase(database), collection, sampleSize, ct);

    private static async Task<IReadOnlyList<string>> InferFieldsAsync(
        IMongoDatabase database, string collection, int sampleSize, CancellationToken ct)
    {
        var cursor = await database.GetCollection<BsonDocument>(collection)
            .Find(new BsonDocument())
            .Limit(sampleSize)
            .ToCursorAsync(ct);
        var documents = await cursor.ToListAsync(ct);
        return MongoDocuments.Flatten(documents).Columns;
    }

    public async Task<MongoResult> ExecuteAsync(
        string text, int limit = DefaultLimit, CancellationToken ct = default)
    {
        var command = MongoQueryParser.Parse(text);
        return command.Operation switch
        {
            MongoOperation.UseDatabase => RunUseDatabase(command),
            MongoOperation.ListCollections => await RunListCollectionsAsync(ct),
            MongoOperation.Find => await RunFindAsync(command, limit, ct),
            MongoOperation.Aggregate => await RunAggregateAsync(command, limit, ct),
            MongoOperation.CountDocuments => await RunCountAsync(command, ct),
            MongoOperation.Distinct => await RunDistinctAsync(command, ct),
            _ => throw new MongoQueryException($"{command.Operation} 는 아직 실행할 수 없습니다."),
        };
    }

    /// <summary>실 mongosh 처럼 이후 문장이 볼 DB 를 바꾼다 — 존재하지 않아도 오류가 아니다.</summary>
    private MongoResult RunUseDatabase(MongoCommand command)
    {
        UseDatabase(command.Argument!);
        return new MongoResult(new MongoTable(["database"], [[command.Argument]]), $"Using {command.Argument}");
    }

    private async Task<MongoResult> RunListCollectionsAsync(CancellationToken ct)
    {
        var names = await ListCollectionsAsync(ct);
        var rows = names.Select(n => new object?[] { n }).ToList();
        return new MongoResult(
            new MongoTable(["collection"], rows), $"{rows.Count} collection(s)");
    }

    private async Task<MongoResult> RunFindAsync(MongoCommand command, int limit, CancellationToken ct)
    {
        var find = RequireDatabase().GetCollection<BsonDocument>(command.Collection)
            .Find(command.Filter ?? new BsonDocument());

        if (command.Projection is not null) find = find.Project<BsonDocument>(command.Projection);
        if (command.Sort is not null) find = find.Sort(command.Sort);
        if (command.Skip is { } skip) find = find.Skip(skip);

        // 사용자가 건 limit 이 상한보다 크면 상한이 이긴다 — 사고 방지가 우선.
        var effective = Math.Min(command.Limit ?? limit, limit);
        var documents = await (await find.Limit(effective).ToCursorAsync(ct)).ToListAsync(ct);

        // projection 이 있으면 문서가 원본과 다를 수 있다(필드 누락 등) — 그대로 되쓰면
        // 화면에 없던 필드가 통째로 사라지므로 Edit Document 를 허용하지 않는다.
        var editContext = command.Projection is null ? (_databaseName!, command.Collection) : ((string, string)?)null;
        return Materialize(documents, effective, editContext);
    }

    private async Task<MongoResult> RunAggregateAsync(MongoCommand command, int limit, CancellationToken ct)
    {
        var stages = command.Pipeline!.Select(s => s.AsBsonDocument).ToList();
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);

        var cursor = await RequireDatabase().GetCollection<BsonDocument>(command.Collection)
            .AggregateAsync(pipeline, cancellationToken: ct);

        // $limit 이 파이프라인에 없을 수 있으므로 받는 쪽에서도 끊는다
        var documents = new List<BsonDocument>();
        while (documents.Count < limit && await cursor.MoveNextAsync(ct))
            foreach (var document in cursor.Current)
            {
                documents.Add(document);
                if (documents.Count >= limit) break;
            }

        return Materialize(documents, limit);
    }

    private async Task<MongoResult> RunCountAsync(MongoCommand command, CancellationToken ct)
    {
        var count = await RequireDatabase().GetCollection<BsonDocument>(command.Collection)
            .CountDocumentsAsync(command.Filter ?? new BsonDocument(), cancellationToken: ct);

        return new MongoResult(
            new MongoTable(["count"], [[count]]), $"{count:N0} document(s)");
    }

    private async Task<MongoResult> RunDistinctAsync(MongoCommand command, CancellationToken ct)
    {
        var cursor = await RequireDatabase().GetCollection<BsonDocument>(command.Collection)
            .DistinctAsync<BsonValue>(command.DistinctField,
                command.Filter ?? new BsonDocument(), cancellationToken: ct);
        var values = await cursor.ToListAsync(ct);

        var rows = values.Select(v => new object?[] { MongoDocuments.ToCell(v) }).ToList();
        return new MongoResult(
            new MongoTable([command.DistinctField!], rows), $"{rows.Count:N0} distinct value(s)");
    }

    private static MongoResult Materialize(
        List<BsonDocument> documents, int limit, (string Database, string Collection)? editContext = null)
    {
        var table = MongoDocuments.Flatten(documents);
        if (editContext is { } ctx)
            table = table with
            {
                RowContexts = documents.Select(d => new MongoRowContext(ctx.Database, ctx.Collection, d)).ToList(),
            };
        var summary = documents.Count >= limit
            ? $"{documents.Count:N0} document(s) (limit reached)"
            : $"{documents.Count:N0} document(s)";
        return new MongoResult(table, summary);
    }

    /// <summary>
    /// Edit Document 저장 — <c>_id</c> 로 찾아 통째로 바꾼다. 다른 곳에서 먼저 지웠으면
    /// (매치 0건) 조용히 넘어가지 않고 알린다 — 사라진 문서를 "저장됐다"고 오인하면 안 된다.
    /// </summary>
    public async Task ReplaceDocumentAsync(
        string database, string collection, BsonValue id, BsonDocument updated, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var result = await _client.GetDatabase(database).GetCollection<BsonDocument>(collection)
            .ReplaceOneAsync(filter, updated, cancellationToken: ct);
        if (result.MatchedCount == 0)
            throw new MongoQueryException(
                "문서를 찾지 못했습니다 — 다른 곳에서 먼저 지웠거나 바뀌었을 수 있습니다. 다시 조회해 주세요.");
    }

    /// <summary>Add Document 저장 — 문서를 그대로 넣는다. <c>_id</c> 를 안 적으면 Mongo 가 만든다.</summary>
    public async Task InsertDocumentAsync(
        string database, string collection, BsonDocument document, CancellationToken ct = default) =>
        await _client.GetDatabase(database).GetCollection<BsonDocument>(collection)
            .InsertOneAsync(document, cancellationToken: ct);

    /// <summary>
    /// Delete Document 저장 — <c>_id</c> 로 하나 지운다. 이미 없으면(매치 0건) 조용히
    /// 넘어가지 않고 알린다 — 다른 곳에서 먼저 지운 것을 "지웠다"고 오인하면 안 된다.
    /// </summary>
    public async Task DeleteDocumentAsync(
        string database, string collection, BsonValue id, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var result = await _client.GetDatabase(database).GetCollection<BsonDocument>(collection)
            .DeleteOneAsync(filter, cancellationToken: ct);
        if (result.DeletedCount == 0)
            throw new MongoQueryException("문서를 찾지 못했습니다 — 다른 곳에서 먼저 지웠을 수 있습니다.");
    }

    public void Dispose() => _client.Dispose();
}
