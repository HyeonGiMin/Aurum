using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>완성 항목 종류 — 팝업의 배지 색·글자를 정한다.</summary>
public enum SqlCompletionKind { Table, View, Schema, Column, Keyword }

/// <summary>
/// 자동완성 항목 하나. 팝업에는 [종류 배지] 이름 … 설명 형태로 그린다
/// (Golden 의 popup table/field lists 를 읽기 쉽게 확장).
/// </summary>
public sealed class SqlCompletionItem(string text, string description, double priority, SqlCompletionKind kind)
    : ICompletionData
{
    private static readonly (string Badge, string Fill, string Fore)[] Styles =
    [
        ("T",  "#E3F0FB", "#1B5E9C"),   // Table
        ("V",  "#EDE7F6", "#5E35B1"),   // View
        ("S",  "#E8F5E9", "#2E7D32"),   // Schema
        ("C",  "#FFF3E0", "#B26A00"),   // Column
        ("K",  "#F2F2F2", "#6B6B6B"),   // Keyword
    ];

    public Avalonia.Media.IImage? Image => null;
    public string Text { get; } = text;
    public object Description { get; } = description;
    public double Priority { get; } = priority;
    public SqlCompletionKind Kind { get; } = kind;

    /// <summary>팝업 행 렌더링: 종류 배지 + 이름(굵게) + 오른쪽 회색 설명.</summary>
    public object Content
    {
        get
        {
            var (badgeText, fill, fore) = Styles[(int)Kind];
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.Parse(fill)),
                CornerRadius = new CornerRadius(3),
                Width = 18,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 10.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse(fore)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var name = new TextBlock
            {
                Text = Text,
                FontSize = 12.5,
                FontWeight = Kind == SqlCompletionKind.Keyword ? FontWeight.Normal : FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var detail = new TextBlock
            {
                Text = Description as string ?? "",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                MinWidth = 260,
            };
            badge.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(badge, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(detail, 2);
            grid.Children.Add(badge);
            grid.Children.Add(name);
            grid.Children.Add(detail);
            return grid;
        }
    }

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

    /// <summary>커서(단어 시작) 직전의 키워드가 테이블 자리인지 — from/join/into/update 뒤.</summary>
    public static bool IsTablePosition(string text, int wordStart)
    {
        var i = wordStart;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        var end = i;
        while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) i--;
        var word = text[i..end];
        return word.Equals("from", StringComparison.OrdinalIgnoreCase)
            || word.Equals("join", StringComparison.OrdinalIgnoreCase)
            || word.Equals("into", StringComparison.OrdinalIgnoreCase)
            || word.Equals("update", StringComparison.OrdinalIgnoreCase)
            || word.Equals("table", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 컬럼이 올 자리인지 — where/and/or/on/having/by/set/select 뒤, 또는 콤마 뒤.
    /// Golden 처럼 WHERE 뒤에서 컬럼명이 바로 뜨게 하려는 판정이다.
    /// </summary>
    public static bool IsColumnPosition(string text, int wordStart)
    {
        var i = wordStart;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        if (i > 0 && text[i - 1] == ',') return true;   // select a, b

        var end = i;
        while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) i--;
        return ColumnKeywords.Contains(text[i..end]);
    }

    private static readonly HashSet<string> ColumnKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "and", "or", "on", "having", "by", "set", "select", "not", "using",
    };

    /// <summary>
    /// FROM/JOIN 에 등장한 테이블들 — WHERE 뒤 컬럼 완성 대상.
    /// 별칭과 테이블명이 같은 대상을 가리켜도 한 번만 담는다.
    /// </summary>
    public static List<TableInfo> ReferencedTables(IReadOnlyList<TableInfo> tables, string sql)
    {
        var result = new List<TableInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (schema, table) in ExtractAliases(sql).Values)
        {
            var match = tables.FirstOrDefault(t =>
                t.Name.Equals(table, StringComparison.OrdinalIgnoreCase) &&
                (schema is null || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));
            if (match is not null && seen.Add(match.QualifiedName))
                result.Add(match);
        }
        return result;
    }

    /// <summary>테이블 자리: 테이블 + 스키마만 (키워드 잡음 없이).
    /// preferredSchema 의 테이블을 맨 위로 올린다 (브라우저에서 고른 스키마).</summary>
    public static List<SqlCompletionItem> TablesOnly(IReadOnlyList<TableInfo> tables, string? preferredSchema = null)
    {
        var items = Order(tables, preferredSchema).Select(t => new SqlCompletionItem(
            t.Name, t.Schema, Weight(t, preferredSchema),
            t.IsView ? SqlCompletionKind.View : SqlCompletionKind.Table)).ToList();
        items.AddRange(tables.Select(t => t.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => new SqlCompletionItem(s, "schema", 2, SqlCompletionKind.Schema)));
        return items;
    }

    /// <summary>현재 스키마 → 그 외 스키마 순, 각각 이름 오름차순.</summary>
    private static IEnumerable<TableInfo> Order(IReadOnlyList<TableInfo> tables, string? preferred) =>
        tables
            .OrderByDescending(t => preferred is not null &&
                                    t.Schema.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    private static double Weight(TableInfo t, string? preferred) =>
        preferred is not null && t.Schema.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 4 : 3;

    /// <summary>한정자 없는 위치: 키워드 + 스키마 + 테이블.</summary>
    public static List<SqlCompletionItem> General(IReadOnlyList<TableInfo> tables, string? preferredSchema = null)
    {
        var items = new List<SqlCompletionItem>();
        items.AddRange(Order(tables, preferredSchema).Select(t => new SqlCompletionItem(
            t.Name, t.Schema, Weight(t, preferredSchema),
            t.IsView ? SqlCompletionKind.View : SqlCompletionKind.Table)));
        items.AddRange(tables.Select(t => t.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => new SqlCompletionItem(s, "schema", 2, SqlCompletionKind.Schema)));
        items.AddRange(Keywords.Select(k => new SqlCompletionItem(k, "", 1, SqlCompletionKind.Keyword)));
        return items;
    }

    /// <summary>한정자(q)가 스키마면 그 스키마의 테이블 목록, 아니면 null.</summary>
    public static List<SqlCompletionItem>? SchemaTables(IReadOnlyList<TableInfo> tables, string qualifier)
    {
        var inSchema = tables.Where(t => t.Schema.Equals(qualifier, StringComparison.OrdinalIgnoreCase)).ToList();
        return inSchema.Count == 0
            ? null
            : inSchema.Select(t => new SqlCompletionItem(
                t.Name, t.IsView ? "view" : "table", 3,
                t.IsView ? SqlCompletionKind.View : SqlCompletionKind.Table)).ToList();
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
