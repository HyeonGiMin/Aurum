using Npgsql;

namespace PrismOne.Db.Core;

/// <summary>
/// ERD 그래프를 읽어오는 provider 경계. PG 전용 코드가 더 굳기 전에 카탈로그만 먼저
/// 추상화해 둔다 — Oracle 드라이버가 붙으면 <c>OracleErdCatalog</c> 만 추가하면 되고
/// 레이아웃·렌더는 손대지 않는다 (STATUS.md §1.5 의 provider 층 방향).
/// </summary>
public interface IErdCatalog
{
    /// <summary>사용자에게 고를 수 있게 보여줄 스키마(Oracle 은 owner) 목록.</summary>
    Task<List<string>> GetSchemasAsync(CancellationToken ct = default);

    /// <summary>주어진 스키마들의 테이블·컬럼·FK 를 한 번에 읽는다.</summary>
    Task<ErdGraph> LoadAsync(IReadOnlyList<string> schemas, CancellationToken ct = default);
}

/// <summary>PostgreSQL 카탈로그(pg_class/pg_constraint) 기반 구현. 읽기 전용.</summary>
public sealed class PgErdCatalog(ConnectionProfile profile) : IErdCatalog
{
    private const string SystemSchemaFilter = "n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')";

    public async Task<List<string>> GetSchemasAsync(CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT DISTINCT n.nspname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind IN ('r', 'p', 'v', 'm')
               AND {SystemSchemaFilter}
             ORDER BY 1
            """;
        await using var conn = await profile.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<ErdGraph> LoadAsync(IReadOnlyList<string> schemas, CancellationToken ct = default)
    {
        if (schemas.Count == 0) return ErdGraph.Empty;
        var names = schemas.ToArray();

        await using var conn = await profile.OpenAsync(ct);
        var pk = await LoadKeyColumnsAsync(conn, names, 'p', ct);
        var fk = await LoadKeyColumnsAsync(conn, names, 'f', ct);
        var tables = await LoadTablesAsync(conn, names, pk, fk, ct);
        var relations = await LoadRelationsAsync(conn, names, ct);

        // 고른 스키마 밖을 가리키는 FK 는 그릴 상대 박스가 없으므로 버린다 (MVP).
        var known = tables.Select(t => t.Key).ToHashSet();
        relations = relations.Where(r => known.Contains(r.ChildKey) && known.Contains(r.ParentKey)).ToList();
        return new ErdGraph(tables, relations);
    }

    /// <summary>(schema.table → 컬럼명 집합). contype 'p' 면 PK, 'f' 면 FK 참여 컬럼.</summary>
    private static async Task<Dictionary<string, HashSet<string>>> LoadKeyColumnsAsync(
        NpgsqlConnection conn, string[] schemas, char contype, CancellationToken ct)
    {
        const string sql = $"""
            SELECT n.nspname || '.' || c.relname, a.attname
              FROM pg_constraint k
              JOIN pg_class c ON c.oid = k.conrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN unnest(k.conkey) AS ck(attnum) ON true
              JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ck.attnum
             WHERE k.contype = @contype
               AND n.nspname::text = ANY(@schemas)
               AND {SystemSchemaFilter}
            """;
        var result = new Dictionary<string, HashSet<string>>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("contype", contype.ToString());
        cmd.Parameters.AddWithValue("schemas", schemas);
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
        NpgsqlConnection conn,
        string[] schemas,
        Dictionary<string, HashSet<string>> pk,
        Dictionary<string, HashSet<string>> fk,
        CancellationToken ct)
    {
        const string sql = $"""
            SELECT n.nspname,
                   c.relname,
                   c.relkind IN ('v', 'm'),
                   a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   a.attnotnull
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
             WHERE c.relkind IN ('r', 'p', 'v', 'm')
               AND n.nspname::text = ANY(@schemas)
               AND {SystemSchemaFilter}
             ORDER BY n.nspname, c.relname, a.attnum
            """;
        var tables = new List<ErdTable>();
        var columns = new List<ErdColumn>();
        string? currentSchema = null, currentName = null;
        var currentIsView = false;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("schemas", schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            if (schema != currentSchema || name != currentName)
            {
                Flush();
                (currentSchema, currentName, currentIsView) = (schema, name, reader.GetBoolean(2));
            }

            var key = $"{schema}.{name}";
            var column = reader.GetString(3);
            columns.Add(new ErdColumn(
                column,
                reader.GetString(4),
                reader.GetBoolean(5),
                pk.TryGetValue(key, out var pkCols) && pkCols.Contains(column),
                fk.TryGetValue(key, out var fkCols) && fkCols.Contains(column)));
        }
        Flush();
        return tables;

        void Flush()
        {
            if (currentSchema is null || currentName is null) return;
            tables.Add(new ErdTable(currentSchema, currentName, currentIsView, columns.ToList()));
            columns.Clear();
        }
    }

    private static async Task<List<ErdRelation>> LoadRelationsAsync(
        NpgsqlConnection conn, string[] schemas, CancellationToken ct)
    {
        // conkey/confkey 는 컬럼 순서가 의미를 가지므로 WITH ORDINALITY 로 순서를 지켜 이름을 뽑는다.
        // ChildUnique  : 자식 FK 컬럼 집합과 완전히 같은 PK/UNIQUE 가 있으면 1:1
        // ChildOptional: 자식 컬럼 중 nullable 이 하나라도 있으면 0..N
        const string sql = """
            SELECT k.conname,
                   cn.nspname || '.' || cc.relname,
                   pn.nspname || '.' || pc.relname,
                   (SELECT array_agg(a.attname ORDER BY x.ord)
                      FROM unnest(k.conkey) WITH ORDINALITY AS x(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = x.attnum),
                   (SELECT array_agg(a.attname ORDER BY x.ord)
                      FROM unnest(k.confkey) WITH ORDINALITY AS x(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = k.confrelid AND a.attnum = x.attnum),
                   EXISTS (SELECT 1 FROM pg_constraint u
                            WHERE u.conrelid = k.conrelid
                              AND u.contype IN ('p', 'u')
                              AND u.conkey @> k.conkey AND u.conkey <@ k.conkey),
                   NOT COALESCE((SELECT bool_and(a.attnotnull)
                                   FROM unnest(k.conkey) AS x(attnum)
                                   JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = x.attnum), false)
              FROM pg_constraint k
              JOIN pg_class cc ON cc.oid = k.conrelid
              JOIN pg_namespace cn ON cn.oid = cc.relnamespace
              JOIN pg_class pc ON pc.oid = k.confrelid
              JOIN pg_namespace pn ON pn.oid = pc.relnamespace
             WHERE k.contype = 'f'
               AND cn.nspname::text = ANY(@schemas)
             ORDER BY 2, 1
            """;
        var result = new List<ErdRelation>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("schemas", schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ErdRelation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<string[]>(3),
                reader.GetString(2),
                reader.GetFieldValue<string[]>(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6)));
        return result;
    }
}
