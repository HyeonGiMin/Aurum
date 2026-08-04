using System;
using System.Collections.Generic;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace PrismOne.Db.Core.Mongo;

public enum MongoOperation
{
    Find,
    Aggregate,
    CountDocuments,
    Distinct,
    /// <summary><c>show collections</c> — 컬렉션 목록.</summary>
    ListCollections,
}

/// <summary>
/// 파싱이 끝난 Mongo 명령 하나. 필터·파이프라인은 <b>문자열이 아니라 BSON 문서</b>로 들고
/// 드라이버에 그대로 넘긴다 — SQL 처럼 문자열을 이어 붙이지 않으므로 주입 여지가 없다.
/// </summary>
public sealed record MongoCommand(
    MongoOperation Operation,
    string Collection,
    BsonDocument? Filter = null,
    BsonDocument? Projection = null,
    BsonArray? Pipeline = null,
    BsonDocument? Sort = null,
    int? Limit = null,
    int? Skip = null,
    string? DistinctField = null);

/// <summary>Mongo 셸 구문을 이해하지 못했을 때. 메시지는 사용자에게 그대로 보인다.</summary>
public sealed class MongoQueryException(string message) : Exception(message);

/// <summary>
/// Mongo 셸 문법(<c>db.people.find({...}).limit(10)</c>)을 <see cref="MongoCommand"/> 로 옮긴다.
/// 셸 전체를 구현하지 않는다 — DataGrip·Studio3T 에서 실제로 많이 쓰는
/// find / aggregate / countDocuments / distinct 와 뒤에 붙는 체인만 받는다.
/// SQL 이 아니라서 <c>StatementSplitter</c> 를 쓸 수 없어 별도 경로다.
/// </summary>
public static class MongoQueryParser
{
    public static MongoCommand Parse(string text)
    {
        var source = StripComments(text).Trim().TrimEnd(';').Trim();
        if (source.Length == 0) throw new MongoQueryException("실행할 명령이 없습니다.");

        if (source.Equals("show collections", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("show tables", StringComparison.OrdinalIgnoreCase))
            return new MongoCommand(MongoOperation.ListCollections, "");

        if (!source.StartsWith("db.", StringComparison.Ordinal))
            throw new MongoQueryException("db.<컬렉션>.find({...}) 형태로 써 주세요.");

        var rest = source[3..];
        var dot = IndexOfTopLevel(rest, '.');
        if (dot <= 0) throw new MongoQueryException("컬렉션 이름 뒤에 연산이 없습니다 (예: db.people.find({})).");

        var collection = rest[..dot].Trim();
        if (collection.Length == 0) throw new MongoQueryException("컬렉션 이름이 비어 있습니다.");

        var chain = SplitChain(rest[(dot + 1)..]);
        if (chain.Count == 0) throw new MongoQueryException("연산이 없습니다 (예: find, aggregate).");

        var head = chain[0];
        var command = head.Name switch
        {
            "find" => ParseFind(collection, head.Arguments),
            "findOne" => ParseFind(collection, head.Arguments) with { Limit = 1 },
            "aggregate" => ParseAggregate(collection, head.Arguments),
            "countDocuments" or "count" => new MongoCommand(
                MongoOperation.CountDocuments, collection, Filter: Doc(head.Arguments, 0, "필터")),
            "distinct" => ParseDistinct(collection, head.Arguments),
            _ => throw new MongoQueryException(
                $"'{head.Name}' 은(는) 아직 지원하지 않습니다 — find / aggregate / countDocuments / distinct 를 쓸 수 있습니다."),
        };

        // .limit(10).skip(5).sort({...}) 같은 뒤따르는 체인
        for (var i = 1; i < chain.Count; i++)
        {
            var call = chain[i];
            command = call.Name switch
            {
                "limit" => command with { Limit = Int(call.Arguments, "limit") },
                "skip" => command with { Skip = Int(call.Arguments, "skip") },
                "sort" => command with { Sort = Doc(call.Arguments, 0, "sort") },
                "projection" => command with { Projection = Doc(call.Arguments, 0, "projection") },
                "toArray" or "pretty" => command,   // 셸 습관 — 결과에 영향 없음
                _ => throw new MongoQueryException($"'{call.Name}' 체인은 지원하지 않습니다."),
            };
        }
        return command;
    }

    private static MongoCommand ParseFind(string collection, IReadOnlyList<string> args) =>
        new(MongoOperation.Find, collection,
            Filter: Doc(args, 0, "필터"),
            Projection: Doc(args, 1, "projection"));

    private static MongoCommand ParseAggregate(string collection, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].Trim().Length == 0)
            throw new MongoQueryException("aggregate 에는 파이프라인 배열이 필요합니다 (예: aggregate([{ $match: {} }])).");

