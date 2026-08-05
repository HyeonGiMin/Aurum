using System;
using System.Data;
using System.Data.Common;
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
/// ADO.NET 셰임 검증. 여기서는 <b>일부러 <see cref="DbConnection"/> 타입으로만</b> 다룬다 —
/// 셰임의 목적이 "나머지 앱이 Mongo 를 다른 DB 와 똑같이 본다" 이므로,
/// 구체 타입을 알아야만 동작한다면 목적을 못 이룬 것이다.
///
/// 서버가 필요하므로 <c>AURUM_MONGO_TEST_HOST</c> 가 있을 때만 돈다.
/// </summary>
public class MongoAdoTests
{
    private static string? Host => Environment.GetEnvironmentVariable("AURUM_MONGO_TEST_HOST");

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("AURUM_MONGO_TEST_PORT"), out var p) ? p : 27017;

    private static ConnectionProfile Profile(string database) =>
        new(Host!, Port, database, "", "", ReadOnly: false, Kind: DbKind.MongoDb);

    private static async Task<string> SeedAsync()
    {
        var database = $"aurum_ado_{Guid.NewGuid():N}";
        var people = new MongoClient($"mongodb://{Host}:{Port}")
            .GetDatabase(database).GetCollection<BsonDocument>("people");

        await people.InsertManyAsync([
            BsonDocument.Parse("{ _id: 1, name: 'a', age: 30, address: { city: 'Seoul' } }"),
            BsonDocument.Parse("{ _id: 2, name: 'b', age: 20 }"),
        ]);
        return database;
    }

    private static async Task DropAsync(string database) =>
        await new MongoClient($"mongodb://{Host}:{Port}").DropDatabaseAsync(database);

    [Fact]
    public void Registry_ExposesMongo_AsSupported()
    {
        // 이건 서버 없이도 돈다 — 등록 여부만 본다
        Assert.True(DbProviders.IsSupported(DbKind.MongoDb));
        Assert.Equal("MongoDB", DbProviders.For(DbKind.MongoDb).DisplayName);
    }

    [Fact]
    public void Capabilities_DisableFeaturesMongoLacks()
    {
        var capabilities = DbProviders.For(DbKind.MongoDb).Capabilities;

        // UI 가 이걸 보고 버튼을 끈다 — "버튼은 있는데 안 되는" 상태를 막는 장치
        Assert.False(capabilities.Transactions);
        Assert.False(capabilities.ForeignKeys);
        Assert.False(capabilities.GridEditing);
        Assert.False(capabilities.BulkExport);
    }

    [Fact]
    public void Describe_OmitsPassword()
    {
        var described = DbProviders.For(DbKind.MongoDb)
            .Describe(new ConnectionProfile("h", 27017, "d", "u", "secret", Kind: DbKind.MongoDb));

        Assert.DoesNotContain("secret", described);
    }

    [Fact]
    public async Task OpenAsync_ReturnsUsableDbConnection()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            // DbConnection 으로만 잡는다 — 구체 타입에 기대지 않는다
            await using DbConnection connection =
                await DbProviders.For(DbKind.MongoDb).OpenAsync(Profile(database));

            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.DoesNotContain("secret", connection.ConnectionString);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task ExecuteReader_YieldsColumnsAndRows_ThroughAdoSurface()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            await using DbConnection connection =
                await DbProviders.For(DbKind.MongoDb).OpenAsync(Profile(database));

            // QuerySession 이 쓰는 것과 똑같은 순서: CreateCommand → ExecuteReaderAsync → Read
            var command = connection.CreateCommand();
            command.CommandText = "db.people.find({}).sort({ age: 1 })";

            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(reader.FieldCount >= 3);
            Assert.Equal("_id", reader.GetName(0));

            Assert.True(await reader.ReadAsync());
            var age = reader.GetOrdinal("age");
            Assert.Equal(20, reader.GetValue(age));

            Assert.True(await reader.ReadAsync());
            Assert.Equal(30, reader.GetValue(age));

            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Reader_ReportsMissingFields_AsDbNull()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            await using DbConnection connection =
                await DbProviders.For(DbKind.MongoDb).OpenAsync(Profile(database));

            // 컬럼은 "돌아온 문서들"의 합집합이다 — 2번만 조회하면 address.city 는
            // 아예 컬럼이 되지 않는다. 빈 칸을 보려면 둘 다 가져와야 한다.
            var command = connection.CreateCommand();
            command.CommandText = "db.people.find({}).sort({ age: 1 })";

            await using var reader = await command.ExecuteReaderAsync();

            var city = reader.GetOrdinal("address.city");

            // age 20 = 2번 문서. address 가 없으므로 그리드가 빈 칸으로 그려야 한다.
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(city));
            Assert.Equal(DBNull.Value, reader.GetValue(city));

            // age 30 = 1번 문서에는 값이 있다
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.IsDBNull(city));
            Assert.Equal("Seoul", reader.GetValue(city));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task ExecuteScalar_ReturnsFirstCell()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            await using DbConnection connection =
                await DbProviders.For(DbKind.MongoDb).OpenAsync(Profile(database));

            var command = connection.CreateCommand();
            command.CommandText = "db.people.countDocuments({})";

            Assert.Equal(2L, await command.ExecuteScalarAsync());
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>
    /// DB 를 안 정하고 접속한 뒤 `use db` 를 실행하면(Explorer 더블클릭과 같은 경로,
    /// QueryTabView.TryUseDatabase → ChangeDatabase) 재접속 없이 그 DB 를 정확히 본다.
    /// </summary>
    [Fact]
    public async Task ChangeDatabase_SwitchesTarget_WithoutReconnecting()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            await using DbConnection connection =
                await DbProviders.For(DbKind.MongoDb).OpenAsync(Profile(""));

            var findBefore = connection.CreateCommand();
            findBefore.CommandText = "db.people.find({})";
            await Assert.ThrowsAsync<MongoQueryException>(() => findBefore.ExecuteReaderAsync());

            connection.ChangeDatabase(database);

            var findAfter = connection.CreateCommand();
            findAfter.CommandText = "db.people.countDocuments({})";
            Assert.Equal(2L, await findAfter.ExecuteScalarAsync());
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>
    /// QueryTabView.EditSelectedDocumentAsync 가 실제로 타는 다리 —
    /// MongoDbConnection.ReplaceDocumentAsync 가 내부 세션에 위임해 정확히 저장한다.
    /// </summary>
    [Fact]
    public async Task ReplaceDocumentAsync_ThroughConnection_UpdatesDocument()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            await using var connection =
                (MongoDbConnection)await DbProviders.For(DbKind.MongoDb).OpenAsync(Profile(database));

            var updated = BsonDocument.Parse("{ _id: 1, name: 'a-renamed' }");
            await connection.ReplaceDocumentAsync(database, "people", new BsonInt32(1), updated);

            var command = connection.CreateCommand();
            command.CommandText = "db.people.find({ _id: 1 })";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            Assert.Equal("a-renamed", reader.GetValue(reader.GetOrdinal("name")));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task ErdCatalog_ListsCollections_WithNoRelations()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            var catalog = DbProviders.For(DbKind.MongoDb).CreateErdCatalog(Profile(database));
            var graph = await catalog.LoadAsync([database]);

            // 스키마 자리에 데이터베이스 이름이 온다 (Studio3T 의 DB → 컬렉션 트리)
            Assert.Contains(graph.Tables, t => t.Schema == database && t.Name == "people");
            Assert.Empty(graph.Relations);   // Mongo 에는 FK 가 없다
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task ErdCatalog_ListsDatabases_AsSchemas_ExcludingSystemOnes()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            var catalog = DbProviders.For(DbKind.MongoDb).CreateErdCatalog(Profile(database));
            var schemas = await catalog.GetSchemasAsync();

            Assert.Contains(database, schemas);
            // admin/local/config 는 잡음이라 뺀다
            Assert.DoesNotContain("admin", schemas);
            Assert.DoesNotContain("local", schemas);
            Assert.DoesNotContain("config", schemas);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>
    /// DB 를 <b>안 적고</b> 접속하면(host:port 만) 서버의 DB 가 전부 보여야 한다 —
    /// "/test 없이 접속했더니 Explorer 가 비어 있다" 를 막는 회귀 테스트.
    /// </summary>
    [Fact]
    public async Task ErdCatalog_ListsAllDatabases_WhenNoDatabaseGiven()
    {
        if (Host is null) return;
        var database = await SeedAsync();
        try
        {
            var catalog = DbProviders.For(DbKind.MongoDb).CreateErdCatalog(Profile(""));
            var graph = await catalog.LoadTablesAsync(await catalog.GetSchemasAsync());

            Assert.Contains(graph.Tables, t => t.Schema == database && t.Name == "people");
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>DB 를 적었으면 그 DB 만 보여야 한다 (서버 전체를 쏟아내지 않는다).</summary>
    [Fact]
    public async Task ErdCatalog_LimitsToGivenDatabase_WhenSpecified()
    {
        if (Host is null) return;
        var first = await SeedAsync();
        var second = await SeedAsync();
        try
        {
            var catalog = DbProviders.For(DbKind.MongoDb).CreateErdCatalog(Profile(first));
            var schemas = await catalog.GetSchemasAsync();

            Assert.Equal([first], schemas);
            Assert.DoesNotContain(second, schemas);
        }
        finally
        {
            await DropAsync(first);
            await DropAsync(second);
        }
    }
}
