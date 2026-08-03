using System.Text;
using System.Text.RegularExpressions;

namespace PrismOne.Db.Core;

/// <summary>WHERE 절 한 줄. 연산자는 화이트리스트에서만 고른다.</summary>
public sealed record QueryCondition(string Column, string Operator, string? Value);

/// <summary>ORDER BY 한 줄.</summary>
public sealed record QueryOrder(string Column, bool Descending);

/// <summary>비주얼 쿼리 빌더가 만드는 SELECT 의 재료 (Golden 의 SQLBuilder).</summary>
public sealed record QuerySpec(
    string Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<QueryCondition> Conditions,
    IReadOnlyList<QueryOrder> Orders,
    int? Limit = null,
    string? Alias = null);

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

    public static string Build(QuerySpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Table))
            throw new ArgumentException("테이블을 고르세요.", nameof(spec));

        var prefix = spec.Alias is { Length: > 0 } alias ? alias + "." : "";
        var sql = new StringBuilder("select ");
        sql.Append(spec.Columns.Count == 0
            ? prefix + "*"
            : string.Join(", ", spec.Columns.Select(c => prefix + QuoteIdentifier(c))));
        sql.Append($"\n  from {spec.Table}");
        if (spec.Alias is { Length: > 0 } a)
            sql.Append(' ').Append(a);

        var conditions = spec.Conditions.Where(c => !string.IsNullOrWhiteSpace(c.Column)).ToList();
        for (var i = 0; i < conditions.Count; i++)
        {
            sql.Append(i == 0 ? "\n where " : "\n   and ");
            sql.Append(RenderCondition(conditions[i], prefix));
        }

        var orders = spec.Orders.Where(o => !string.IsNullOrWhiteSpace(o.Column)).ToList();
        if (orders.Count > 0)
        {
            sql.Append("\n order by ");
            sql.Append(string.Join(", ", orders.Select(o =>
                prefix + QuoteIdentifier(o.Column) + (o.Descending ? " desc" : ""))));
        }

        if (spec.Limit is > 0 and var limit)
            sql.Append($"\n limit {limit}");

        sql.Append(';');
        return sql.ToString();
    }

    private static string RenderCondition(QueryCondition condition, string prefix)
    {
        var op = Operators.FirstOrDefault(o => string.Equals(o, condition.Operator, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"지원하지 않는 연산자입니다: {condition.Operator}", nameof(condition));
        var column = prefix + QuoteIdentifier(condition.Column);

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
