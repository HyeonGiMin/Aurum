using System.Text;
using Npgsql;

namespace PrismOne.Db.Core;

/// <summary>mini-psql 실행 단위. Statement 는 그대로, Gexec 는 결과 각 셀을 SQL 로 재실행.</summary>
public abstract record PsqlUnit(string Sql)
{
    public sealed record Statement(string Text) : PsqlUnit(Text);
    public sealed record Gexec(string Text) : PsqlUnit(Text);
}

/// <summary>
/// psql 없이 sql/*.sql 을 실행하기 위한 mini-psql 프리프로세서 (STATUS.md 의 A안).
/// repo 의 SQL 파일이 실제로 쓰는 psql 기능만 지원한다:
///   \set var value · \if :{?var} / \else / \endif · \gexec ·
///   :'var'(리터럴) · :"var"(식별자) · :var(원문) 치환.
/// 치환은 psql 과 같이 작은따옴표·큰따옴표·달러쿼팅·주석 안에서는 하지 않는다.
/// 지원하지 않는 메타커맨드는 조용히 넘기지 않고 예외로 알린다.
/// </summary>
public static class PsqlScript
{
    /// <summary>파일 텍스트를 실행 단위 목록으로 바꾼다. variables 는 \set 으로 갱신될 수 있다.</summary>
    public static List<PsqlUnit> Parse(string text, Dictionary<string, string> variables)
    {
        var units = new List<PsqlUnit>();
        var buffer = new StringBuilder();
        // \if 중첩 상태: (이 분기가 참인가, 바깥 분기들이 전부 참인가)
        var ifStack = new Stack<(bool Taken, bool ParentActive)>();
        var active = true;

        void FlushAsStatements()
        {
            EmitBuffer(units, buffer, variables, lastIsGexec: false);
        }

        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine;
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('\\'))
            {
                var (command, argument) = SplitMeta(trimmed);
                switch (command)
                {
                    case "set":
                        if (active)
                        {
                            var space = argument.IndexOf(' ');
                            if (space < 0)
                                variables[argument] = "";
                            else
                                variables[argument[..space]] = argument[(space + 1)..].Trim();
                        }
                        continue;
                    case "if":
                        ifStack.Push((Taken: active && EvaluateCondition(argument, variables), ParentActive: active));
                        active = ifStack.Peek().Taken;
                        continue;
                    case "else":
                        if (ifStack.Count == 0)
                            throw new FormatException(@"\else without \if");
                        var frame = ifStack.Peek();
                        active = frame.ParentActive && !frame.Taken;
                        continue;
                    case "endif":
                        if (ifStack.Count == 0)
                            throw new FormatException(@"\endif without \if");
                        active = ifStack.Pop().ParentActive;
                        continue;
                    case "gexec":
                        if (active)
                            EmitBuffer(units, buffer, variables, lastIsGexec: true);
                        continue;
                    case "echo":
                        continue;   // 설치 로그는 러너가 파일 단위로 남긴다
                    default:
                        throw new NotSupportedException(
                            $@"지원하지 않는 psql 메타커맨드: \{command} — PsqlScript(mini-psql)에 추가가 필요합니다");
                }
            }

            if (!active)
                continue;

            // 파일들의 실제 사용 형태: SQL 줄 끝에 \gexec 가 붙는다.
            // (줄 중간의 \gexec 언급은 주석일 수 있으므로 그대로 버퍼로 보낸다 —
            //  주석은 StatementSplitter 가 걸러낸다)
            var end = line.TrimEnd();
            if (end.EndsWith("\\gexec", StringComparison.Ordinal))
            {
                buffer.AppendLine(end[..^"\\gexec".Length]);
                EmitBuffer(units, buffer, variables, lastIsGexec: true);
                continue;
            }

