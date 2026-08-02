namespace PrismOne.Db.Core;

/// <summary>원문 내 위치를 보존하는 SQL 문장 하나. End 는 세미콜론 다음(exclusive).</summary>
public sealed record SqlStatement(string Text, int Start, int End);

/// <summary>
/// SQL 텍스트를 문장 단위로 분리한다 (Golden 의 "커서 위치 문장 실행"용).
/// 작은따옴표(E-string 백슬래시 포함)·큰따옴표 식별자·주석(중첩 블록 포함)·
/// 달러쿼팅($tag$…$tag$) 안의 세미콜론은 구분자로 보지 않는다.
/// </summary>
public static class StatementSplitter
{
    public static List<SqlStatement> Split(string sql)
    {
        var result = new List<SqlStatement>();
        var i = 0;
        var n = sql.Length;
        var stmtStart = -1;   // 현재 문장의 첫 유효 문자 위치

        while (i < n)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i + 2);
                i = nl < 0 ? n : nl + 1;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i = SkipBlockComment(sql, i);
                continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (stmtStart < 0) stmtStart = i;

            switch (c)
            {
                case ';':
                    AddStatement(result, sql, stmtStart, i, i + 1);
                    stmtStart = -1;
                    i++;
                    break;
                case '\'':
                    i = SkipSingleQuoted(sql, i);
                    break;
                case '"':
                    i = SkipDoubleQuoted(sql, i);
                    break;
                case '$':
                    var after = SkipDollarQuote(sql, i);
                    i = after < 0 ? i + 1 : after;
                    break;
                default:
                    i++;
                    break;
            }
        }

        if (stmtStart >= 0)
            AddStatement(result, sql, stmtStart, n, n);
        return result;
    }

    /// <summary>커서 위치의 문장. 문장 사이 공백이면 앞 문장, 그마저 없으면 다음 문장.</summary>
    public static SqlStatement? StatementAt(string sql, int caret)
    {
        var stmts = Split(sql);
        if (stmts.Count == 0) return null;
        foreach (var s in stmts)
            if (caret >= s.Start && caret <= s.End)
                return s;
        SqlStatement? prev = null;
        foreach (var s in stmts)
        {
            if (s.End <= caret) prev = s;
            else break;
        }
        return prev ?? stmts[0];
    }

    private static void AddStatement(List<SqlStatement> result, string sql, int start, int textEnd, int rangeEnd)
    {
        var text = sql[start..textEnd].TrimEnd();
        if (text.Length > 0)
            result.Add(new SqlStatement(text, start, rangeEnd));
    }

    private static int SkipBlockComment(string s, int i)
    {
        // PG 는 블록 주석 중첩을 허용한다
        var depth = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') { depth++; i += 2; }
            else if (i + 1 < s.Length && s[i] == '*' && s[i + 1] == '/')
            {
                depth--; i += 2;
                if (depth == 0) return i;
            }
            else i++;
        }
        return s.Length;
    }

    private static int SkipSingleQuoted(string s, int i)
    {
        // E'…' 는 백슬래시 이스케이프 허용
        var escape = i > 0 && (s[i - 1] is 'E' or 'e') && (i < 2 || !IsIdentChar(s[i - 2]));
        i++;
        while (i < s.Length)
        {
            if (escape && s[i] == '\\') { i += 2; continue; }
            if (s[i] == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'') { i += 2; continue; }   // '' 이스케이프
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static int SkipDoubleQuoted(string s, int i)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>s[i]=='$' 에서 달러쿼트면 닫는 구분자 다음 위치, 아니면 -1.</summary>
    private static int SkipDollarQuote(string s, int i)
    {
        var j = i + 1;
        while (j < s.Length && IsIdentChar(s[j])) j++;
        if (j >= s.Length || s[j] != '$') return -1;
        if (j > i + 1 && char.IsDigit(s[i + 1])) return -1;   // 태그는 숫자로 시작 못 함
        var delim = s.Substring(i, j - i + 1);
        var close = s.IndexOf(delim, j + 1, StringComparison.Ordinal);
        return close < 0 ? s.Length : close + delim.Length;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
