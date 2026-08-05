using PrismOne.Db.Core.Mongo;
using Xunit;
using MongoQueryException = PrismOne.Db.Core.Mongo.MongoQueryException;

namespace PrismOne.Db.Core.Tests;

/// <summary>Import JSON 파서 — 서버 없이 검증 가능한 순수 로직.</summary>
public class MongoJsonImportTests
{
    [Fact]
    public void Parse_ReadsJsonArray()
    {
        var documents = MongoJsonImport.Parse("""[{ "name": "a" }, { "name": "b" }]""");

        Assert.Equal(2, documents.Count);
        Assert.Equal("a", documents[0]["name"].AsString);
        Assert.Equal("b", documents[1]["name"].AsString);
    }

    [Fact]
    public void Parse_ReadsJsonLines_MongoexportDefaultFormat()
    {
        var documents = MongoJsonImport.Parse("""
            { "name": "a" }
            { "name": "b" }
            """);

        Assert.Equal(2, documents.Count);
        Assert.Equal("a", documents[0]["name"].AsString);
    }

    [Fact]
    public void Parse_SkipsBlankLines_InJsonLines()
    {
        var documents = MongoJsonImport.Parse("{ \"name\": \"a\" }\n\n\n{ \"name\": \"b\" }\n");

        Assert.Equal(2, documents.Count);
    }

    [Fact]
    public void Parse_ReturnsEmpty_ForBlankInput() =>
        Assert.Empty(MongoJsonImport.Parse("   "));

    [Fact]
    public void Parse_Throws_ForMalformedJsonArray() =>
        Assert.Throws<MongoQueryException>(() => MongoJsonImport.Parse("[{ bad json }]"));

    [Fact]
    public void Parse_Throws_ForMalformedJsonLine() =>
        Assert.Throws<MongoQueryException>(() => MongoJsonImport.Parse("{ \"ok\": 1 }\n{ bad }"));

    [Fact]
    public void Parse_KeepsNestedDocuments_InArray()
    {
        var documents = MongoJsonImport.Parse(
            """[{ "name": "a", "address": { "city": "Seoul" } }]""");

        Assert.Equal("Seoul", documents[0]["address"]["city"].AsString);
    }
}
