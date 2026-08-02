using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>자동완성 항목 하나 (테이블/컬럼/키워드).</summary>
public sealed class SqlCompletionItem(string text, string description, double priority) : ICompletionData
{
    public Avalonia.Media.IImage? Image => null;
    public string Text { get; } = text;
    public object Content => Text;
    public object Description { get; } = description;
    public double Priority { get; } = priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);
}

/// <summary>완성 후보 계산 (Golden 의 popup table/field lists).</summary>
public static class SqlCompletion
{
    public static readonly string[] Keywords =
    [
        "select", "from", "where", "insert", "into", "values", "update", "set", "delete",
        "join", "left", "right", "inner", "outer", "on", "group", "by", "order", "having",
        "limit", "offset", "union", "all", "distinct", "and", "or", "not", "null", "as",
        "case", "when", "then", "else", "end", "like", "ilike", "between", "exists", "in",
        "is", "count", "sum", "avg", "min", "max", "coalesce", "cast", "begin", "commit",
        "rollback", "create", "table", "index", "view", "alter", "drop", "returning",
    ];

    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "on", "join", "left", "right", "inner", "outer", "cross", "full",
        "group", "order", "having", "limit", "union", "set", "as", "using",
    };

    /// <summary>FROM/JOIN 절의 "테이블 별칭" 쌍을 찾는다. (alias → (schema?, table))</summary>
    public static Dictionary<string, (string? Schema, string Table)> ExtractAliases(string sql)
    {
        var map = new Dictionary<string, (string?, string)>(StringComparer.OrdinalIgnoreCase);
        var pattern = new Regex(
            @"\b(?:from|join)\s+(?:([A-Za-z_]\w*)\s*\.\s*)?([A-Za-z_]\w*)(?:\s+(?:as\s+)?([A-Za-z_]\w*))?",
            RegexOptions.IgnoreCase);
        foreach (Match m in pattern.Matches(sql))
        {
            var schema = m.Groups[1].Success ? m.Groups[1].Value : null;
            var table = m.Groups[2].Value;
            var alias = m.Groups[3].Success ? m.Groups[3].Value : null;
            if (alias is not null && !NotAliases.Contains(alias))
                map[alias] = (schema, table);
            map.TryAdd(table, (schema, table));
        }
        return map;
    }

    /// <summary>한정자 없는 위치: 키워드 + 스키마 + 테이블.</summary>
    public static List<SqlCompletionItem> General(IReadOnlyList<TableInfo> tables)
    {
        var items = new List<SqlCompletionItem>();
        items.AddRange(tables
            .Select(t => new SqlCompletionItem(t.Name, $"{t.Schema} · {(t.IsView ? "view" : "table")}", 3)));
        items.AddRange(tables.Select(t => t.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => new SqlCompletionItem(s, "schema", 2)));
        items.AddRange(Keywords.Select(k => new SqlCompletionItem(k, "keyword", 1)));
        return items;
    }

    /// <summary>한정자(q)가 스키마면 그 스키마의 테이블 목록, 아니면 null.</summary>
    public static List<SqlCompletionItem>? SchemaTables(IReadOnlyList<TableInfo> tables, string qualifier)
    {
        var inSchema = tables.Where(t => t.Schema.Equals(qualifier, StringComparison.OrdinalIgnoreCase)).ToList();
        return inSchema.Count == 0
            ? null
            : inSchema.Select(t => new SqlCompletionItem(t.Name, t.IsView ? "view" : "table", 3)).ToList();
    }

    /// <summary>한정자를 테이블(직접 이름 또는 별칭)로 해석한다.</summary>
    public static TableInfo? ResolveTable(IReadOnlyList<TableInfo> tables, string qualifier, string sql)
    {
        var direct = tables.FirstOrDefault(t => t.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;
        if (ExtractAliases(sql).TryGetValue(qualifier, out var target))
            return tables.FirstOrDefault(t =>
                t.Name.Equals(target.Table, StringComparison.OrdinalIgnoreCase) &&
                (target.Schema is null || t.Schema.Equals(target.Schema, StringComparison.OrdinalIgnoreCase)));
        return null;
    }
}
