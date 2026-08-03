using System.Text;
using System.Text.RegularExpressions;

namespace PrismOne.Db.Core;

/// <summary>편집 모드로 다시 실행할 수 있는 쿼리 (Golden 의 Run and Edit).</summary>
/// <param name="Table">UPDATE/DELETE/INSERT 대상 — 원문에 적힌 그대로 (스키마 포함 가능).</param>
/// <param name="Sql">행 식별자(ctid)를 첫 컬럼으로 덧붙인 SELECT.</param>
public sealed record EditableQuery(string Table, string Sql);

/// <summary>변경 한 건. Golden EditMode 의 update / delete / insert 에 대응.</summary>
public abstract record GridChange
{
    public sealed record Update(string RowId, IReadOnlyList<(string Column, string? Value)> Cells) : GridChange;
    public sealed record Delete(string RowId) : GridChange;
    public sealed record Insert(IReadOnlyList<(string Column, string? Value)> Cells) : GridChange;
}

/// <summary>실행할 문장 하나와 파라미터 값들 (값은 unknown 타입으로 바인딩해 PG 가 캐스팅하게 한다).</summary>
public sealed record EditStatement(string Sql, IReadOnlyList<string?> Parameters);

/// <summary>
/// Golden 의 Run and Edit — 결과 그리드를 직접 고쳐 UPDATE/DELETE/INSERT 를 만든다.
///
/// 행 식별은 Golden(Oracle)이 ROWID 를 쓰던 것과 같은 방식으로 PG 의 <c>ctid</c> 를 쓴다.
/// ctid 는 행이 갱신되면 바뀌므로, 실행부는 영향 행 수가 1 이 아니면 되돌려야 한다.
/// </summary>
public static class GridEditor
{
    /// <summary>편집 모드 SELECT 가 덧붙이는 행 식별자 컬럼 이름 (그리드에는 숨긴다).</summary>
    public const string RowIdColumn = "__iap_ctid";

