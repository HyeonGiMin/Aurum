using MongoDB.Bson;
using PrismOne.Db.Core.Mongo;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>Tree View 변환 — 순수 로직이라 서버 없이 다 잡는다.</summary>
public class MongoTreeTests
{
    private static readonly BsonDocument Sample = BsonDocument.Parse("""
        { _id: 7, name: "kim", age: 30,
          address: { city: "Seoul", geo: { lat: 37.5, lon: 127.0 } },
          tags: ["a", "b"],
          visits: [ { at: "2026-08-01" }, { at: "2026-08-05" } ] }
        """);

    [Fact]
    public void RootSummarizesFieldCountAndId()
    {
        var root = MongoTree.FromDocument(Sample, index: 0);

        Assert.Equal("(1)", root.Name);
        Assert.Contains("6 field(s)", root.Value);
        Assert.Contains("_id: 7", root.Value);
        Assert.Equal(6, root.Children.Count);
    }

    [Fact]
    public void ScalarsKeepGridCellFormatting()
    {
        var root = MongoTree.FromDocument(Sample, 0);

        var name = root.Children[1];
        Assert.Equal(("name", "kim", "String"), (name.Name, name.Value, name.Type));
        Assert.False(name.HasChildren);
        Assert.Equal("Int32", root.Children[2].Type);   // age
    }

    [Fact]
    public void NestedDocumentsExpandRecursively()
    {
        var address = MongoTree.FromDocument(Sample, 0).Children[3];

        Assert.Equal("{ 2 field(s) }", address.Value);
        Assert.Equal("Document", address.Type);
        var geo = address.Children[1];
        Assert.Equal("geo", geo.Name);
        Assert.Equal("lat", geo.Children[0].Name);
        Assert.Equal("37.5", geo.Children[0].Value);
    }

    [Fact]
    public void ArraysIndexTheirElements()
    {
        var root = MongoTree.FromDocument(Sample, 0);

        var tags = root.Children[4];
        Assert.Equal("[ 2 element(s) ]", tags.Value);
        Assert.Equal(("[0]", "a"), (tags.Children[0].Name, tags.Children[0].Value));

        // 배열 안의 문서도 계속 펴진다
        var visits = root.Children[5];
        Assert.Equal("Document", visits.Children[0].Type);
        Assert.Equal("at", visits.Children[0].Children[0].Name);
    }

    [Fact]
    public void MissingIdIsJustOmittedFromSummary()
    {
        var root = MongoTree.FromDocument(BsonDocument.Parse("""{ a: 1 }"""), 2);

        Assert.Equal("(3)", root.Name);
        Assert.Equal("{ 1 field(s) }", root.Value);
    }

    [Fact]
    public void NullValueReadsAsNull()
    {
        var root = MongoTree.FromDocument(BsonDocument.Parse("""{ a: null }"""), 0);

        Assert.Equal("null", root.Children[0].Value);
    }
}
