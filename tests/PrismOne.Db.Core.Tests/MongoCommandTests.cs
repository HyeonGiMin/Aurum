using MongoDB.Bson;
using PrismOne.Db.Core.Mongo;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// Mongo 셸 구문 파서 — 서버 없이 검증할 수 있는 순수 로직이라 여기서 다 잡는다.
/// (MULTI_DB_PLAN §4: 검증 가능한 것부터)
/// </summary>
public class MongoCommandTests
{
    [Fact]
    public void Parse_ReadsCollectionAndFilter_ForFind()
    {
        var command = MongoQueryParser.Parse("db.people.find({ age: { $gt: 20 } })");

        Assert.Equal(MongoOperation.Find, command.Operation);
        Assert.Equal("people", command.Collection);
        Assert.Equal(20, command.Filter!["age"]["$gt"].AsInt32);
    }

    [Fact]
    public void Parse_ReadsProjection_AsSecondArgument()
    {
        var command = MongoQueryParser.Parse("db.people.find({}, { name: 1, _id: 0 })");

        Assert.Equal(0, command.Filter!.ElementCount);
        Assert.Equal(1, command.Projection!["name"].AsInt32);
        Assert.Equal(0, command.Projection["_id"].AsInt32);
    }

    [Fact]
    public void Parse_AppliesChainedLimitSkipAndSort()
    {
        var command = MongoQueryParser.Parse("db.people.find({}).limit(10).skip(5).sort({ name: -1 })");

        Assert.Equal(10, command.Limit);
        Assert.Equal(5, command.Skip);
        Assert.Equal(-1, command.Sort!["name"].AsInt32);
    }

    [Fact]
    public void Parse_TreatsFindOne_AsLimitOne()
    {
        var command = MongoQueryParser.Parse("db.people.findOne({ _id: 1 })");

        Assert.Equal(MongoOperation.Find, command.Operation);
        Assert.Equal(1, command.Limit);
    }

    [Fact]
    public void Parse_ReadsPipeline_ForAggregate()
    {
        var command = MongoQueryParser.Parse(
            "db.orders.aggregate([{ $match: { status: 'A' } }, { $group: { _id: '$cust', n: { $sum: 1 } } }])");

        Assert.Equal(MongoOperation.Aggregate, command.Operation);
        Assert.Equal("orders", command.Collection);
        Assert.Equal(2, command.Pipeline!.Count);
        Assert.Equal("A", command.Pipeline[0]["$match"]["status"].AsString);
    }

    [Fact]
    public void Parse_ReadsDistinctFieldAndFilter()
    {
        var command = MongoQueryParser.Parse("db.people.distinct('city', { active: true })");

        Assert.Equal(MongoOperation.Distinct, command.Operation);
        Assert.Equal("city", command.DistinctField);
        Assert.True(command.Filter!["active"].AsBoolean);
    }

    [Fact]
    public void Parse_ReadsCountDocuments()
    {
        var command = MongoQueryParser.Parse("db.people.countDocuments({})");

        Assert.Equal(MongoOperation.CountDocuments, command.Operation);
        Assert.Equal("people", command.Collection);
    }

    [Theory]
    [InlineData("use mydb")]
    [InlineData("use   mydb  ")]
    [InlineData("USE mydb")]
    [InlineData("use 'mydb'")]
    [InlineData("use \"mydb\"")]
    public void Parse_RecognizesUseDatabase(string text)
    {
        var command = MongoQueryParser.Parse(text);

        Assert.Equal(MongoOperation.UseDatabase, command.Operation);
        Assert.Equal("mydb", command.Argument);
    }

    [Fact]
    public void Parse_Throws_WhenUseHasNoDatabaseName() =>
        Assert.Throws<MongoQueryException>(() => MongoQueryParser.Parse("use"));

    [Fact]
    public void Parse_RecognizesShowCollections()
    {
        var command = MongoQueryParser.Parse("show collections");

        Assert.Equal(MongoOperation.ListCollections, command.Operation);
    }

    // ---- 구분자 오탐 방지: 문자열·중첩 안의 점과 쉼표는 구분자가 아니다 ----

    [Fact]
    public void Parse_IgnoresDotsInsideStringValues()
    {
        var command = MongoQueryParser.Parse("db.people.find({ host: 'a.b.c' })");

        Assert.Equal("people", command.Collection);
        Assert.Equal("a.b.c", command.Filter!["host"].AsString);
    }

    [Fact]
    public void Parse_IgnoresCommasInsideNestedDocuments()
    {
        var command = MongoQueryParser.Parse("db.people.find({ a: { b: 1, c: 2 } })");

        // 인자가 하나로 유지돼야 projection 이 잘못 잡히지 않는다
        Assert.Null(command.Projection);
        Assert.Equal(2, command.Filter!["a"]["c"].AsInt32);
    }

    [Fact]
    public void Parse_KeepsDottedFieldPathsInFilter()
    {
        var command = MongoQueryParser.Parse("db.people.find({ 'address.city': 'Seoul' })");

        Assert.Equal("Seoul", command.Filter!["address.city"].AsString);
    }

    [Fact]
    public void Parse_StripsLineAndBlockComments()
    {
        var command = MongoQueryParser.Parse("""
            // 활성 사용자만
            db.people.find({ active: true }) /* 뒤쪽 주석 */
            """);

        Assert.True(command.Filter!["active"].AsBoolean);
    }

    [Fact]
    public void Parse_TrimsTrailingSemicolonAndToArray()
    {
        var command = MongoQueryParser.Parse("db.people.find({}).toArray();");

        Assert.Equal(MongoOperation.Find, command.Operation);
    }

    // ---- 오류는 사용자에게 보일 메시지로 ----

    [Theory]
    [InlineData("")]
    [InlineData("select * from people")]
    [InlineData("db.people")]
    [InlineData("db.people.find({")]
    [InlineData("db.people.drop()")]
    [InlineData("db.people.find({}).limit(abc)")]
    [InlineData("db.people.find({ bad json })")]
    public void Parse_Throws_ForUnsupportedOrMalformedInput(string text) =>
        Assert.Throws<MongoQueryException>(() => MongoQueryParser.Parse(text));

    [Fact]
    public void Parse_Throws_ForNegativeLimit() =>
        Assert.Throws<MongoQueryException>(() => MongoQueryParser.Parse("db.people.find({}).limit(-1)"));
}