        BsonArray pipeline;
        try
        {
            pipeline = BsonSerializer.Deserialize<BsonArray>(args[0]);
        }
        catch (Exception ex)
        {
            throw new MongoQueryException($"파이프라인 JSON 을 읽지 못했습니다: {ex.Message}");
        }
        return new MongoCommand(MongoOperation.Aggregate, collection, Pipeline: pipeline);
    }

    private static MongoCommand ParseDistinct(string collection, IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new MongoQueryException("distinct 에는 필드 이름이 필요합니다.");
        var field = args[0].Trim().Trim('"', '\'');
        if (field.Length == 0) throw new MongoQueryException("distinct 필드 이름이 비어 있습니다.");
        return new MongoCommand(MongoOperation.Distinct, collection,
            Filter: Doc(args, 1, "필터"), DistinctField: field);
    }

    private static BsonDocument? Doc(IReadOnlyList<string> args, int index, string what)
    {
        if (index >= args.Count) return null;
        var text = args[index].Trim();
        if (text.Length == 0) return null;
        try
        {
            return BsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new MongoQueryException($"{what} JSON 을 읽지 못했습니다: {ex.Message}");
        }
    }

    private static int Int(IReadOnlyList<string> args, string what)
    {
        if (args.Count == 0 || !int.TryParse(args[0].Trim(), out var value))
            throw new MongoQueryException($"{what}() 에는 숫자가 필요합니다.");
        if (value < 0) throw new MongoQueryException($"{what}() 는 음수일 수 없습니다.");
        return value;
    }

    private sealed record Call(string Name, IReadOnlyList<string> Arguments);

    /// <summary>
    /// <c>find({...}).limit(10)</c> 을 호출 목록으로 나눈다.
    /// 문자열·중첩 괄호 안의 점과 쉼표는 구분자로 보지 않는다.
    /// </summary>
    private static List<Call> SplitChain(string text)
    {
        var calls = new List<Call>();
        var i = 0;
        while (i < text.Length)
        {
            var open = IndexOfTopLevel(text[i..], '(');
            if (open < 0) throw new MongoQueryException("괄호가 없습니다 — find({}) 처럼 써 주세요.");
            var name = text[i..(i + open)].Trim();
            if (name.Length == 0) throw new MongoQueryException("연산 이름이 비어 있습니다.");

            var close = MatchingParen(text, i + open);
            var inner = text[(i + open + 1)..close];
            calls.Add(new Call(name, SplitArguments(inner)));

            i = close + 1;
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '.')) i++;
        }
        return calls;
    }

    /// <summary>여는 괄호와 짝이 되는 닫는 괄호 위치. 문자열 안은 세지 않는다.</summary>
    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (IsQuote(c)) { i = SkipString(text, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        throw new MongoQueryException("괄호가 닫히지 않았습니다.");
    }

    /// <summary>최상위 인자 분리 — 중첩·문자열 안의 쉼표는 건너뛴다.</summary>
    private static List<string> SplitArguments(string inner)
    {
        var args = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (IsQuote(c)) { i = SkipString(inner, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(inner[start..i]);
                start = i + 1;
            }
        }
        var tail = inner[start..];
        if (args.Count > 0 || tail.Trim().Length > 0) args.Add(tail);
        return args;
    }

    /// <summary>중첩·문자열 밖에 있는 첫 <paramref name="target"/> 위치. 없으면 -1.</summary>
    private static int IndexOfTopLevel(string text, char target)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsQuote(c)) { i = SkipString(text, i); continue; }
            // 목표 문자를 깊이 처리보다 먼저 본다 — '(' 는 목표이면서 여는 괄호라
            // 순서를 뒤집으면 자기 자신을 절대 찾지 못한다.
            if (c == target && depth == 0) return i;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
        }
        return -1;
    }

    private static bool IsQuote(char c) => c is '"' or '\'';

    /// <summary>여는 따옴표 위치를 받아 닫는 따옴표 위치를 준다 (백슬래시 이스케이프 존중).</summary>
    private static int SkipString(string text, int open)
    {
        var quote = text[open];
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == quote) return i;
        }
        throw new MongoQueryException("따옴표가 닫히지 않았습니다.");
    }

    /// <summary>// 줄 주석과 /* 블록 */ 주석 제거. 문자열 안의 // 는 건드리지 않는다.</summary>
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsQuote(c))
            {
                var end = SkipString(text, i);
                sb.Append(text, i, end - i + 1);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new MongoQueryException("블록 주석이 닫히지 않았습니다.");
                    i = end + 1;
                    sb.Append(' ');
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
