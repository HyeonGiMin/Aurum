using System.Text;
using System.Text.RegularExpressions;

namespace PrismOne.Db.Core.Mcp;

public enum McpSqlKind
{
    /// <summary>바로 실행해도 되는 읽기 전용 문장.</summary>
    ReadOnly,

    /// <summary>데이터·스키마를 바꾸거나 잠그거나, 판단할 수 없는 문장 — 사용자 확인이 필요하다.</summary>
    Write,
}

/// <summary>
/// AI 가 보낸 SQL 을 확인 없이 돌려도 되는지 가른다.
/// 판단이 애매하면 항상 <see cref="McpSqlKind.Write"/> 쪽으로 기운다 — 잘못 막으면 확인창 한 번이지만,
/// 잘못 통과시키면 운영 데이터가 바뀐다.
///
/// <b>방언 차이로 우회되지 않게</b> 모든 판정을 PG 식·Oracle 식 두 번 해서 한쪽이라도 쓰기면 쓰기다.
/// (Oracle 실행기는 줄 하나짜리 '/' 에서 문장을 끊고 '$' 를 인용으로 보지 않으며 q'[…]' 인용을 쓴다 —
/// 정책이 실행기와 다르게 나누면 "SELECT 하나" 로 보인 스크립트에서 DELETE 가 실행된다.)
///
/// 한계: 사용자 정의 함수(<c>select my_proc()</c>)의 부작용은 알 수 없다. 알려진 위험 함수만 막는다.
/// 수동 커밋 모드(기본)가 그 경우의 마지막 안전망이다.
/// </summary>
public static partial class McpSqlPolicy
{
    // PG: SELECT … INTO new_table 은 테이블을 만든다. FOR UPDATE/SHARE 는 행을 잠근다.
    [GeneratedRegex(@"\binto\b|\bfor\s+(update|share|no\s+key\s+update|key\s+share)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SideEffectClause();

    // SELECT 안에서 불러도 효과가 남는(또는 트랜잭션 밖에서 남는) 내장 함수·패키지.
    [GeneratedRegex(
        @"\b(nextval|setval|set_config|pg_sleep\w*|pg_advisory_\w+|pg_try_advisory_\w+|dblink\w*|lo_\w+|" +
        @"pg_terminate_backend|pg_cancel_backend|pg_reload_conf|pg_rotate_logfile|pg_switch_wal|pg_promote|" +
        @"pg_create_\w+|pg_drop_\w+|pg_notify|pg_read_\w+|pg_ls_\w+|pg_stat_reset\w*|pg_file_\w+|" +
        @"pg_logical_emit_message|pg_wal_replay_\w+|query_to_xml\w*|cursor_to_xml\w*|" +
        @"load_extension|writefile|readfile)\s*\(" +
        @"|\b(dbms|utl|owa)_\w+\s*\.",
        RegexOptions.IgnoreCase)]
    private static partial Regex SideEffectFunction();

    // 유니코드 이스케이프 식별자·문자열(U&"…", U&'…')은 이름을 숨길 수 있다 — 판정하지 않고 쓰기로 본다.
    [GeneratedRegex(@"\bu&['""]", RegexOptions.IgnoreCase)]
    private static partial Regex UnicodeEscape();

    public static McpSqlKind Classify(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || UnicodeEscape().IsMatch(sql))
            return McpSqlKind.Write;

        foreach (var oracle in (ReadOnlySpan<bool>)[false, true])
        {
            if (!FavoriteSql.IsSelectOnly(sql, oracle))
                return McpSqlKind.Write;
            var visible = Mask(sql, oracle);
            if (SideEffectClause().IsMatch(visible) || SideEffectFunction().IsMatch(visible))
                return McpSqlKind.Write;
        }
        return McpSqlKind.ReadOnly;
    }

    /// <summary>
    /// 실행기가 "코드"로 읽는 부분만 남긴다: 주석 → 공백, 문자열 → <c>''</c>,
    /// 인용 식별자 → 안의 이름만(<c>"nextval"(…)</c> 같은 호출을 숨기지 못하게).
    /// 판정이 엇갈릴 수 있는 곳(중첩 주석, E'…' 백슬래시)은 덜 가리는 쪽으로 처리한다 — 덜 가리면 오탐일 뿐이다.
    /// </summary>
    internal static string Mask(string sql, bool oracle)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c == '-' && next == '-')
            {
                var nl = sql.IndexOf('\n', i);
                i = nl < 0 ? sql.Length : nl;
                sb.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
                sb.Append(' ');
            }
            else if (oracle && c is 'q' or 'Q' && next == '\'' && StartsQQuote(sql, i))
            {
                i = SkipOracleQQuote(sql, i);
                sb.Append("''");
            }
            else if (c == '\'')
            {
                i = SkipSingleQuoted(sql, i);
                sb.Append("''");
            }
            else if (c == '"')
            {
                var close = sql.IndexOf('"', i + 1);
                var end = close < 0 ? sql.Length : close;
                sb.Append(' ');
                foreach (var ch in sql.AsSpan(i + 1, end - i - 1))
                    sb.Append(IsIdentChar(ch) ? ch : ' ');
                sb.Append(' ');
                i = close < 0 ? sql.Length : close + 1;
            }
            else if (!oracle && c == '$' && !IsIdentChar(Prev(sql, i)) && DollarTag(sql, i) is { } tag)
            {
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + tag.Length;
                sb.Append("''");
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static char Prev(string sql, int i) => i > 0 ? sql[i - 1] : ' ';

    /// <summary>q' 가 식별자 중간이 아닌지 — <c>q'[…]'</c> 와 국가 문자셋 <c>nq'[…]'</c> 둘 다.</summary>
    private static bool StartsQQuote(string sql, int i)
    {
        var before = Prev(sql, i);
        if (!IsIdentChar(before))
            return true;
        return before is 'n' or 'N' && !IsIdentChar(Prev(sql, i - 1));
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private static int SkipSingleQuoted(string sql, int start)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }   // '' 이스케이프
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    /// <summary>Oracle q'[…]' · q'{…}' · q'!…!' — 닫는 구분자 바로 뒤의 ' 까지.</summary>
    private static int SkipOracleQQuote(string sql, int start)
    {
        if (start + 2 >= sql.Length)
            return sql.Length;
        var open = sql[start + 2];
        var closeChar = open switch { '[' => ']', '(' => ')', '{' => '}', '<' => '>', _ => open };
        var close = sql.IndexOf($"{closeChar}'", start + 3, StringComparison.Ordinal);
        return close < 0 ? sql.Length : close + 2;
    }

    /// <summary>PG 달러 인용 태그 (<c>$$</c> 또는 <c>$tag$</c>). 아니면 null (<c>$1</c> 같은 매개변수).</summary>
    private static string? DollarTag(string sql, int start)
    {
        var i = start + 1;
        while (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_' || (i > start + 1 && char.IsDigit(sql[i]))))
            i++;
        return i < sql.Length && sql[i] == '$' ? sql[start..(i + 1)] : null;
    }
}
