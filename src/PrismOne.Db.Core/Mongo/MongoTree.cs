using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;

namespace PrismOne.Db.Core.Mongo;

/// <summary>트리 뷰 한 줄 — 필드 이름 · 표시 값 · BSON 타입 · 자식.</summary>
public sealed record MongoTreeNode(
    string Name,
    string Value,
    string Type,
    IReadOnlyList<MongoTreeNode> Children)
{
    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// Studio3T 의 Tree View 대응 — 문서를 점 경로로 펴는 표(Table View)와 달리
/// **중첩 구조 그대로** 접었다 펴며 본다. 순수 변환이라 서버 없이 테스트한다.
/// </summary>
public static class MongoTree
{
    /// <summary>
    /// 문서 하나 → 트리 루트. 루트 라벨은 Studio3T 처럼 "(순번) { N fields } _id 요약".
    /// </summary>
    public static MongoTreeNode FromDocument(BsonDocument document, int index)
    {
        var id = document.TryGetValue("_id", out var v) ? $"_id: {Scalar(v)}" : "";
        return new MongoTreeNode(
            $"({index + 1})",
            $"{{ {document.ElementCount} field(s) }}  {id}".TrimEnd(),
            "Document",
            document.Elements.Select(FromElement).ToList());
    }

    private static MongoTreeNode FromElement(BsonElement element) =>
        FromValue(element.Name, element.Value);

    private static MongoTreeNode FromValue(string name, BsonValue value) => value.BsonType switch
    {
        BsonType.Document => new MongoTreeNode(
            name, $"{{ {value.AsBsonDocument.ElementCount} field(s) }}", "Document",
            value.AsBsonDocument.Elements.Select(FromElement).ToList()),
        BsonType.Array => new MongoTreeNode(
            name, $"[ {value.AsBsonArray.Count} element(s) ]", "Array",
            value.AsBsonArray.Select((item, i) => FromValue($"[{i}]", item)).ToList()),
        _ => new MongoTreeNode(name, Scalar(value), value.BsonType.ToString(), []),
    };

    /// <summary>스칼라 표시 값 — 그리드 셀과 같은 규칙(<see cref="MongoDocuments.ToCell"/>).</summary>
    private static string Scalar(BsonValue value) =>
        MongoDocuments.ToCell(value)?.ToString() ?? "null";
}
