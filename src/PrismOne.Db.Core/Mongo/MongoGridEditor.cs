using MongoDB.Bson;

namespace PrismOne.Db.Core.Mongo;

/// <summary>
/// Mongo 그리드 편집(Run and Edit)의 문서 만들기. SQL 쪽 <see cref="GridEditor"/> 에 대응하지만
/// 문장을 만들지 않고 <b>BSON 문서</b>를 만든다 — 행 식별도 의사 컬럼이 아니라 <c>_id</c> 다.
///
/// **타입을 지키는 게 핵심이다.** 그리드 셀은 전부 문자열이라 그대로 쓰면 숫자 필드가
/// 문자열로 바뀌어 버린다(Mongo 는 스키마가 없어 조용히 성공한다). 그래서 원본 문서에서
/// 같은 경로의 값을 찾아 <b>그 타입으로</b> 되돌린다.
///
/// 컬럼 이름은 <see cref="MongoDocuments.Flatten"/> 이 만든 점 경로(`address.city`)이고,
/// Mongo 의 <c>$set</c> 이 그대로 알아듣는다.
/// </summary>
public static class MongoGridEditor
{
    public const string IdField = "_id";

    /// <summary>바뀐 셀만 <c>{ $set: { ... } }</c> 로 만든다.</summary>
    /// <exception cref="ArgumentException">
    /// <c>_id</c> 를 고치려 하거나, 배열·중첩 문서처럼 그리드에서 다룰 수 없는 값일 때.
    /// </exception>
    public static BsonDocument BuildUpdate(
        BsonDocument original, IReadOnlyList<(string Field, string? Value)> changes)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (changes.Count == 0)
            throw new ArgumentException("바뀐 값이 없습니다.", nameof(changes));

        var set = new BsonDocument();
        foreach (var (field, value) in changes)
        {
            if (string.IsNullOrWhiteSpace(field))
                continue;
            if (field == IdField)
                throw new ArgumentException(
                    "_id 는 그리드에서 바꿀 수 없습니다 — 새 문서를 넣고 예전 것을 지우세요.", nameof(changes));
            set[field] = Convert(field, value, Resolve(original, field));
        }

        if (set.ElementCount == 0)
            throw new ArgumentException("바뀐 값이 없습니다.", nameof(changes));
        return new BsonDocument("$set", set);
    }

    /// <summary>
    /// 새 문서. 원본이 없으니 타입을 문자열에서 <b>추론</b>한다 (참/거짓 · 정수 · 실수 · 그 외 문자열).
    /// 빈 칸은 넣지 않는다 — Mongo 는 스키마가 없어 null 을 굳이 채워 둘 이유가 없다.
    /// </summary>
    public static BsonDocument BuildInsert(IReadOnlyList<(string Field, string? Value)> cells)
    {
        var document = new BsonDocument();
        foreach (var (field, value) in cells)
        {
            if (string.IsNullOrWhiteSpace(field) || string.IsNullOrEmpty(value))
                continue;
            // _id 를 적었으면 존중한다 (안 적으면 Mongo 가 만든다)
            document[field] = Infer(value);
        }
        if (document.ElementCount == 0)
            throw new ArgumentException("값이 하나도 없습니다.", nameof(cells));
        return document;
    }

    /// <summary>점 경로로 원본 값을 찾는다. 없으면 null (새로 생기는 필드).</summary>
    public static BsonValue? Resolve(BsonDocument document, string path)
    {
        BsonValue current = document;
        foreach (var part in path.Split('.'))
        {
            if (current is not BsonDocument doc || !doc.TryGetValue(part, out var next))
                return null;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// 셀 문자열을 <paramref name="original"/> 의 타입으로 되돌린다.
    /// 원본이 없으면(새 필드) 추론한다.
    /// </summary>
    public static BsonValue Convert(string field, string? value, BsonValue? original)
    {
        if (string.IsNullOrEmpty(value))
            return BsonNull.Value;

        // 그리드에는 JSON 한 줄로 보이는 값들 — 문자열로 되돌리면 문서가 망가진다
        if (original is not null &&
            original.BsonType is BsonType.Array or BsonType.Document or BsonType.Binary)
            throw new ArgumentException(
                $"{field}: 배열·중첩 문서는 그리드에서 고칠 수 없습니다 — Edit Document (Ctrl+Shift+D) 를 쓰세요.");

        if (original is null || original.IsBsonNull)
            return Infer(value);

        return original.BsonType switch
        {
            BsonType.Boolean => bool.TryParse(value, out var b) ? b : throw Bad(field, value, "참/거짓"),
            BsonType.Int32 => int.TryParse(value, out var i) ? i : throw Bad(field, value, "정수"),
            BsonType.Int64 => long.TryParse(value, out var l) ? l : throw Bad(field, value, "정수"),
            BsonType.Double => double.TryParse(value, out var d) ? d : throw Bad(field, value, "실수"),
            BsonType.Decimal128 => decimal.TryParse(value, out var m) ? m : throw Bad(field, value, "실수"),
            BsonType.DateTime => DateTime.TryParse(value, out var t)
                ? t.ToUniversalTime()
                : throw Bad(field, value, "날짜"),
            BsonType.ObjectId => ObjectId.TryParse(value, out var oid) ? oid : throw Bad(field, value, "ObjectId"),
            _ => value,
        };
    }

    /// <summary>원본 타입을 모를 때의 추론. 날짜는 넘겨짚지 않는다 (문자열로 둔다).</summary>
    private static BsonValue Infer(string value)
    {
        if (bool.TryParse(value, out var b)) return b;
        if (long.TryParse(value, out var l)) return l;
        if (double.TryParse(value, out var d)) return d;
        return value;
    }

    private static ArgumentException Bad(string field, string value, string expected) =>
        new($"{field}: '{value}' 는 {expected} 가 아닙니다 (원래 값의 타입을 지킵니다).");
}
