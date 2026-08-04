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
/// Mongo 접속·실행. <c>QuerySession</c> 과 나란한 역할이지만 별도 타입이다 —
/// Mongo 드라이버는 ADO.NET(<c>DbConnection</c>/<c>DbDataReader</c>)이 아니라
/// 기존 SQL 경로를 재사용할 수 없다 (MULTI_DB_PLAN §2).
///
/// 읽기 전용이다. drop·insert·update 같은 쓰기 연산은 파서가 애초에 받지 않는다 —
/// Studio 는 조회·관리 전용이라는 원칙(STATUS §2·3)을 여기서도 지킨다.
/// </summary>
public sealed class MongoSession : IDisposable
{
    /// <summary>한 번에 가져올 문서 수 상한 — 운영 컬렉션을 통째로 끌어오는 사고 방지.</summary>
    public const int DefaultLimit = 500;

    private readonly IMongoDatabase _database;
    private readonly MongoClient _client;

    private MongoSession(MongoClient client, IMongoDatabase database)
    {
        _client = client;
        _database = database;
    }

    public static MongoSession Open(ConnectionProfile profile)
    {
        var settings = MongoClientSettings.FromConnectionString(BuildConnectionString(profile));
        // 서버가 없을 때 몇 분씩 매달리지 않게 한다 (기본은 30초).
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);

        var client = new MongoClient(settings);
        var databaseName = string.IsNullOrWhiteSpace(profile.Database) ? "test" : profile.Database;
        return new MongoSession(client, client.GetDatabase(databaseName));
    }

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

    /// <summary>접속 확인 — 실패하면 드라이버 예외가 그대로 올라온다.</summary>
    public async Task PingAsync(CancellationToken ct = default) =>
        await _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);

    public async Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default)
    {
        var names = await _database.ListCollectionNamesAsync(cancellationToken: ct);
        var list = await names.ToListAsync(ct);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// 컬렉션의 필드 이름을 <b>샘플에서 추론</b>한다. Mongo 는 스키마가 없어서
    /// 카탈로그를 읽을 수 없다 (MULTI_DB_PLAN §3: "컬렉션·샘플 기반 추론").
    /// 자동완성·브라우저용이라 정확할 필요는 없고 대표적이면 된다.
    /// </summary>
    public async Task<IReadOnlyList<string>> InferFieldsAsync(
        string collection, int sampleSize = 50, CancellationToken ct = default)
    {
        var cursor = await _database.GetCollection<BsonDocument>(collection)
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
            MongoOperation.ListCollections => await RunListCollectionsAsync(ct),
            MongoOperation.Find => await RunFindAsync(command, limit, ct),
            MongoOperation.Aggregate => await RunAggregateAsync(command, limit, ct),
            MongoOperation.CountDocuments => await RunCountAsync(command, ct),
            MongoOperation.Distinct => await RunDistinctAsync(command, ct),
            _ => throw new MongoQueryException($"{command.Operation} 는 아직 실행할 수 없습니다."),
        };
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
        var find = _database.GetCollection<BsonDocument>(command.Collection)
            .Find(command.Filter ?? new BsonDocument());

        if (command.Projection is not null) find = find.Project<BsonDocument>(command.Projection);
        if (command.Sort is not null) find = find.Sort(command.Sort);
        if (command.Skip is { } skip) find = find.Skip(skip);

        // 사용자가 건 limit 이 상한보다 크면 상한이 이긴다 — 사고 방지가 우선.
        var effective = Math.Min(command.Limit ?? limit, limit);
        var documents = await (await find.Limit(effective).ToCursorAsync(ct)).ToListAsync(ct);

        return Materialize(documents, effective);
    }

    private async Task<MongoResult> RunAggregateAsync(MongoCommand command, int limit, CancellationToken ct)
    {
        var stages = command.Pipeline!.Select(s => s.AsBsonDocument).ToList();
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);

        var cursor = await _database.GetCollection<BsonDocument>(command.Collection)
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
        var count = await _database.GetCollection<BsonDocument>(command.Collection)
            .CountDocumentsAsync(command.Filter ?? new BsonDocument(), cancellationToken: ct);

        return new MongoResult(
            new MongoTable(["count"], [[count]]), $"{count:N0} document(s)");
    }

    private async Task<MongoResult> RunDistinctAsync(MongoCommand command, CancellationToken ct)
    {
        var cursor = await _database.GetCollection<BsonDocument>(command.Collection)
            .DistinctAsync<BsonValue>(command.DistinctField,
                command.Filter ?? new BsonDocument(), cancellationToken: ct);
        var values = await cursor.ToListAsync(ct);

        var rows = values.Select(v => new object?[] { MongoDocuments.ToCell(v) }).ToList();
        return new MongoResult(
            new MongoTable([command.DistinctField!], rows), $"{rows.Count:N0} distinct value(s)");
    }

    private static MongoResult Materialize(List<BsonDocument> documents, int limit)
    {
        var table = MongoDocuments.Flatten(documents);
        var summary = documents.Count >= limit
            ? $"{documents.Count:N0} document(s) (limit reached)"
            : $"{documents.Count:N0} document(s)";
        return new MongoResult(table, summary);
    }

    public void Dispose() => _client.Dispose();
}
