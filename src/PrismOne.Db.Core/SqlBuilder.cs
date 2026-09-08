using System.Text;
using System.Text.RegularExpressions;

namespace PrismOne.Db.Core;

/// <summary>WHERE 절 한 줄. 연산자는 화이트리스트에서만 고른다.</summary>
public sealed record QueryCondition(string Column, string Operator, string? Value);

/// <summary>ORDER BY 한 줄.</summary>
public sealed record QueryOrder(string Column, bool Descending);

/// <summary>조인 방식. UI 드롭다운과 같은 순서.</summary>
public enum JoinKind { Inner, Left, Right, Full }

/// <summary>조인 조건 한 쌍. 양쪽 모두 <c>별칭.컬럼</c> 형태로 적는다.</summary>
public sealed record JoinOn(string Left, string Right);

/// <summary>
/// 조인 한 줄. <paramref name="On"/> 이 비면 <c>cross join</c> 이 되므로
/// 빌더는 FK 에서 찾은 짝을 기본으로 채워 준다.
/// </summary>
public sealed record QueryJoin(
    string Table,
    string Alias,
    JoinKind Kind,
    IReadOnlyList<JoinOn> On);

/// <summary>
/// 집계 한 줄 — <c>count(*)</c>, <c>sum(s.amount) as total</c> 처럼.
/// 하나라도 있으면 고른 일반 컬럼이 자동으로 GROUP BY 가 된다.
/// </summary>
public sealed record QueryAggregate(string Function, string? Column, string? Alias);

/// <summary>LIMIT 문법이 갈리는 지점만 구분한다 (Oracle 은 fetch first).</summary>
public enum SqlDialect { Standard, Oracle }

/// <summary>비주얼 쿼리 빌더가 만드는 SELECT 의 재료 (Golden 의 SQLBuilder).</summary>
public sealed record QuerySpec(
    string Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<QueryCondition> Conditions,
    IReadOnlyList<QueryOrder> Orders,
    int? Limit = null,
    string? Alias = null,
    IReadOnlyList<QueryJoin>? Joins = null,
    IReadOnlyList<QueryAggregate>? Aggregates = null,
    bool Distinct = false,
    SqlDialect Dialect = SqlDialect.Standard)
{
    public IReadOnlyList<QueryJoin> JoinList => Joins ?? [];
    public IReadOnlyList<QueryAggregate> AggregateList => Aggregates ?? [];
}

/// <summary>
/// Golden 의 SQLBuilder — 테이블·컬럼·조건을 골라 SELECT 문을 만든다.
/// 만든 문장은 바로 실행되지 않고 에디터에 들어가므로 사용자가 확인한 뒤 실행한다.
/// 그래도 값은 리터럴로 인용하고 연산자는 화이트리스트로 제한한다.
/// </summary>
public static class SqlBuilder
{
    /// <summary>고를 수 있는 연산자 (UI 드롭다운과 같은 순서).</summary>
    public static readonly string[] Operators =
        ["=", "<>", ">", ">=", "<", "<=", "LIKE", "ILIKE", "IN", "IS NULL", "IS NOT NULL"];

    private static readonly Regex NumberLiteral = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex BindVariable = new(@"^:[A-Za-z_]\w*$", RegexOptions.Compiled);
    private static readonly Regex PlainIdentifier = new(@"^[a-z_][a-z0-9_$]*$", RegexOptions.Compiled);

    /// <summary>집계 함수 화이트리스트 (UI 드롭다운과 같은 순서).</summary>
    public static readonly string[] AggregateFunctions =
        ["count", "sum", "avg", "min", "max"];

