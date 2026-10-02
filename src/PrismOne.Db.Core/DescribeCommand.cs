using PrismOne.Db.Core.Providers;

namespace PrismOne.Db.Core;

/// <summary>
/// DESC 명령을 실행할 카탈로그 조회. 결과는 (이름, NOT NULL 여부 텍스트, 타입) 세 컬럼이고
/// 대상 이름은 문장에 끼우지 않고 <paramref name="Parameters"/> 로 바인딩한다.
/// </summary>
/// <param name="EmptyMeansMissing">
/// 행이 없으면 대상이 없다는 뜻인지. PG 는 regclass 가 없는 이름을 이미 오류로 답하고,
/// 컬럼 없는 테이블(<c>create table t()</c>)도 만들 수 있어 false 다.
/// </param>
public sealed record DescribeQuery(
    string Sql,
    IReadOnlyList<KeyValuePair<string, string?>> Parameters,
    bool EmptyMeansMissing);

/// <summary>
/// SQL*Plus 의 <c>DESC[RIBE] [schema.]object</c> — 에디터에서 친 그대로 실행되게 한다.
/// 어느 DB 서버도 이 문장을 받지 않으므로(SQL*Plus 가 클라이언트에서 처리하던 명령이다)
/// 같은 모양(Name / Null? / Type)의 카탈로그 조회로 바꿔 보낸다. 결과가 보통 결과셋이라
/// 그리드·Show Text·내보내기가 그대로 동작한다.
///
/// SQL*Plus 처럼 줄 명령이라 세미콜론이 없어도 줄 끝에서 끝나고(<see cref="StatementSplitter"/>),
/// 명령어는 DESC 부터 DESCRIBE 까지 어떤 줄임도 받는다.
/// </summary>
/// <param name="Parts">대상 이름의 각 부분 — 친 그대로(따옴표 포함).</param>
public sealed record DescribeCommand(IReadOnlyList<string> Parts)
{
    private const string Keyword = "DESCRIBE";
    private const int MinKeywordLength = 4;   // DESC
    private const int MaxNameParts = 3;       // database.schema.object 까지 (PG regclass 가 받는 최대)

    private const string Usage = "Usage: DESCRIBE [schema.]object";

    /// <summary>표시용 대상 이름 — 부분 사이 공백만 걷어낸 것.</summary>
    public string Target => string.Join('.', Parts);

    /// <summary>
    /// pos 부터(앞 공백은 건너뜀) DESC 명령으로 시작하는지. 문장 분리기가 줄 명령을 알아보는 데 쓴다.
    /// </summary>
    public static bool IsCommandStart(string sql, int pos = 0) => KeywordEnd(sql, pos) > 0;

    /// <summary>pos 부터(앞 공백·블록 주석은 건너뜀) DESC 명령어가 끝나는 위치, 명령이 아니면 -1.</summary>
    public static int KeywordEnd(string sql, int pos = 0) => ReadKeyword(sql, SkipSpaces(sql, pos));

    /// <summary>
    /// DESC 명령이면 대상을 읽어 돌려주고, 아니면 null. 명령어는 맞는데 대상이 형식에
    /// 맞지 않으면 SQL*Plus 처럼 사용법을 담아 <see cref="FormatException"/> 을 던진다.
    /// </summary>
    public static DescribeCommand? Parse(string sql)
    {
        var keywordEnd = KeywordEnd(sql);
        if (keywordEnd < 0)
            return null;

        var parts = new List<string>();
        var pos = keywordEnd;
        while (true)
        {
            pos = SkipSpaces(sql, pos);
            var partEnd = ReadIdentifier(sql, pos);
            if (partEnd < 0 || parts.Count == MaxNameParts)
                throw UsageError(sql.Trim());
            parts.Add(sql[pos..partEnd]);
            pos = SkipSpaces(sql, partEnd);
            if (pos < sql.Length && sql[pos] == '.') { pos++; continue; }
            break;
        }

        if (pos < sql.Length && sql[pos] == ';')
            pos = SkipSpaces(sql, pos + 1);
        if (pos < sql.Length)
            throw UsageError(sql.Trim());
        return new DescribeCommand(parts);
    }

    /// <summary>
    /// 이 DB 에서 실행할 조회. DESC 를 흉내 낼 카탈로그가 없는 DB(Mongo)면 null —
    /// 호출부는 원문을 그대로 보낸다. 이름 부분 수가 DB 에 맞지 않으면 사용법 오류.
    /// </summary>
    public DescribeQuery? BuildQuery(DbKind kind) => kind switch
    {
        DbKind.PostgreSql => new DescribeQuery(PostgresSql, [new("target", Target)], EmptyMeansMissing: false),
        DbKind.Oracle => BuildOracle(),
        DbKind.Sqlite => BuildSqlite(),
        _ => null,
    };