    private static readonly Regex SelectHead = new(
        @"^\s*select\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>편집 불가 조건 — 집계·중복제거·조인·집합연산이 섞이면 원본 행을 특정할 수 없다.</summary>
    private static readonly Regex NotEditable = new(
        @"\b(distinct|join|group\s+by|having|union|intersect|except|with)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>FROM 절의 테이블 하나 + 뒤따르는 나머지.</summary>
    private static readonly Regex FromClause = new(
        @"\bfrom\s+(?<table>""?[A-Za-z_][\w$]*""?(?:\.""?[A-Za-z_][\w$]*""?)?)\s*(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AliasHead = new(
        @"^(?:as\s+)?(?<alias>[A-Za-z_]\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>별칭으로 오해하면 안 되는 예약어들.</summary>
    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "order", "limit", "offset", "fetch", "for", "group", "having",
        "union", "intersect", "except", "join", "inner", "left", "right", "full", "cross", "on", "using",
    };

    /// <summary>단일 테이블 SELECT 면 ctid 를 붙인 SELECT 를 돌려준다. 편집할 수 없으면 null.</summary>
    public static EditableQuery? Prepare(string sql)
    {
        var statements = StatementSplitter.Split(sql);
        if (statements.Count != 1)
            return null;

        var text = statements[0].Text.TrimEnd(';', ' ', '\t', '\r', '\n');
        if (!SelectHead.IsMatch(text) || NotEditable.IsMatch(text))
            return null;

        var from = FromClause.Match(text);
        if (!from.Success)
            return null;

        // FROM 뒤가 콤마(다중 테이블)거나 괄호(서브쿼리)면 편집 대상 행을 특정할 수 없다
        var rest = from.Groups["rest"].Value.TrimStart();
        if (rest.StartsWith(',') || rest.StartsWith('('))
            return null;

        var table = from.Groups["table"].Value;
        var qualifier = table;
        if (AliasHead.Match(rest) is { Success: true } alias)
        {
            var name = alias.Groups["alias"].Value;
            if (!ClauseKeywords.Contains(name))
                qualifier = name;
        }

        var head = SelectHead.Match(text);
        var edited = string.Concat(
            text[..head.Length],
            $"{qualifier}.ctid::text as \"{RowIdColumn}\", ",
            text[head.Length..]);
        return new EditableQuery(table, edited);
    }

    /// <summary>
    /// 클립보드 텍스트(탭 구분 — 엑셀·우리 TSV 내보내기 형식)를 붙여넣기용 행으로 바꾼다.
    /// Golden 의 "EditMode: Paste inserted %d records." 에 대응.
    /// 반환 행은 <paramref name="columns"/> 와 같은 길이이며 <paramref name="offset"/> 부터 채운다
    /// (편집 모드에선 0번이 ctid 자리라 비워 둔다).
    /// 첫 줄이 컬럼명과 같으면 헤더로 보고 건너뛴다 — 우리 그리드 복사가 헤더를 포함하기 때문.
    /// </summary>
    public static List<string?[]> ParsePaste(string text, IReadOnlyList<string> columns, int offset = 0)
    {
        var rows = new List<string?[]>();
        if (string.IsNullOrEmpty(text))
            return rows;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        if (lines.Length > 0 && IsHeaderLine(lines[0], columns, offset))
            start = 1;

        for (var i = start; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
                continue;   // 끝에 붙는 빈 줄
            var values = lines[i].Split('\t');
            var cells = new string?[columns.Count];
            for (var c = 0; c < values.Length && offset + c < columns.Count; c++)
                cells[offset + c] = values[c];
            rows.Add(cells);
        }
        return rows;
    }

    private static bool IsHeaderLine(string line, IReadOnlyList<string> columns, int offset)
    {
        var values = line.Split('\t');
        if (values.Length == 0 || offset + values.Length > columns.Count + 1)
            return false;
        for (var i = 0; i < values.Length && offset + i < columns.Count; i++)
        {
            if (!string.Equals(values[i].Trim(), columns[offset + i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>변경 목록을 실행 순서(수정 → 삭제 → 삽입)대로 문장으로 만든다.</summary>
    public static List<EditStatement> Build(string table, IEnumerable<GridChange> changes)
    {
        var ordered = changes.ToList();
        var statements = new List<EditStatement>();
        foreach (var change in ordered.OfType<GridChange.Update>())
            statements.Add(BuildUpdate(table, change));
        foreach (var change in ordered.OfType<GridChange.Delete>())
            statements.Add(BuildDelete(table, change));
        foreach (var change in ordered.OfType<GridChange.Insert>())
            statements.Add(BuildInsert(table, change));
        return statements;
    }

    private static EditStatement BuildUpdate(string table, GridChange.Update change)
    {
        if (change.Cells.Count == 0)
            throw new ArgumentException("수정된 셀이 없습니다.", nameof(change));

        var sql = new StringBuilder($"UPDATE {table} SET ");
        var values = new List<string?>();
        for (var i = 0; i < change.Cells.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append($"{Quote(change.Cells[i].Column)} = ${values.Count + 1}");
            values.Add(change.Cells[i].Value);
        }
        sql.Append($" WHERE ctid = ${values.Count + 1}::tid");
        values.Add(change.RowId);
        return new EditStatement(sql.ToString(), values);
    }

    private static EditStatement BuildDelete(string table, GridChange.Delete change) =>
        new($"DELETE FROM {table} WHERE ctid = $1::tid", [change.RowId]);

    private static EditStatement BuildInsert(string table, GridChange.Insert change)
    {
        if (change.Cells.Count == 0)
            throw new ArgumentException("입력된 값이 없습니다.", nameof(change));

        var columns = string.Join(", ", change.Cells.Select(c => Quote(c.Column)));
        var placeholders = string.Join(", ", change.Cells.Select((_, i) => $"${i + 1}"));
        return new EditStatement(
            $"INSERT INTO {table} ({columns}) VALUES ({placeholders})",
            change.Cells.Select(c => c.Value).ToList());
    }

    /// <summary>식별자 인용 — 대소문자·예약어 컬럼도 안전하게.</summary>
    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