    public static string Build(QuerySpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Table))
            throw new ArgumentException("테이블을 고르세요.", nameof(spec));

        var prefix = spec.Alias is { Length: > 0 } alias ? alias + "." : "";
        var joins = spec.JoinList.Where(j => !string.IsNullOrWhiteSpace(j.Table)).ToList();
        var aggregates = spec.AggregateList.Where(a => !string.IsNullOrWhiteSpace(a.Function)).ToList();

        var sql = new StringBuilder("select ");
        if (spec.Distinct)
            sql.Append("distinct ");

        // 고른 컬럼 + 집계. 둘 다 없으면 * (조인이 있으면 앞쪽 테이블만 * 하지 않고 전체를 준다)
        var selected = spec.Columns
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => RenderColumnRef(c, prefix))
            .ToList();
        var aggregateSql = aggregates.Select(a => RenderAggregate(a, prefix)).ToList();
        sql.Append(selected.Count == 0 && aggregateSql.Count == 0
            ? (joins.Count > 0 ? "*" : prefix + "*")
            : string.Join(", ", selected.Concat(aggregateSql)));

        sql.Append($"\n  from {spec.Table}");
        if (spec.Alias is { Length: > 0 } a)
            sql.Append(' ').Append(a);

        foreach (var join in joins)
        {
            sql.Append($"\n  {JoinKeyword(join.Kind)} {join.Table}");
            if (!string.IsNullOrWhiteSpace(join.Alias))
                sql.Append(' ').Append(join.Alias);
            var on = (join.On ?? []).Where(o =>
                !string.IsNullOrWhiteSpace(o.Left) && !string.IsNullOrWhiteSpace(o.Right)).ToList();
            for (var i = 0; i < on.Count; i++)
            {
                sql.Append(i == 0 ? "\n    on " : "\n   and ");
                sql.Append($"{RenderColumnRef(on[i].Left, prefix)} = {RenderColumnRef(on[i].Right, prefix)}");
            }
        }

        var conditions = spec.Conditions.Where(c => !string.IsNullOrWhiteSpace(c.Column)).ToList();
        for (var i = 0; i < conditions.Count; i++)
        {
            sql.Append(i == 0 ? "\n where " : "\n   and ");
            sql.Append(RenderCondition(conditions[i], prefix));
        }

        // 집계가 있으면 고른 일반 컬럼이 곧 GROUP BY 다 (안 그러면 DB 가 거부한다)
        if (aggregateSql.Count > 0 && selected.Count > 0)
        {
            sql.Append("\n group by ");
            sql.Append(string.Join(", ", selected));
        }

        var orders = spec.Orders.Where(o => !string.IsNullOrWhiteSpace(o.Column)).ToList();
        if (orders.Count > 0)
        {
            sql.Append("\n order by ");
            sql.Append(string.Join(", ", orders.Select(o =>
                RenderColumnRef(o.Column, prefix) + (o.Descending ? " desc" : ""))));
        }

        if (spec.Limit is > 0 and var limit)
            sql.Append(spec.Dialect == SqlDialect.Oracle
                ? $"\n fetch first {limit} rows only"
                : $"\n limit {limit}");

        sql.Append(';');
        return sql.ToString();
    }

    private static string JoinKeyword(JoinKind kind) => kind switch
    {
        JoinKind.Left => "left join",
        JoinKind.Right => "right join",
        JoinKind.Full => "full join",
        _ => "join",
    };

    private static string RenderAggregate(QueryAggregate aggregate, string prefix)
    {
        var fn = AggregateFunctions.FirstOrDefault(f =>
                     string.Equals(f, aggregate.Function, StringComparison.OrdinalIgnoreCase))
                 ?? throw new ArgumentException($"지원하지 않는 집계 함수입니다: {aggregate.Function}", nameof(aggregate));

        // count 는 컬럼을 비워 두면 count(*) — 나머지는 컬럼이 있어야 한다
        var inner = string.IsNullOrWhiteSpace(aggregate.Column)
            ? (fn == "count" ? "*" : throw new ArgumentException($"{fn} 은 컬럼이 필요합니다.", nameof(aggregate)))
            : RenderColumnRef(aggregate.Column!, prefix);

        var call = $"{fn}({inner})";
        return string.IsNullOrWhiteSpace(aggregate.Alias)
            ? call
            : $"{call} as {QuoteIdentifier(aggregate.Alias!)}";
    }

    /// <summary>
    /// 컬럼 참조. <c>별칭.컬럼</c> 이면 마디마다 따로 인용하고(조인 빌더가 이렇게 넘긴다),
    /// 마디가 하나면 기본 별칭 접두사를 붙인다.
    /// </summary>
    private static string RenderColumnRef(string column, string prefix)
    {
        var trimmed = column.Trim();
        var dot = trimmed.IndexOf('.');
        if (dot < 0)
            return prefix + QuoteIdentifier(trimmed);

        // 점 양옆의 공백까지 털어낸다 — 손으로 적은 ON("s . key")도 같은 식별자가 되게
        var qualifier = trimmed[..dot].TrimEnd();
        var name = trimmed[(dot + 1)..].TrimStart();
        if (qualifier.Length == 0 || name.Length == 0)
            return prefix + QuoteIdentifier(trimmed);
        return QuoteIdentifier(qualifier) + "." + QuoteIdentifier(name);
    }

    private static string RenderCondition(QueryCondition condition, string prefix)
    {
        var op = Operators.FirstOrDefault(o => string.Equals(o, condition.Operator, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"지원하지 않는 연산자입니다: {condition.Operator}", nameof(condition));
        var column = RenderColumnRef(condition.Column, prefix);

        return op switch
        {
            "IS NULL" or "IS NOT NULL" => $"{column} {op.ToLowerInvariant()}",
            "IN" => $"{column} in ({RenderList(condition.Value)})",
            _ => $"{column} {op.ToLowerInvariant()} {RenderValue(condition.Value)}",
        };
    }

    /// <summary>IN 목록 — 콤마로 끊어 각각 리터럴로 인용한다.</summary>
    private static string RenderList(string? value)
    {
        var items = (value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RenderValue)
            .ToList();
        return items.Count == 0 ? "null" : string.Join(", ", items);
    }

    /// <summary>숫자·바인드 변수는 그대로, 나머지는 작은따옴표로 인용.</summary>
    private static string RenderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "null";
        if (NumberLiteral.IsMatch(value) || BindVariable.IsMatch(value))
            return value;
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            return "null";
        return "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>소문자 단순 식별자는 그대로, 그 밖에는 큰따옴표로 인용.</summary>
    private static string QuoteIdentifier(string identifier) =>
        PlainIdentifier.IsMatch(identifier)
            ? identifier
            : '"' + identifier.Replace("\"", "\"\"") + '"';
}