            buffer.AppendLine(line);
        }

        if (ifStack.Count > 0)
            throw new FormatException(@"\if 가 \endif 로 닫히지 않았습니다");
        FlushAsStatements();
        return units;
    }

    /// <summary>버퍼를 문장으로 쪼개 내보낸다. lastIsGexec 면 마지막 문장(세미콜론 없는 쿼리)이 \gexec 대상.</summary>
    private static void EmitBuffer(
        List<PsqlUnit> units, StringBuilder buffer, Dictionary<string, string> variables, bool lastIsGexec)
    {
        var text = buffer.ToString();
        buffer.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            if (lastIsGexec)
                throw new FormatException(@"\gexec 앞에 보낼 쿼리가 없습니다");
            return;
        }

        var substituted = Substitute(text, variables);
        var statements = StatementSplitter.Split(substituted);
        if (lastIsGexec && statements.Count == 0)
            throw new FormatException(@"\gexec 앞에 보낼 쿼리가 없습니다");

        for (var i = 0; i < statements.Count; i++)
        {
            var sql = statements[i].Text.Trim();
            if (sql.Length == 0)
                continue;
            units.Add(lastIsGexec && i == statements.Count - 1
                ? new PsqlUnit.Gexec(sql)
                : new PsqlUnit.Statement(sql));
        }
    }

    private static (string Command, string Argument) SplitMeta(string trimmed)
    {
        var body = trimmed[1..];
        var space = body.IndexOf(' ');
        return space < 0
            ? (body.Trim(), "")
            : (body[..space], body[(space + 1)..].Trim());
    }

    /// <summary>\if 조건 — :{?var}(정의 여부)와 psql 의 불리언 리터럴만 지원.</summary>
    private static bool EvaluateCondition(string expr, Dictionary<string, string> variables)
    {
        expr = expr.Trim();
        if (expr.StartsWith(":{?", StringComparison.Ordinal) && expr.EndsWith('}'))
            return variables.ContainsKey(expr[3..^1]);
        return expr.ToLowerInvariant() switch
        {
            "true" or "on" or "yes" or "1" => true,
            "false" or "off" or "no" or "0" => false,
            _ => throw new NotSupportedException($@"지원하지 않는 \if 조건: {expr}"),
        };
    }

    /// <summary>
    /// psql 변수 치환. 작은따옴표/큰따옴표/달러쿼팅/주석 안은 건드리지 않고,
    /// :: 캐스트는 변수로 보지 않는다. :'v' 는 리터럴 인용, :"v" 는 식별자 인용,
    /// :v 는 정의된 경우에만 원문 그대로.
    /// </summary>
    public static string Substitute(string sql, IReadOnlyDictionary<string, string> variables)
    {
        var sb = new StringBuilder(sql.Length + 64);
        var i = 0;
        var n = sql.Length;
        while (i < n)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i);
                var stop = nl < 0 ? n : nl + 1;
                sb.Append(sql, i, stop - i);
                i = stop;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                var depth = 0;
                var j = i;
                while (j < n)
                {
                    if (j + 1 < n && sql[j] == '/' && sql[j + 1] == '*') { depth++; j += 2; continue; }
                    if (j + 1 < n && sql[j] == '*' && sql[j + 1] == '/') { depth--; j += 2; if (depth == 0) break; continue; }
                    j++;
                }
                sb.Append(sql, i, j - i);
                i = j;
                continue;
            }
            if (c == '\'' || c == '"')
            {
                var j = i + 1;
                while (j < n)
                {
                    if (sql[j] == c)
                    {
                        if (j + 1 < n && sql[j + 1] == c) { j += 2; continue; }   // '' / "" 이스케이프
                        j++;
                        break;
                    }
                    j++;
                }
                sb.Append(sql, i, j - i);
                i = j;
                continue;
            }
            if (c == '$')
            {
                var tagEnd = i + 1;
                while (tagEnd < n && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_'))
                    tagEnd++;
                if (tagEnd < n && sql[tagEnd] == '$')
                {
                    var tag = sql[i..(tagEnd + 1)];
                    var close = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                    var stop = close < 0 ? n : close + tag.Length;
                    sb.Append(sql, i, stop - i);
                    i = stop;
                    continue;
                }
            }
            if (c == ':')
            {
                if (i + 1 < n && sql[i + 1] == ':')   // :: 캐스트
                {
                    sb.Append("::");
                    i += 2;
                    continue;
                }
                if (i + 1 < n && (sql[i + 1] == '\'' || sql[i + 1] == '"'))
                {
                    var quote = sql[i + 1];
                    var close = sql.IndexOf(quote, i + 2);
                    if (close > i + 2)
                    {
                        var name = sql[(i + 2)..close];
                        if (!variables.TryGetValue(name, out var value))
                            throw new KeyNotFoundException($"psql 변수가 정의되지 않았습니다: {name}");
                        sb.Append(quote == '\''
                            ? "'" + value.Replace("'", "''") + "'"
                            : "\"" + value.Replace("\"", "\"\"") + "\"");
                        i = close + 1;
                        continue;
                    }
                }
                if (i + 1 < n && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    var j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
                        j++;
                    var name = sql[(i + 1)..j];
                    if (variables.TryGetValue(name, out var value))
                    {
                        sb.Append(value);
                        i = j;
                        continue;
                    }
                    // psql 처럼 미정의 :var 는 원문 그대로 둔다 (예: 시간 표기 '12:30' 밖의 콜론)
                }
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>PsqlScript 실행기 — 파일 하나를 접속 위에서 순서대로 실행한다.</summary>
public static class PsqlScriptRunner
{
    /// <summary>실행한 문장 수를 돌려준다. \gexec 는 결과 셀 각각을 SQL 로 실행한다.</summary>
    public static async Task<int> RunAsync(
        NpgsqlConnection conn,
        IReadOnlyList<PsqlUnit> units,
        Action<string>? verbose = null,
        CancellationToken ct = default)
    {
        var executed = 0;
        foreach (var unit in units)
        {
            switch (unit)
            {
                case PsqlUnit.Statement s:
                    verbose?.Invoke(FirstLine(s.Sql));
                    await using (var cmd = new NpgsqlCommand(s.Sql, conn) { CommandTimeout = 0 })
                        await cmd.ExecuteNonQueryAsync(ct);
                    executed++;
                    break;

                case PsqlUnit.Gexec g:
                    verbose?.Invoke(FirstLine(g.Sql) + @" \gexec");
                    var generated = new List<string>();
                    await using (var query = new NpgsqlCommand(g.Sql, conn) { CommandTimeout = 0 })
                    await using (var reader = await query.ExecuteReaderAsync(ct))
                    {
                        while (await reader.ReadAsync(ct))
                            for (var col = 0; col < reader.FieldCount; col++)
                                if (!await reader.IsDBNullAsync(col, ct))
                                    generated.Add(reader.GetString(col));
                    }
                    foreach (var sql in generated)
                    {
                        verbose?.Invoke("  gexec> " + FirstLine(sql));
                        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
                        await cmd.ExecuteNonQueryAsync(ct);
                        executed++;
                    }
                    executed++;   // gexec 원 쿼리 자체
                    break;
            }
        }
        return executed;
    }

    private static string FirstLine(string sql)
    {
        var line = sql.AsSpan();
        var nl = line.IndexOf('\n');
        if (nl >= 0) line = line[..nl];
        return line.Length > 100 ? string.Concat(line[..100], "…") : line.ToString();
    }
}
