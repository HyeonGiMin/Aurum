using System.Text;
using Oracle.ManagedDataAccess.Client;

namespace PrismOne.Db.Core.Providers;

/// <summary>Object Browser 에 표시할 프로시저/함수/패키지 한 개. PACKAGE 와 PACKAGE BODY 는
/// all_objects 에 별도 행이라 따로 나온다.</summary>
public sealed record OracleRoutine(string Owner, string Name, string ObjectType);

/// <summary>
/// Oracle ERD 카탈로그. 읽기 전용 (all_* 데이터 딕셔너리).
///
/// **실접속 검증됨 (2026-08-04)** — Oracle 19.3 인스턴스의 PRISMONE 스키마에서
/// 테이블 517개·관계 293개를 읽고 ErdLayout 까지 통과하는 것을 확인했다.
/// 자동 테스트는 아직 없다(서버가 필요) — OracleProviderTests 는 서버 없이 도는
/// 항목만 본다.
///
/// 스키마 이름은 문자열 연결이 아니라 번호 붙은 바인드 변수로 넘긴다.
/// </summary>
public sealed class OracleErdCatalog(ConnectionProfile profile) : IErdCatalog
{
    /// <summary>기본 제공 계정 — 스키마 목록에서 뺀다.</summary>
    private static readonly string[] SystemSchemas =
    [
        "SYS", "SYSTEM", "XDB", "OUTLN", "DBSNMP", "APPQOSSYS", "AUDSYS", "CTXSYS",
        "GSMADMIN_INTERNAL", "LBACSYS", "OJVMSYS", "OLAPSYS", "ORDDATA", "ORDSYS",
        "WMSYS", "MDSYS", "DVSYS", "DVF", "REMOTE_SCHEDULER_AGENT", "SYSBACKUP",
        "SYSDG", "SYSKM", "SYSRAC", "GGSYS", "ANONYMOUS", "PUBLIC",
    ];

