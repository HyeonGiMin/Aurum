namespace PrismOne.Db.Core;

/// <summary>에디터에 밑줄 칠 구간 하나 (오프셋은 원문 기준).</summary>
public readonly record struct SqlIssue(int Start, int Length, string Message);

/// <summary>
/// 실행 전 SQL 검증 (DataGrip 의 unresolved reference 표시 대응, DATAGRIP_GAP §2).
///
/// 전체 파서를 두지 않는다 — FROM/JOIN/INTO/UPDATE 뒤 테이블과
/// <c>별칭.컬럼</c> 참조만 introspection 캐시(<see cref="SchemaSnapshot"/>)와 대조한다.
/// 원칙은 **확신할 때만 표시**: 해석이 안 되는 것(서브쿼리 별칭, 모르는 스키마,
/// 따옴표 식별자, 함수)은 조용히 넘어간다. 오탐 하나가 기능 전체의 신뢰를 깎는다.
/// </summary>
public static class SqlValidator
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "insert", "into", "values", "update", "set", "delete",
        "join", "left", "right", "inner", "outer", "cross", "full", "lateral", "on",
        "group", "by", "order", "having", "limit", "offset", "union", "all", "distinct",
        "and", "or", "not", "null", "as", "case", "when", "then", "else", "end",
        "like", "ilike", "between", "exists", "in", "is", "any", "some",
        "begin", "commit", "rollback", "create", "table", "index", "view", "alter",
        "drop", "returning", "with", "recursive", "using", "for", "asc", "desc",
    };

    /// <summary>별칭이 될 수 없는 단어 — 테이블 뒤에 오면 절이 시작된 것이다.</summary>
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "on", "join", "left", "right", "inner", "outer", "cross", "full",
        "group", "order", "having", "limit", "offset", "union", "intersect", "except",
        "set", "using", "returning", "for", "values", "select", "natural",
    };

    /// <summary>DB 내장 이름 — 카탈로그에 없어도 정상이라 검사하지 않는다.</summary>
    private static bool IsBuiltinTable(string name) =>
        name.Equals("dual", StringComparison.OrdinalIgnoreCase)       // Oracle
        || name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)     // PG 카탈로그 뷰
        || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    public static List<SqlIssue> Validate(string sql, SchemaSnapshot snapshot)
    {
        var issues = new List<SqlIssue>();
        if (string.IsNullOrWhiteSpace(sql) || snapshot.Tables.Count == 0)
            return issues;

        var text = Mask(sql);
        var locals = CollectLocalNames(text);          // CTE·서브쿼리 별칭
        var refs = CollectTableRefs(text);

        var schemas = new HashSet<string>(
            snapshot.Tables.Select(t => t.Schema), StringComparer.OrdinalIgnoreCase);

        // 별칭/테이블명 → 카탈로그 키 (해석된 것만 — 못 찾은 테이블의 컬럼은 검사 안 함)
        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in refs)
        {
            if (locals.Contains(r.Name) || IsBuiltinTable(r.Name))
                continue;
            // 모르는 스키마(pg_catalog·information_schema 등)는 판단하지 않는다
            if (r.Schema is not null && !schemas.Contains(r.Schema))
                continue;

            var match = snapshot.Tables.FirstOrDefault(t =>
                t.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase) &&
                (r.Schema is null || t.Schema.Equals(r.Schema, StringComparison.OrdinalIgnoreCase)));
            if (match is null)
            {
                issues.Add(new SqlIssue(r.NameStart, r.Name.Length, $"Unknown table: {r.Name}"));
                continue;
            }
            var key = $"{match.Schema}.{match.Name}";
            aliasMap.TryAdd(r.Name, key);
            if (r.Alias is not null)
                aliasMap[r.Alias] = key;
        }

        CollectColumnIssues(text, snapshot, aliasMap, locals, schemas, issues);
        issues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return issues;
    }

    // ---------- 별칭.컬럼 ----------

    private static void CollectColumnIssues(
        string text, SchemaSnapshot snapshot,
        Dictionary<string, string> aliasMap, HashSet<string> locals, HashSet<string> schemas,
        List<SqlIssue> issues)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsWordStart(text[i]) || (i > 0 && IsWordChar(text[i - 1]))) continue;

            var qStart = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            var qualifier = text[qStart..i];

            var j = SkipWs(text, i);
            if (j >= text.Length || text[j] != '.') continue;
            j = SkipWs(text, j + 1);
            if (j >= text.Length || !IsWordStart(text[j])) continue;   // t.* 등
            var cStart = j;
            while (j < text.Length && IsWordChar(text[j])) j++;
            var column = text[cStart..j];

            // schema.table / schema.func() / a.b.c 체인은 컬럼 참조가 아니다
            var after = SkipWs(text, j);
            if (after < text.Length && (text[after] == '(' || text[after] == '.')) continue;
            var before = LastNonWs(text, qStart - 1);
            if (before >= 0 && text[before] == '.') continue;

            if (locals.Contains(qualifier) || schemas.Contains(qualifier)) continue;
            if (!aliasMap.TryGetValue(qualifier, out var key)) continue;   // 해석 불가 — 침묵
            if (!snapshot.Columns.TryGetValue(key, out var columns)) continue;

            if (!columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new SqlIssue(cStart, column.Length,
                    $"Unknown column: {qualifier}.{column} ({key} 에 없음)"));
        }
    }

    // ---------- FROM/JOIN/INTO/UPDATE 테이블 참조 ----------

    private readonly record struct TableRef(string? Schema, string Name, int NameStart, string? Alias);

    private static List<TableRef> CollectTableRefs(string text)
    {
        var refs = new List<TableRef>();
        // 함수 괄호 안의 from(extract(year from x) 등)을 걸러내기 위한 괄호 분류 스택.
        // true = 함수 호출 괄호 (식별자 뒤), false = 그룹/서브쿼리 괄호
        var parens = new Stack<bool>();

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '(')
            {
                var before = LastNonWs(text, i - 1);
                var isCall = before >= 0 && IsWordChar(text[before]) &&
                             !Keywords.Contains(WordEndingAt(text, before));
                parens.Push(isCall);
                continue;
            }
            if (ch == ')')
            {
                if (parens.Count > 0) parens.Pop();
                continue;
            }
            if (!IsWordStart(ch) || (i > 0 && IsWordChar(text[i - 1]))) continue;

            var start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            var word = text[start..i];

            // 함수 인자 안이면 이 from 은 extract(... from ...) 류다
            var inCall = parens.Count > 0 && parens.Peek();

            if (word.Equals("from", StringComparison.OrdinalIgnoreCase) && !inCall)
                i = ParseRefList(text, i, refs);
            else if (word.Equals("join", StringComparison.OrdinalIgnoreCase) && !inCall)
                i = ParseRef(text, i, refs, out _);
            else if (word.Equals("into", StringComparison.OrdinalIgnoreCase) && !inCall)
                i = ParseRef(text, i, refs, out _);
            else if (word.Equals("update", StringComparison.OrdinalIgnoreCase) && !inCall)
            {
                // SELECT … FOR UPDATE / ON UPDATE CASCADE 의 update 는 테이블 자리가 아니다
                var prev = PreviousWord(text, start);
                if (!prev.Equals("for", StringComparison.OrdinalIgnoreCase) &&
                    !prev.Equals("on", StringComparison.OrdinalIgnoreCase))
                    i = ParseRef(text, i, refs, out _);
            }
        }
        return refs;
    }

    /// <summary>FROM 뒤 콤마 목록: <c>from a x, b y</c> — 옛날식 조인도 전부 잡는다.</summary>
    private static int ParseRefList(string text, int pos, List<TableRef> refs)
    {
        while (true)
        {
            pos = ParseRef(text, pos, refs, out var parsed);
            if (!parsed) return pos;
            var next = SkipWs(text, pos);
            if (next >= text.Length || text[next] != ',') return pos;
            pos = next + 1;
        }
    }

    /// <summary>테이블 참조 하나: <c>[schema.]name [as] [alias]</c>. 함수 호출·서브쿼리는 건너뛴다.</summary>
    private static int ParseRef(string text, int pos, List<TableRef> refs, out bool parsed)
    {
        parsed = false;
        var i = SkipWs(text, pos);
        if (i >= text.Length || !IsWordStart(text[i])) return pos;   // '(' 서브쿼리 등

        var firstStart = i;
        while (i < text.Length && IsWordChar(text[i])) i++;
        var first = text[firstStart..i];

        string? schema = null;
        var name = first;
        var nameStart = firstStart;

        var j = SkipWs(text, i);
        if (j < text.Length && text[j] == '.')
        {
            j = SkipWs(text, j + 1);
            if (j >= text.Length || !IsWordStart(text[j])) return i;
            schema = first;
            nameStart = j;
            while (j < text.Length && IsWordChar(text[j])) j++;
            name = text[nameStart..j];
            i = j;
        }

        // 함수 호출 (generate_series(...) 등) — 테이블이 아니고, 인자 콤마와 섞이므로 목록도 중단
        var k = SkipWs(text, i);
        if (k < text.Length && text[k] == '(') return i;

        string? alias = null;
        if (k < text.Length && IsWordStart(text[k]))
        {
            var wStart = k;
            var w = k;
            while (w < text.Length && IsWordChar(text[w])) w++;
            var word = text[wStart..w];
            if (word.Equals("as", StringComparison.OrdinalIgnoreCase))
            {
                var aStart = SkipWs(text, w);
                if (aStart < text.Length && IsWordStart(text[aStart]))
                {
                    var a = aStart;
                    while (a < text.Length && IsWordChar(text[a])) a++;
                    alias = text[aStart..a];
                    i = a;
                }
            }
            else if (!NotAliases.Contains(word) && !Keywords.Contains(word))
            {
                alias = word;
                i = w;
            }
        }

        refs.Add(new TableRef(schema, name, nameStart, alias));
        parsed = true;
        return i;
    }

    // ---------- CTE·서브쿼리 별칭 ----------

    /// <summary>
    /// 카탈로그에 없는 게 정상인 이름들: <c>with x as (…)</c> 의 x,
    /// <c>(select …) t</c> 의 t. 이름만 모으고 컬럼 검사는 하지 않는다.
    /// </summary>
    private static HashSet<string> CollectLocalNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     text, @"\b([A-Za-z_]\w*)\s*(?:\([\w\s,]*\))?\s+as\s*\(",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (!Keywords.Contains(m.Groups[1].Value))
                names.Add(m.Groups[1].Value);
        }
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     text, @"\)\s*(?:as\s+)?([A-Za-z_]\w*)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (!Keywords.Contains(m.Groups[1].Value))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    // ---------- 주석·문자열 마스킹 ----------

    /// <summary>
    /// 주석·문자열·따옴표 식별자·달러 인용을 공백으로 바꾼다 (길이 유지 — 오프셋 보존).
    /// 그 안의 from/컬럼 참조를 검사 대상에서 빼기 위해서다.
    /// </summary>
    internal static string Mask(string sql)
    {
        var chars = sql.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            var c = chars[i];
            if (c == '-' && i + 1 < chars.Length && chars[i + 1] == '-')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var depth = 0;
                while (i < chars.Length)
                {
                    if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*') { depth++; chars[i++] = ' '; chars[i++] = ' '; }
                    else if (chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/')
                    {
                        chars[i++] = ' '; chars[i++] = ' ';
                        if (--depth == 0) break;
                    }
                    else if (chars[i] != '\n') chars[i++] = ' ';
                    else i++;
                }
            }
            else if (c is '\'' or '"')
            {
                var quote = c;
                chars[i++] = ' ';
                while (i < chars.Length)
                {
                    if (chars[i] == quote)
                    {
                        chars[i++] = ' ';
                        if (i < chars.Length && chars[i] == quote) chars[i++] = ' ';   // '' 이스케이프
                        else break;
                    }
                    else { if (chars[i] != '\n') chars[i] = ' '; i++; }
                }
            }
            else if (c == '$')
            {
                // PG 달러 인용 $tag$ … $tag$
                var t = i + 1;
                while (t < chars.Length && IsWordChar(chars[t])) t++;
                if (t < chars.Length && chars[t] == '$')
                {
                    var tag = sql[i..(t + 1)];
                    var close = sql.IndexOf(tag, t + 1, StringComparison.Ordinal);
                    var end = close < 0 ? chars.Length : close + tag.Length;
                    for (var k = i; k < end; k++)
                        if (chars[k] != '\n') chars[k] = ' ';
                    i = end;
                }
                else i++;
            }
            else i++;
        }
        return new string(chars);
    }

    // ---------- 문자 헬퍼 ----------

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static int SkipWs(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static int LastNonWs(string text, int i)
    {
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i;
    }

    private static string WordEndingAt(string text, int end)
    {
        var s = end;
        while (s >= 0 && IsWordChar(text[s])) s--;
        return text[(s + 1)..(end + 1)];
    }

    private static string PreviousWord(string text, int wordStart)
    {
        var e = LastNonWs(text, wordStart - 1);
        if (e < 0 || !IsWordChar(text[e])) return "";
        return WordEndingAt(text, e);
    }
}
