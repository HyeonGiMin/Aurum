namespace PrismOne.Db.Core;

/// <summary>USER_ERRORS 한 줄. Line/Position 은 오브젝트 DDL 문장의 시작(1행)부터 센다.</summary>
public readonly record struct OracleCompileError(int Line, int Position, string Text);

/// <summary>
/// Oracle PL/Edit 파리티 3단계 — <c>CREATE [OR REPLACE] PROCEDURE|FUNCTION|PACKAGE
/// [BODY]|TRIGGER|TYPE [BODY]</c> 문장에서 오브젝트 이름/USER_ERRORS.TYPE 값을 뽑고,
/// USER_ERRORS 의 (LINE, POSITION) 을 에디터 오프셋으로 옮긴다. 서버 접속이 필요 없는
/// 순수 파싱이라 여기 둔다 — 실제 USER_ERRORS 조회는 QuerySession 의 몫.
/// </summary>
public static class OracleCompileErrorParser
{
    /// <summary>PACKAGE/TYPE 뒤에 BODY 가 붙으면 USER_ERRORS.TYPE 도 "PACKAGE BODY"/"TYPE BODY"다.</summary>
    private static readonly string[] BodyCapableObjects = ["PACKAGE", "TYPE"];
    private static readonly string[] PlainObjects = ["PROCEDURE", "FUNCTION", "TRIGGER"];

    /// <summary>문장이 컴파일 대상 오브젝트를 만드는 CREATE 문이면 (이름, USER_ERRORS.TYPE) 반환.</summary>
    public static (string Name, string ObjectType)? ParseObjectHeader(string sql)
    {
        var (word1, next1) = ReadWord(sql, 0);
        if (!word1.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
            return null;

        var (word2, next2) = ReadWord(sql, next1);
        if (word2.Equals("OR", StringComparison.OrdinalIgnoreCase))
        {
            var (_, next3) = ReadWord(sql, next2);   // REPLACE
            (word2, next2) = ReadWord(sql, next3);
        }

        string objectType;
        int afterType;
        if (Array.Exists(BodyCapableObjects, k => k.Equals(word2, StringComparison.OrdinalIgnoreCase)))
        {
            var (word3, next3) = ReadWord(sql, next2);
            if (word3.Equals("BODY", StringComparison.OrdinalIgnoreCase))
            {
                objectType = word2.ToUpperInvariant() + " BODY";
                afterType = next3;
            }
            else
            {
                objectType = word2.ToUpperInvariant();
                afterType = next2;
            }
        }
        else if (Array.Exists(PlainObjects, k => k.Equals(word2, StringComparison.OrdinalIgnoreCase)))
        {
            objectType = word2.ToUpperInvariant();
            afterType = next2;
        }
        else
        {
            return null;
        }

        var (name, _) = ReadWord(sql, afterType);
        return name.Length == 0 ? null : (name, objectType);
    }

    /// <summary>USER_ERRORS 오류 한 줄을 에디터 밑줄 구간으로. stmtStart 는 원문 안에서
    /// 이 CREATE 문이 시작하는 절대 오프셋. 다음 공백까지를 밑줄 폭으로 잡는 근사치다 —
    /// Oracle 은 컬럼 폭을 안 주므로 정확한 토큰 경계는 알 수 없다.</summary>
    public static SqlIssue ToSqlIssue(this OracleCompileError error, string stmtText, int stmtStart)
    {
        // USER_ERRORS.POSITION 은 1-based(줄의 첫 글자가 1) — 그대로 더하면 한 글자 밀린다
        var offset = Math.Min(stmtText.Length, LineOffset(stmtText, error.Line) + Math.Max(0, error.Position - 1));
        var length = 1;
        while (offset + length < stmtText.Length &&
               stmtText[offset + length] != '\n' && !char.IsWhiteSpace(stmtText[offset + length]))
            length++;
        return new SqlIssue(stmtStart + offset, length, error.Text.Trim());
    }

    /// <summary>1-based 줄 번호가 시작하는 문자 오프셋.</summary>
    private static int LineOffset(string s, int line)
    {
        var current = 1;
        var i = 0;
        while (current < line && i < s.Length)
        {
            if (s[i] == '\n') current++;
            i++;
        }
        return i;
    }

    private static (string Word, int NextPos) ReadWord(string s, int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        var start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
        return (s[start..pos], pos);
    }
}
