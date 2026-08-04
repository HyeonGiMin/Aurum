using System;
using MongoDB.Bson;
using PrismOne.Db.Core.Mongo;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// 문서 → 표 평탄화. Mongo 는 문서마다 필드가 달라서 이 규칙이 그리드 동작을 좌우한다.
/// </summary>
public class MongoDocumentsTests
{
    [Fact]
    public void Flatten_KeepsFieldOrderOfFirstAppearance()
    {
        var table = MongoDocuments.Flatten([
            BsonDocument.Parse("{ _id: 1, name: 'a' }"),
            BsonDocument.Parse("{ _id: 2, name: 'b' }"),
        ]);

        Assert.Equal(["_id", "name"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void Flatten_UnionsFieldsAcrossDocuments_AndLeavesMissingAsNull()
    {
        var table = MongoDocuments.Flatten([
            BsonDocument.Parse("{ a: 1 }"),
            BsonDocument.Parse("{ b: 2 }"),
        ]);

        Assert.Equal(["a", "b"], table.Columns);
        Assert.Equal(1, table.Rows[0][0]);
        Assert.Null(table.Rows[0][1]);      // 첫 문서에 b 가 없다
        Assert.Null(table.Rows[1][0]);
        Assert.Equal(2, table.Rows[1][1]);
    }

    [Fact]
    public void Flatten_ExpandsNestedDocuments_AsDottedPaths()
    {
        var table = MongoDocuments.Flatten([
            BsonDocument.Parse("{ _id: 1, address: { city: 'Seoul', zip: '01234' } }"),
        ]);

        Assert.Equal(["_id", "address.city", "address.zip"], table.Columns);
        Assert.Equal("Seoul", table.Rows[0][1]);
    }

    [Fact]
    public void Flatten_StopsExpanding_BeyondMaxDepth()
    {
        var table = MongoDocuments.Flatten(
            [BsonDocument.Parse("{ a: { b: { c: 1 } } }")], maxDepth: 1);

        // depth 1 까지만 펴므로 a.b 가 JSON 한 칸으로 남는다
        Assert.Equal(["a.b"], table.Columns);
        Assert.Contains("c", Assert.IsType<string>(table.Rows[0][0]));
    }

    [Fact]
    public void Flatten_KeepsArraysAsJson_InsteadOfExpanding()
    {
        var table = MongoDocuments.Flatten([BsonDocument.Parse("{ tags: ['x', 'y'] }")]);

        Assert.Equal(["tags"], table.Columns);
        var cell = Assert.IsType<string>(table.Rows[0][0]);
        Assert.Contains("x", cell);
        Assert.Contains("y", cell);
    }

    [Fact]
    public void Flatten_TreatsEmptyNestedDocument_AsValue()
    {
        var table = MongoDocuments.Flatten([BsonDocument.Parse("{ meta: {} }")]);

        Assert.Equal(["meta"], table.Columns);
    }

    [Fact]
    public void Flatten_ReturnsEmptyTable_ForNoDocuments()
    {
        var table = MongoDocuments.Flatten([]);

        Assert.Empty(table.Columns);
        Assert.Empty(table.Rows);
    }

    // ---- 값 변환: 정렬이 문자열순으로 어긋나지 않도록 타입을 지킨다 ----

    [Fact]
    public void ToCell_KeepsNumbersAsNumbers() =>
        Assert.Equal(42, MongoDocuments.ToCell(new BsonInt32(42)));

    [Fact]
    public void ToCell_KeepsBooleans() =>
        Assert.Equal(true, MongoDocuments.ToCell(BsonBoolean.True));

    [Fact]
    public void ToCell_MapsNullAndUndefined_ToNull()
    {
        Assert.Null(MongoDocuments.ToCell(BsonNull.Value));
        Assert.Null(MongoDocuments.ToCell(BsonUndefined.Value));
    }

    [Fact]
    public void ToCell_RendersObjectId_AsHexString()
    {
        var id = ObjectId.GenerateNewId();

        Assert.Equal(id.ToString(), MongoDocuments.ToCell(new BsonObjectId(id)));
    }

    [Fact]
    public void ToCell_KeepsDateTime_AsUtcDateTime()
    {
        var when = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);

        var cell = Assert.IsType<DateTime>(MongoDocuments.ToCell(new BsonDateTime(when)));
        Assert.Equal(when, cell);
        Assert.Equal(DateTimeKind.Utc, cell.Kind);
    }
}
