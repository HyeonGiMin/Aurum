using MongoDB.Bson;
using PrismOne.Db.Core.Mongo;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// 그리드 셀(문자열)을 원래 BSON 타입으로 되돌리는지. Mongo 는 스키마가 없어서
/// 타입이 어긋나도 <b>조용히 저장된다</b> — 그래서 여기서 막는 게 유일한 방어선이다.
/// </summary>
public class MongoGridEditorTests
{
    private static BsonDocument Sample() => new()
    {
        ["_id"] = new ObjectId("64b7f0c2e13b4a5d6c8f0a11"),
        ["patient_id"] = "P000182",
        ["age"] = 42,                       // Int32
        ["visits"] = 900000000000L,         // Int64
        ["score"] = 8.5,                    // Double
        ["active"] = true,
        ["seen_at"] = new DateTime(2026, 8, 4, 9, 10, 0, DateTimeKind.Utc),
        ["address"] = new BsonDocument { ["city"] = "Seoul" },
        ["tags"] = new BsonArray { "ct", "urgent" },
    };

    private static BsonDocument Set(BsonDocument update) => update["$set"].AsBsonDocument;

    [Fact]
    public void UpdateKeepsTheOriginalTypeOfEachField()
    {
        var set = Set(MongoGridEditor.BuildUpdate(Sample(),
        [
            ("age", "43"),
            ("visits", "900000000001"),
            ("score", "9.25"),
            ("active", "false"),
            ("patient_id", "P000999"),
        ]));

        Assert.Equal(BsonType.Int32, set["age"].BsonType);
        Assert.Equal(43, set["age"].AsInt32);
        Assert.Equal(BsonType.Int64, set["visits"].BsonType);
        Assert.Equal(BsonType.Double, set["score"].BsonType);
        Assert.False(set["active"].AsBoolean);
        Assert.Equal("P000999", set["patient_id"].AsString);
    }

    [Fact]
    public void NestedFieldKeepsItsDottedPathSoMongoSetsJustThatKey()
    {
        var set = Set(MongoGridEditor.BuildUpdate(Sample(), [("address.city", "Busan")]));

        Assert.Equal("Busan", set["address.city"].AsString);
        Assert.Equal(1, set.ElementCount);
    }

    [Fact]
    public void EmptyCellBecomesNullNotAnEmptyString()
    {
        var set = Set(MongoGridEditor.BuildUpdate(Sample(), [("patient_id", "")]));

        Assert.Equal(BsonType.Null, set["patient_id"].BsonType);
    }

    [Fact]
    public void DateIsStoredAsUtcDateTimeNotText()
    {
        var set = Set(MongoGridEditor.BuildUpdate(Sample(), [("seen_at", "2026-09-08 01:02:03Z")]));

        Assert.Equal(BsonType.DateTime, set["seen_at"].BsonType);
    }

    [Fact]
    public void ChangingIdIsRefused()
        => Assert.Throws<ArgumentException>(() =>
            MongoGridEditor.BuildUpdate(Sample(), [("_id", "64b7f0c2e13b4a5d6c8f0a99")]));

    [Fact]
    public void ArraysAndNestedDocumentsAreRefusedBecauseTheGridShowsThemAsJson()
    {
        Assert.Throws<ArgumentException>(() =>
            MongoGridEditor.BuildUpdate(Sample(), [("tags", "[\"ct\"]")]));
        // 펼치지 않은 중첩 문서를 통째로 고치려는 경우
        var flat = new BsonDocument { ["meta"] = new BsonDocument { ["a"] = 1 } };
        Assert.Throws<ArgumentException>(() =>
            MongoGridEditor.BuildUpdate(flat, [("meta", "{}")]));
    }

    [Theory]
    [InlineData("age", "마흔셋")]
    [InlineData("active", "예")]
    [InlineData("seen_at", "어제")]
    [InlineData("visits", "1.5")]
    public void ValuesThatDoNotFitTheOriginalTypeAreRefused(string field, string value)
        => Assert.Throws<ArgumentException>(() =>
            MongoGridEditor.BuildUpdate(Sample(), [(field, value)]));

    [Fact]
    public void BrandNewFieldFallsBackToInference()
    {
        var set = Set(MongoGridEditor.BuildUpdate(Sample(),
            [("note", "hello"), ("count", "7"), ("ok", "true")]));

        Assert.Equal(BsonType.String, set["note"].BsonType);
        Assert.Equal(BsonType.Int64, set["count"].BsonType);
        Assert.Equal(BsonType.Boolean, set["ok"].BsonType);
    }

    [Fact]
    public void UpdateWithNothingChangedIsRefused()
        => Assert.Throws<ArgumentException>(() => MongoGridEditor.BuildUpdate(Sample(), []));

    // ---------- insert ----------

    [Fact]
    public void InsertInfersTypesAndDropsBlankCells()
    {
        var document = MongoGridEditor.BuildInsert(
            [("patient_id", "P1"), ("age", "30"), ("score", "1.5"), ("active", "true"), ("note", "")]);

        Assert.Equal("P1", document["patient_id"].AsString);
        Assert.Equal(BsonType.Int64, document["age"].BsonType);
        Assert.Equal(BsonType.Double, document["score"].BsonType);
        Assert.True(document["active"].AsBoolean);
        Assert.False(document.Contains("note"));
    }

    [Fact]
    public void InsertWithNoValuesIsRefused()
        => Assert.Throws<ArgumentException>(() => MongoGridEditor.BuildInsert([("a", "")]));

    [Fact]
    public void ResolveWalksDottedPathsAndReturnsNullWhenMissing()
    {
        Assert.Equal("Seoul", MongoGridEditor.Resolve(Sample(), "address.city")!.AsString);
        Assert.Null(MongoGridEditor.Resolve(Sample(), "address.zip"));
        Assert.Null(MongoGridEditor.Resolve(Sample(), "patient_id.deeper"));
    }
}
