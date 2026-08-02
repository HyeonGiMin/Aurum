using System.Text;

namespace PrismOne.Db.Core;

/// <summary>SQL 문장에서 찾은 바인드 변수 (Golden 의 :var 프롬프트).</summary>
public sealed record BindVariable(string Name);

/// <summary>
/// `:name` 형태 바인드 변수를 찾고, Npgsql 파라미터(@name)로 바꿔준다.
/// 문자열·주석·달러쿼팅 안, `::타입캐스트` 는 변수로 보지 않는다.
/// </summary>
public static class BindVariables
{
    public static List<BindVariable> Find(string sql)
    {
        var names = new List<BindVariable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Scan(sql, (name, _, _) =>
        {
            if (seen.Add(name))
                names.Add(new BindVariable(name));
        });
        return names;
    }

    /// <summary>`:name` → `@name` 치환 (Npgsql 이 파라미터로 처리).</summary>
    public static string Rewrite(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var last = 0;
        Scan(sql, (name, start, end) =>
        {
            sb.Append(sql, last, start - last);
            sb.Append('@').Append(name);
            last = end;
        });
        sb.Append(sql, last, sql.Length - last);
        return sb.ToString();
    }

    /// <summary>변수 발견 시 (이름, 시작오프셋, 끝오프셋) 콜백. 순서 보장.</summary>
    private static void Scan(string sql, Action<string, int, int> onFound)
    {
        var i = 0;
        var n = sql.Length;
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
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                continue;
            }
            if (c == '\'' || c == '"')
            {
                var quote = c;
                i++;
                while (i < n)
                {
                    if (sql[i] == quote)
                    {
                        if (i + 1 < n && sql[i + 1] == quote) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == '$')
            {
                var j = i + 1;
                while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                if (j < n && sql[j] == '$')
                {
                    var delim = sql.Substring(i, j - i + 1);
                    var close = sql.IndexOf(delim, j + 1, StringComparison.Ordinal);
                    i = close < 0 ? n : close + delim.Length;
                    continue;
                }
                i++;
                continue;
            }
            if (c == ':')
            {
                if (i + 1 < n && sql[i + 1] == ':')   // ::타입캐스트
                {
                    i += 2;
                    continue;
                }
                var start = i;
                var j = i + 1;
                if (j < n && (char.IsLetter(sql[j]) || sql[j] == '_'))
                {
                    while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                    onFound(sql[(start + 1)..j], start, j);
                    i = j;
                    continue;
                }
            }
            i++;
        }
    }
}
