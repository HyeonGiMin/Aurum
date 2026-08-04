using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;

namespace PrismOne.Db.Core.Mongo;

/// <summary>문서 목록을 표로 바꾼 결과. 컬럼은 문서들에 나온 필드의 합집합이다.</summary>
public sealed record MongoTable(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// BSON 문서를 그리드가 쓸 수 있는 표로 편다.
///
/// SQL 결과와 달리 <b>문서마다 필드가 다를 수 있어</b> 컬럼을 미리 알 수 없다 —
/// 그래서 받은 문서를 훑어 필드의 합집합을 만들고, 없는 칸은 null 로 둔다.
/// 중첩 문서는 <c>address.city</c> 같은 점 경로로 펴서 한 화면에서 읽히게 한다
/// (Studio3T 의 Table View 와 같은 방식). 배열은 펴지 않고 JSON 으로 보여준다 —
/// 길이가 문서마다 달라 컬럼으로 만들면 표가 폭발한다.
/// </summary>
public static class MongoDocuments
{
    /// <summary>이 깊이를 넘는 중첩은 펴지 않고 JSON 한 칸으로 둔다.</summary>
    public const int DefaultMaxDepth = 3;

    /// <summary>컬럼 수 상한 — 필드가 제각각인 컬렉션에서 표가 무한히 넓어지는 걸 막는다.</summary>
    public const int MaxColumns = 200;

    public static MongoTable Flatten(IEnumerable<BsonDocument> documents, int maxDepth = DefaultMaxDepth)
    {
        // 처음 나온 순서를 지켜야 _id 가 맨 앞에 온다 (Dictionary 는 순서를 보장하지 않는다)
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var flattened = new List<Dictionary<string, object?>>();

        foreach (var document in documents)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            Walk(document, prefix: "", depth: 0, maxDepth, row);
            flattened.Add(row);

            foreach (var key in row.Keys)
            {
                if (!seen.Add(key)) continue;
                if (columns.Count < MaxColumns) columns.Add(key);
            }
        }

        var rows = flattened
            .Select(row => columns.Select(c => row.GetValueOrDefault(c)).ToArray())
            .ToList();

        return new MongoTable(columns, rows);
    }

    private static void Walk(
        BsonDocument document, string prefix, int depth, int maxDepth, Dictionary<string, object?> row)
    {
        foreach (var element in document)
        {
            var path = prefix.Length == 0 ? element.Name : $"{prefix}.{element.Name}";

            // 중첩 문서는 깊이가 남아 있을 때만 편다. 빈 문서는 펼 게 없으니 값으로 둔다.
            if (element.Value is BsonDocument nested && depth < maxDepth && nested.ElementCount > 0)
            {
                Walk(nested, path, depth + 1, maxDepth, row);
                continue;
            }
            row[path] = ToCell(element.Value);
        }
    }

    /// <summary>
    /// 그리드 한 칸에 넣을 값. 숫자·불리언·날짜는 원래 타입을 지켜 정렬이 문자열순으로
    /// 어긋나지 않게 하고, 나머지는 사람이 읽을 수 있는 문자열로 만든다.
    /// </summary>
    public static object? ToCell(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => null,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => value.AsDecimal,
        BsonType.String => value.AsString,
        BsonType.DateTime => value.ToUniversalTime(),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        // 배열·중첩 문서·기타(바이너리 등)는 JSON 으로. 셀 상세 창에서 전문을 본다.
        _ => value.ToJson(),
    };
}
