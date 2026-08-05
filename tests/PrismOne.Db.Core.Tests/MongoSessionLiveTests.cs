using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;
using PrismOne.Db.Core.Providers;
using Xunit;
// MongoDB.Driver 도 같은 이름의 예외를 갖고 있어 앨리어스로 우리 타입을 명확히 한다.
using MongoQueryException = PrismOne.Db.Core.Mongo.MongoQueryException;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// 실서버 검증. Mongo 는 SQLite 와 달리 파일 DB 가 아니라 서버가 필요하므로
/// <c>AURUM_MONGO_TEST_HOST</c> 가 있을 때만 돈다 (없으면 조용히 통과) —
/// 접속 정보를 코드에 박지 않기 위한 것이다.
/// 새 패키지(Xunit.SkippableFact)를 들이지 않으려고 조기 반환으로 처리한다.
///
/// 로컬에서 띄우는 법:
///   docker run -d --name aurum-mongo-test -p 127.0.0.1:27017:27017 mongo:7
///   $env:AURUM_MONGO_TEST_HOST = "localhost"
/// </summary>
public class MongoSessionLiveTests
{
    private static string? Host => Environment.GetEnvironmentVariable("AURUM_MONGO_TEST_HOST");

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("AURUM_MONGO_TEST_PORT"), out var p) ? p : 27017;

    /// <summary>테스트마다 제 DB 를 써서 서로 간섭하지 않게 한다.</summary>
    private static ConnectionProfile Profile(string database) =>
        new(Host!, Port, database, "", "", ReadOnly: false, Kind: DbKind.MongoDb);

    /// <summary>합성 데이터를 넣고 세션을 연다. 반환된 이름의 DB 는 끝나고 지운다.</summary>
    private static async Task<(MongoSession Session, string Database)> SeedAsync()
    {
        var database = $"aurum_test_{Guid.NewGuid():N}";
        var client = new MongoClient($"mongodb://{Host}:{Port}");
        var people = client.GetDatabase(database).GetCollection<BsonDocument>("people");

        await people.InsertManyAsync([
            BsonDocument.Parse("{ _id: 1, name: 'a', age: 30, address: { city: 'Seoul' }, tags: ['x'] }"),
            BsonDocument.Parse("{ _id: 2, name: 'b', age: 20, address: { city: 'Busan' } }"),
            BsonDocument.Parse("{ _id: 3, name: 'c', age: 40, address: { city: 'Seoul' } }"),
        ]);

        return (MongoSession.Open(Profile(database)), database);
    }

    private static async Task DropAsync(string database) =>
        await new MongoClient($"mongodb://{Host}:{Port}").DropDatabaseAsync(database);

    [Fact]
    public async Task Execute_Find_ReturnsFlattenedDocuments()
    {
        if (Host is null) return;
        var (session, database) = await SeedAsync();
        try
        {
            var result = await session.ExecuteAsync("db.people.find({ age: { $gt: 25 } }).sort({ age: 1 })");

            Assert.Equal(2, result.Table.Rows.Count);
            Assert.Contains("address.city", result.Table.Columns);
            // sort 가 걸렸으므로 30 이 먼저다
            var age = result.Table.Columns.ToList().IndexOf("age");
            Assert.Equal(30, result.Table.Rows[0][age]);
        }
        finally
        {
            session.Dispose();
            await DropAsync(database);
        }
    }

    /// <summary>
    /// DB 를 안 정하고(host:port 만 접속) 바로 조회하면 예전처럼 아무 DB(test)로 조용히
    /// 넘어가지 않고 바로 알려야 한다 — 안 그러면 실제로는 다른 DB 에 있는 컬렉션을
    /// 엉뚱한 빈 DB 에서 찾아 "0건"으로 착각하게 만든다.
    /// </summary>
    [Fact]
    public async Task Execute_WithoutDatabaseSelected_ThrowsInsteadOfSilentlyQueryingSomeDefault()
    {
        if (Host is null) return;
        using var session = MongoSession.Open(Profile(""));

        var ex = await Assert.ThrowsAsync<MongoQueryException>(
            () => session.ExecuteAsync("db.people.find({})"));
        Assert.Contains("데이터베이스", ex.Message);
    }

    /// <summary>실 mongosh 의 `use db` 로 DB 를 정한 뒤에는 그 DB 를 정확히 본다.</summary>
    [Fact]
    public async Task Execute_UseDatabase_ThenFind_TargetsThatDatabase()
    {
        if (Host is null) return;
        var (session, database) = await SeedAsync();
        try
        {
            using var bare = MongoSession.Open(Profile(""));
            Assert.Null(bare.CurrentDatabase);

            await bare.ExecuteAsync($"use {database}");
            Assert.Equal(database, bare.CurrentDatabase);

            var result = await bare.ExecuteAsync("db.people.countDocuments({})");
            Assert.Equal(3L, result.Table.Rows[0][0]);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Execute_Aggregate_GroupsDocuments()
    {
        if (Host is null) return;
        var (session, database) = await SeedAsync();
        try
        {
            var result = await session.ExecuteAsync(
                "db.people.aggregate([{ $group: { _id: '$address.city', n: { $sum: 1 } } }])");

            Assert.Equal(2, result.Table.Rows.Count);      // Seoul, Busan
            Assert.Contains("n", result.Table.Columns);
        }
        finally
        {
            session.Dispose();
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Execute_CountAndDistinct()
    {
        if (Host is null) return;
        var (session, database) = await SeedAsync();
        try
        {
            var count = await session.ExecuteAsync("db.people.countDocuments({})");
            Assert.Equal(3L, count.Table.Rows[0][0]);

            var distinct = await session.ExecuteAsync("db.people.distinct('address.city')");
            Assert.Equal(2, distinct.Table.Rows.Count);
        }
        finally
        {
            session.Dispose();
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Execute_ListCollections_ShowsSeededCollection()
    {
        if (Host is null) return;
        var (session, database) = await SeedAsync();
        try
        {
            var result = await session.ExecuteAsync("show collections");

            Assert.Contains(result.Table.Rows, r => Equals(r[0], "people"));
        }
        finally
        {
            session.Dispose();
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task InferFields_ReadsFieldNamesFromSample()
    {
        if (Host is null) return;
        var (session, database) = await SeedAsync();
        try
        {
            var fields = await session.InferFieldsAsync("people");

            Assert.Contains("name", fields);
            Assert.Contains("address.city", fields);
        }
        finally
        {
            session.Dispose();
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Execute_RespectsLimit_AndSaysSoInSummary()
    {
        if (Host is null) return;
        var (session, database) = await SeedAsync();
        try
        {
            var result = await session.ExecuteAsync("db.people.find({})", limit: 2);

            Assert.Equal(2, result.Table.Rows.Count);
            Assert.Contains("limit reached", result.Summary);
        }
        finally
        {
            session.Dispose();
            await DropAsync(database);
        }
    }

    /// <summary>이건 서버가 없어도 돈다 — 문자열 조립 규칙이라서.</summary>
    [Fact]
    public void BuildConnectionString_EscapesCredentials_AndOmitsThemWhenAnonymous()
    {
        var anonymous = MongoSession.BuildConnectionString(
            new ConnectionProfile("h", 27017, "d", "", "", Kind: DbKind.MongoDb));
        Assert.Equal("mongodb://h:27017", anonymous);

        // 비밀번호에 @ 나 : 가 있어도 URI 가 깨지면 안 된다
        var withCredentials = MongoSession.BuildConnectionString(
            new ConnectionProfile("h", 27017, "d", "us er", "p@ss:word", Kind: DbKind.MongoDb));
        Assert.Equal("mongodb://us%20er:p%40ss%3Aword@h:27017", withCredentials);
    }
}