    /// <summary>
    /// SSH 를 쓰는 프로필이면 터널을 통과한 주소로 연다 — 이 카탈로그는 provider 를 거치지
    /// 않고 직접 접속 문자열을 만들기 때문에 여기서 한 번 더 풀어 줘야 한다.
    /// </summary>
    private async Task<OracleConnection> OpenAsync(CancellationToken ct)
    {
        var effective = await PrismOne.Db.Core.Ssh.SshTunnelPool.ResolveAsync(profile, ct);
        var conn = new OracleConnection(new OracleProvider().BuildConnectionString(effective));
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>테이블이나 뷰를 실제로 가진 owner 만.</summary>
    public async Task<List<string>> GetSchemasAsync(CancellationToken ct = default)
    {
        var sql = $"""
            SELECT DISTINCT owner
              FROM all_objects
             WHERE object_type IN ('TABLE', 'VIEW')
               AND owner NOT IN ({Placeholders(SystemSchemas.Length, "x")})
             ORDER BY owner
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        Bind(cmd, "x", SystemSchemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// 관계를 빼면 1:1 판정용 상관 서브쿼리가 사라져 훨씬 빠르다.
    /// FK 컬럼 표시는 all_cons_columns 단순 조인이라 그대로 둔다.
    /// </summary>
    public async Task<ErdGraph> LoadTablesAsync(
        IReadOnlyList<string> schemas, CancellationToken ct = default)
    {
        if (schemas.Count == 0) return ErdGraph.Empty;
        var owners = schemas.ToArray();

        await using var conn = await OpenAsync(ct);
        var pkColumns = await LoadKeyColumnsAsync(conn, owners, "P", ct);
        var fkColumns = await LoadKeyColumnsAsync(conn, owners, "R", ct);
        var tables = await LoadTablesAsync(conn, owners, pkColumns, fkColumns, ct);
        return new ErdGraph(tables, []);
    }

    public async Task<ErdGraph> LoadAsync(IReadOnlyList<string> schemas, CancellationToken ct = default)
    {
        if (schemas.Count == 0) return ErdGraph.Empty;
        var owners = schemas.ToArray();

        await using var conn = await OpenAsync(ct);
        var pkColumns = await LoadKeyColumnsAsync(conn, owners, "P", ct);
        var fkColumns = await LoadKeyColumnsAsync(conn, owners, "R", ct);
        var tables = await LoadTablesAsync(conn, owners, pkColumns, fkColumns, ct);
        var relations = await LoadRelationsAsync(conn, owners, ct);

        // 고른 스키마 밖을 가리키는 FK 는 그릴 상대가 없으므로 버린다.
        var known = tables.Select(t => t.Key).ToHashSet();
        relations = relations.Where(r => known.Contains(r.ChildKey) && known.Contains(r.ParentKey)).ToList();
        return new ErdGraph(tables, relations);
    }

    /// <summary>PL/Edit 4단계 — Object Browser 에 표시할 프로시저/함수/패키지 목록.
    /// 트리거·타입은 소스 재구성 방식이 달라(ALL_SOURCE 밖) 뺐다.</summary>
    public async Task<List<OracleRoutine>> GetRoutinesAsync(
        IReadOnlyList<string> schemas, CancellationToken ct = default)
    {
        if (schemas.Count == 0) return [];
        var owners = schemas.ToArray();
        var sql = $"""
            SELECT owner, object_name, object_type
              FROM all_objects
             WHERE object_type IN ('PROCEDURE', 'FUNCTION', 'PACKAGE', 'PACKAGE BODY')
               AND owner IN ({Placeholders(owners.Length, "o")})
             ORDER BY owner, object_name, object_type
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        Bind(cmd, "o", owners);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<OracleRoutine>();
        while (await reader.ReadAsync(ct))
            result.Add(new OracleRoutine(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    /// <summary>ALL_SOURCE 에서 오브젝트 소스를 읽어 CREATE OR REPLACE 문으로 되돌린다 —
    /// Oracle 은 컴파일된 소스에 CREATE 헤더를 저장하지 않고 "PROCEDURE p IS…" 부터 담는다.
    /// 오브젝트가 없으면 빈 문자열.</summary>
    public async Task<string> GetSourceAsync(
        string owner, string objectName, string objectType, CancellationToken ct = default)
    {
        const string sql = """
            SELECT text FROM all_source
             WHERE owner = :owner AND name = :name AND type = :type
             ORDER BY line
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        // 따옴표 식별자가 아닌 이상 Oracle 오브젝트명은 항상 대문자로 저장된다 —
        // 그대로 바인딩하면(예: 소문자 호출) 조용히 0행이 되어 빈 소스로 보인다.
        cmd.Parameters.Add("owner", owner.ToUpperInvariant());
        cmd.Parameters.Add("name", objectName.ToUpperInvariant());
        cmd.Parameters.Add("type", objectType.ToUpperInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var body = new StringBuilder();
        while (await reader.ReadAsync(ct))
            body.Append(reader.GetString(0));
        return body.Length == 0 ? "" : $"CREATE OR REPLACE {body}";
    }

    // ---------- 조회 ----------

    /// <summary>(OWNER.TABLE → 컬럼 집합). 'P' 면 PK, 'R' 이면 FK 참여 컬럼.</summary>
    private static async Task<Dictionary<string, HashSet<string>>> LoadKeyColumnsAsync(
        OracleConnection conn, string[] owners, string constraintType, CancellationToken ct)
    {
        var sql = $"""
            SELECT cc.owner || '.' || cc.table_name, cc.column_name
              FROM all_constraints c
              JOIN all_cons_columns cc
                ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
             WHERE c.constraint_type = :ctype
               AND c.owner IN ({Placeholders(owners.Length, "o")})
            """;
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add("ctype", constraintType);
        Bind(cmd, "o", owners);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            if (!result.TryGetValue(key, out var set))
                result[key] = set = [];
            set.Add(reader.GetString(1));
        }
        return result;
    }

    private static async Task<List<ErdTable>> LoadTablesAsync(
        OracleConnection conn,
        string[] owners,
        Dictionary<string, HashSet<string>> pk,
        Dictionary<string, HashSet<string>> fk,
        CancellationToken ct)
    {
        // all_tab_columns 는 테이블·뷰를 다 담으므로 all_views 로 뷰 여부를 가른다.
        var sql = $"""
            SELECT c.owner,
                   c.table_name,
                   CASE WHEN v.view_name IS NULL THEN 0 ELSE 1 END AS is_view,
                   c.column_name,
                   c.data_type,
                   c.char_length,
                   c.data_precision,
                   c.data_scale,
                   c.nullable
              FROM all_tab_columns c
              LEFT JOIN all_views v
                     ON v.owner = c.owner AND v.view_name = c.table_name
             WHERE c.owner IN ({Placeholders(owners.Length, "o")})
             ORDER BY c.owner, c.table_name, c.column_id
            """;
        var tables = new List<ErdTable>();
        var columns = new List<ErdColumn>();
        string? currentOwner = null, currentName = null;
        var currentIsView = false;

        await using var cmd = Command(conn, sql);
        Bind(cmd, "o", owners);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var owner = reader.GetString(0);
            var name = reader.GetString(1);
            if (owner != currentOwner || name != currentName)
            {
                Flush();
                (currentOwner, currentName, currentIsView) = (owner, name, reader.GetInt32(2) == 1);
            }

            var key = $"{owner}.{name}";
            var column = reader.GetString(3);
            columns.Add(new ErdColumn(
                column,
                FormatType(
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7)),
                NotNull: reader.GetString(8) == "N",
                IsPk: pk.TryGetValue(key, out var pkCols) && pkCols.Contains(column),
                IsFk: fk.TryGetValue(key, out var fkCols) && fkCols.Contains(column)));
        }
        Flush();
        return tables;

        void Flush()
        {
            if (currentOwner is null || currentName is null) return;
            tables.Add(new ErdTable(currentOwner, currentName, currentIsView, columns.ToList()));
            columns.Clear();
        }
    }

    /// <summary>
    /// FK. Oracle 은 r_constraint_name 으로 부모의 PK/UNIQUE 제약을 가리키므로
    /// 부모 컬럼은 그 제약의 컬럼을 position 순으로 맞춘다.
    /// child_unique 는 자식 쪽에 "FK 컬럼 집합과 정확히 같은" PK/UNIQUE 가 있는지다.
    /// </summary>
    private static async Task<List<ErdRelation>> LoadRelationsAsync(
        OracleConnection conn, string[] owners, CancellationToken ct)
    {
        // 1:1 판정(child_unique)은 예전에 상관 서브쿼리로 SQL 안에서 했는데
        // FK 행마다 all_cons_columns 를 세 번씩 훑어 **517테이블 기준 31.8초**가 걸렸다.
        // PK/UNIQUE 컬럼 집합을 단순 쿼리로 한 번 읽어 C# 에서 비교한다
        // (SqliteErdCatalog 와 같은 방식).
        var uniqueSets = await LoadUniqueColumnSetsAsync(conn, owners, ct);

        var sql = $"""
            SELECT c.constraint_name,
                   c.owner || '.' || c.table_name,
                   rc.owner || '.' || rc.table_name,
                   cc.column_name,
                   rcc.column_name,
                   col.nullable
              FROM all_constraints c
              JOIN all_constraints rc
                ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
              JOIN all_cons_columns cc
                ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
              JOIN all_cons_columns rcc
                ON rcc.owner = rc.owner AND rcc.constraint_name = rc.constraint_name
               AND rcc.position = cc.position
              JOIN all_tab_columns col
                ON col.owner = c.owner AND col.table_name = c.table_name
               AND col.column_name = cc.column_name
             WHERE c.constraint_type = 'R'
               AND c.owner IN ({Placeholders(owners.Length, "o")})
             ORDER BY c.owner, c.table_name, c.constraint_name, cc.position
            """;

        var parts = new List<(string Name, string Child, string Parent,
            string ChildColumn, string ParentColumn, bool Nullable)>();
        await using var cmd = Command(conn, sql);
        Bind(cmd, "o", owners);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            parts.Add((
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.GetString(5) == "Y"));

        return parts
            .GroupBy(p => (p.Name, p.Child))
            .Select(g =>
            {
                var childColumns = g.Select(p => p.ChildColumn).ToList();
                var set = childColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
                // 자식 FK 컬럼 집합과 정확히 같은 PK/UNIQUE 가 있으면 1:1
                var unique = uniqueSets.TryGetValue(g.Key.Child, out var sets)
                             && sets.Any(s => s.SetEquals(set));
                return new ErdRelation(
                    g.Key.Name,
                    g.Key.Child,
                    childColumns,
                    g.First().Parent,
                    g.Select(p => p.ParentColumn).ToList(),
                    ChildUnique: unique,
                    ChildOptional: g.Any(p => p.Nullable));
            })
            .ToList();
    }

    /// <summary>(OWNER.TABLE → PK/UNIQUE 제약별 컬럼 집합들). 1:1 판정에 쓴다.</summary>
    private static async Task<Dictionary<string, List<HashSet<string>>>> LoadUniqueColumnSetsAsync(
        OracleConnection conn, string[] owners, CancellationToken ct)
    {
        var sql = $"""
            SELECT cc.owner || '.' || cc.table_name, cc.constraint_name, cc.column_name
              FROM all_constraints c
              JOIN all_cons_columns cc
                ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
             WHERE c.constraint_type IN ('P', 'U')
               AND c.owner IN ({Placeholders(owners.Length, "o")})
            """;
        var byConstraint = new Dictionary<(string Table, string Name), HashSet<string>>();
        await using var cmd = Command(conn, sql);
        Bind(cmd, "o", owners);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!byConstraint.TryGetValue(key, out var set))
                byConstraint[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(reader.GetString(2));
        }

        return byConstraint
            .GroupBy(p => p.Key.Table)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToList(), StringComparer.Ordinal);
    }

    // ---------- 도우미 ----------

    /// <summary>PG 의 format_type 에 해당하는 표기 — VARCHAR2(64), NUMBER(10,2) 등.</summary>
    public static string FormatType(string type, int? charLength, int? precision, int? scale) => type switch
    {
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" when charLength is > 0
            => $"{type}({charLength})",
        "NUMBER" when precision is > 0 && scale is > 0 => $"NUMBER({precision},{scale})",
        "NUMBER" when precision is > 0 => $"NUMBER({precision})",
        _ => type,
    };

    /// <summary>IN 절용 바인드 자리표 — :p0, :p1, ... (값을 문자열로 잇지 않는다).</summary>
    public static string Placeholders(int count, string prefix)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) text.Append(", ");
            text.Append(':').Append(prefix).Append(i);
        }
        return text.ToString();
    }

    private static OracleCommand Command(OracleConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        // 이름으로 바인딩해야 :o0 같은 자리표가 순서와 무관하게 맞는다
        cmd.BindByName = true;
        return cmd;
    }

    private static void Bind(OracleCommand cmd, string prefix, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
            cmd.Parameters.Add($"{prefix}{i}", values[i]);
    }
}