    /// <summary>
    /// 대상 이름은 텍스트로 바인딩해 <c>regclass</c> 입력 규칙에 해석을 맡긴다 — 따옴표 없는
    /// 이름은 소문자로 접히고(Oracle 습관대로 대문자로 쳐도 찾는다), 스키마를 안 쓰면
    /// search_path 를 따르며, 없으면 PG 가 <c>relation "…" does not exist</c> 로 답한다.
    /// 리터럴로 끼워 넣지 않는 건 standard_conforming_strings=off 서버에서 <c>\'</c> 로
    /// 문장 밖으로 빠져나갈 수 있어서다.
    /// </summary>
    private const string PostgresSql = """
        SELECT a.attname,
               CASE WHEN a.attnotnull THEN 'NOT NULL' ELSE '' END,
               pg_catalog.format_type(a.atttypid, a.atttypmod)
          FROM pg_catalog.pg_attribute a
         WHERE a.attrelid = @target::text::pg_catalog.regclass
           AND a.attnum > 0
           AND NOT a.attisdropped
         ORDER BY a.attnum
        """;

    /// <summary>
    /// SQL*Plus 의 이름 해석 순서 — 내(또는 지정한) 스키마의 테이블·뷰, 그 스키마의 동의어,
    /// 스키마를 안 썼으면 PUBLIC 동의어. 타입 표기도 SQL*Plus 와 같게 만든다
    /// (VARCHAR2(20 CHAR), NUMBER(10,2), INTEGER 는 NUMBER(38)). 11g 에서도 돌게
    /// FETCH FIRST 대신 ROWNUM 을 쓴다. 같은 바인드를 여러 번 쓰므로 이름으로 바인딩한다.
    /// </summary>
    private const string OracleSql = """
        SELECT c.column_name,
               CASE WHEN c.nullable = 'N' THEN 'NOT NULL' END,
               CASE
                 WHEN c.data_type IN ('VARCHAR2', 'CHAR')
                   THEN c.data_type || '(' || c.char_length || CASE WHEN c.char_used = 'C' THEN ' CHAR' END || ')'
                 WHEN c.data_type IN ('NVARCHAR2', 'NCHAR')
                   THEN c.data_type || '(' || c.char_length || ')'
                 WHEN c.data_type IN ('RAW', 'UROWID')
                   THEN c.data_type || '(' || c.data_length || ')'
                 WHEN c.data_type = 'NUMBER' AND c.data_precision IS NOT NULL
                   THEN 'NUMBER(' || c.data_precision || CASE WHEN c.data_scale <> 0 THEN ',' || c.data_scale END || ')'
                 WHEN c.data_type = 'NUMBER' AND c.data_scale = 0
                   THEN 'NUMBER(38)'
                 WHEN c.data_type = 'NUMBER' AND c.data_scale IS NOT NULL
                   THEN 'NUMBER(*,' || c.data_scale || ')'
                 WHEN c.data_type = 'FLOAT'
                   THEN 'FLOAT(' || c.data_precision || ')'
                 ELSE c.data_type
               END
          FROM all_tab_columns c
          JOIN (SELECT owner, table_name
                  FROM (SELECT owner, table_name
                          FROM (SELECT t.owner, t.table_name, 0 AS pri
                                  FROM all_tab_columns t
                                 WHERE t.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))
                                   AND t.table_name = :name
                                   AND t.column_id = 1
                                UNION ALL
                                SELECT s.table_owner, s.table_name, CASE s.owner WHEN 'PUBLIC' THEN 2 ELSE 1 END
                                  FROM all_synonyms s
                                 WHERE s.synonym_name = :name
                                   AND s.db_link IS NULL
                                   AND (s.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))
                                        OR (:owner IS NULL AND s.owner = 'PUBLIC'))
                                   -- 볼 수 없는 대상을 가리키는 동의어가 PUBLIC 동의어를 가로채지 않게
                                   AND EXISTS (SELECT 1 FROM all_tab_columns x
                                                WHERE x.owner = s.table_owner AND x.table_name = s.table_name))
                         ORDER BY pri)
                 WHERE ROWNUM = 1) r
            ON c.owner = r.owner AND c.table_name = r.table_name
         WHERE c.column_id IS NOT NULL   -- INVISIBLE 컬럼 — SQL*Plus 기본(COLINVISIBLE OFF)처럼 숨긴다
         ORDER BY c.column_id
        """;

