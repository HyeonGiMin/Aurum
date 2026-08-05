using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace PrismOne.Db.Core.Mongo;

/// <summary>
/// Import JSON — 파일 텍스트를 문서 목록으로 파싱한다. 두 형식을 받는다:
/// <b>JSON 배열</b>(<c>[{...}, {...}]</c>, `mongoexport --jsonArray` 산출물과 같다)과
/// <b>JSON Lines</b>(줄마다 문서 하나, `mongoexport` 기본 산출물).
/// 어느 쪽인지는 앞 글자(<c>[</c>)로 판정한다.
/// </summary>
public static class MongoJsonImport
{
    public static IReadOnlyList<BsonDocument> Parse(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0) return [];

        if (trimmed[0] == '[')
        {
            BsonArray array;
            try
            {
                array = BsonSerializer.Deserialize<BsonArray>(trimmed);
            }
            catch (System.Exception ex)
            {
                throw new MongoQueryException($"JSON 배열을 읽지 못했습니다: {ex.Message}");
            }
            return array.Select(v => v.AsBsonDocument).ToList();
        }

        // JSON Lines — 줄마다 문서 하나. 빈 줄은 건너뛴다.
        var documents = new List<BsonDocument>();
        var lineNumber = 0;
        foreach (var line in trimmed.Split('\n'))
        {
            lineNumber++;
            var t = line.Trim().TrimEnd('\r');
            if (t.Length == 0) continue;
            try
            {
                documents.Add(BsonDocument.Parse(t));
            }
            catch (System.Exception ex)
            {
                throw new MongoQueryException($"{lineNumber}번째 줄을 읽지 못했습니다: {ex.Message}");
            }
        }
        return documents;
    }
}