    private DescribeQuery BuildOracle()
    {
        // SQL*Plus 의 schema.package.procedure·@dblink 는 다루지 않는다
        if (Parts.Count > 2)
            throw UsageError(Target);
        // Oracle 은 따옴표 없는 이름을 대문자로 접는다
        var owner = Parts.Count == 2 ? Unquote(Parts[0], upper: true) : null;
        var name = Unquote(Parts[^1], upper: true);
        return new DescribeQuery(OracleSql, [new("owner", owner), new("name", name)], EmptyMeansMissing: true);
    }

    /// <summary>pragma_table_info 는 인자를 바인딩할 수 있는 테이블 값 함수다 (SQLite 3.16+).</summary>
    private DescribeQuery BuildSqlite()
    {
        if (Parts.Count > 2)
            throw UsageError(Target);
        var schema = Parts.Count == 2 ? Unquote(Parts[0], upper: false) : null;
        var name = Unquote(Parts[^1], upper: false);
        var source = schema is null ? "pragma_table_info(@name)" : "pragma_table_info(@name, @schema)";
        var sql = $"""
            SELECT name, CASE WHEN "notnull" <> 0 THEN 'NOT NULL' ELSE '' END, type
              FROM {source}
             ORDER BY cid
            """;
        List<KeyValuePair<string, string?>> parameters = [new("@name", name)];
        if (schema is not null)
            parameters.Add(new("@schema", schema));
        return new DescribeQuery(sql, parameters, EmptyMeansMissing: true);
    }

    /// <summary>"…" 는 벗기고("" → "), 따옴표 없는 이름은 필요하면 대문자로 접는다.</summary>
    private static string Unquote(string part, bool upper) =>
        part.StartsWith('"')
            ? part[1..^1].Replace("\"\"", "\"")
            : upper ? part.ToUpperInvariant() : part;

    /// <summary>pos 의 단어가 DESCRIBE 의 줄임(4자 이상)이면 단어 끝 위치, 아니면 -1.</summary>
    private static int ReadKeyword(string sql, int pos)
    {
        var end = pos;
        while (end < sql.Length && char.IsAsciiLetter(sql[end])) end++;
        var length = end - pos;
        if (length < MinKeywordLength || length > Keyword.Length)
            return -1;
        if (string.Compare(sql, pos, Keyword, 0, length, StringComparison.OrdinalIgnoreCase) != 0)
            return -1;
        // 단어가 여기서 끝나야 한다 — "desc_x" 같은 식별자는 명령이 아니다
        if (end < sql.Length && !char.IsWhiteSpace(sql[end]) && sql[end] is not (';' or '"' or '/'))
            return -1;
        return end;
    }

    /// <summary>pos 의 식별자(따옴표 포함) 끝 위치, 식별자가 아니면 -1.</summary>
    private static int ReadIdentifier(string sql, int pos)
    {
        if (pos >= sql.Length)
            return -1;
        if (sql[pos] == '"')
        {
            var i = pos + 1;
            while (i < sql.Length)
            {
                if (sql[i] == '"')
                {
                    if (i + 1 < sql.Length && sql[i + 1] == '"') { i += 2; continue; }   // "" 이스케이프
                    return i - pos > 1 ? i + 1 : -1;   // "" 빈 이름은 안 된다
                }
                i++;
            }
            return -1;   // 닫는 따옴표 없음
        }
        if (!(char.IsLetter(sql[pos]) || sql[pos] == '_'))
            return -1;
        var end = pos + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] is '_' or '$' or '#')) end++;
        return end;
    }

    /// <summary>공백과 블록 주석(<c>/* … */</c>)을 건너뛴다 — <c>desc t /* 메모 */</c> 도 받게.</summary>
    private static int SkipSpaces(string sql, int pos)
    {
        while (pos < sql.Length)
        {
            if (char.IsWhiteSpace(sql[pos])) { pos++; continue; }
            if (pos + 1 < sql.Length && sql[pos] == '/' && sql[pos + 1] == '*')
            {
                var close = sql.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                pos = close < 0 ? sql.Length : close + 2;
                continue;
            }
            break;
        }
        return pos;
    }

    private static FormatException UsageError(string what) =>
        new($"{Usage} — 대상 이름을 읽지 못했습니다: {what}");
}
