using Microsoft.Data.Sqlite;

namespace PrismOne.Db.Core.Providers;

/// <summary>
/// SQLite ERD 카탈로그. 읽기 전용.
///
/// SQLite 는 information_schema 가 없어 <c>sqlite_master</c> + PRAGMA 로 읽는다.
/// PRAGMA 는 파라미터 바인딩을 받지 않으므로 식별자는 반드시 인용해서 넣는다.
/// </summary>
public sealed class SqliteErdCatalog(ConnectionProfile profile) : IErdCatalog
{
    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    /// <summary>SQLite 에는 스키마가 없다 — main 하나로 고정.</summary>
    public Task<List<string>> GetSchemasAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<string> { SqliteProvider.MainSchema });

    public async Task<ErdGraph> LoadAsync(IReadOnlyList<string> schemas, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(new SqliteProvider().BuildConnectionString(profile));
        await conn.OpenAsync(ct);

        var names = await LoadTableNamesAsync(conn, ct);
        var tables = new List<ErdTable>();
        var relations = new List<ErdRelation>();

        foreach (var (name, isView) in names)
        {
            var columns = await LoadColumnsAsync(conn, name, ct);
            var uniqueSets = isView ? [] : await LoadUniqueColumnSetsAsync(conn, name, columns, ct);
            var foreignKeys = isView ? [] : await LoadForeignKeysAsync(conn, name, ct);

            var fkColumns = foreignKeys
                .SelectMany(f => f.ChildColumns)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            tables.Add(new ErdTable(
                SqliteProvider.MainSchema,
                name,
                isView,
                columns.Select(c => c with { IsFk = fkColumns.Contains(c.Name) }).ToList()));

            foreach (var fk in foreignKeys)
                relations.Add(Build(fk, name, columns, uniqueSets));
        }

        // 대상 테이블이 없는 FK 는 그릴 상대가 없으므로 버린다.
        var known = tables.Select(t => t.Key).ToHashSet();
        relations = relations.Where(r => known.Contains(r.ParentKey)).ToList();
        return new ErdGraph(tables, relations);
    }

    /// <summary>내부 표(sqlite_ 로 시작)는 제외.</summary>
    private static async Task<List<(string Name, bool IsView)>> LoadTableNamesAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT name, type = 'view'
              FROM sqlite_master
             WHERE type IN ('table', 'view')
               AND name NOT LIKE 'sqlite_%'
             ORDER BY name
            """;
        var result = new List<(string, bool)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetString(0), reader.GetBoolean(1)));
        return result;
    }

    /// <summary>PRAGMA table_info: cid, name, type, notnull, dflt_value, pk.</summary>
    private static async Task<List<ErdColumn>> LoadColumnsAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        var result = new List<ErdColumn>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ErdColumn(
                reader.GetString(1),
                // SQLite 는 타입 없이 선언할 수 있다 — 비면 표기를 채워준다
                reader.IsDBNull(2) || reader.GetString(2).Length == 0 ? "(none)" : reader.GetString(2),
                NotNull: reader.GetInt64(3) != 0,
                IsPk: reader.GetInt64(5) != 0,
                IsFk: false));
        return result;
    }

    /// <summary>PRAGMA foreign_key_list: id, seq, table, from, to, on_update, on_delete, match.</summary>
    private static async Task<List<SqliteForeignKey>> LoadForeignKeysAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        var parts = new List<(long Id, long Seq, string Parent, string From, string? To)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA foreign_key_list({Quote(table)})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                parts.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        var result = new List<SqliteForeignKey>();
        foreach (var group in parts.GroupBy(p => p.Id).OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(p => p.Seq).ToList();
            var parent = ordered[0].Parent;
            var childColumns = ordered.Select(p => p.From).ToList();
            // to 가 NULL 이면 "부모의 PK 를 참조"라는 뜻 — 부모 PK 를 읽어 채운다.
            var parentColumns = ordered.All(p => p.To is not null)
                ? ordered.Select(p => p.To!).ToList()
                : await LoadPrimaryKeyAsync(conn, parent, ct);
            result.Add(new SqliteForeignKey($"fk_{table}_{group.Key}", parent, childColumns, parentColumns));
        }
        return result;
    }

    private static async Task<List<string>> LoadPrimaryKeyAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        var columns = await LoadColumnsAsync(conn, table, ct);
        return columns.Where(c => c.IsPk).Select(c => c.Name).ToList();
    }

    /// <summary>PK + UNIQUE 인덱스의 컬럼 집합들 — 1:1 판정에 쓴다.</summary>
    private static async Task<List<HashSet<string>>> LoadUniqueColumnSetsAsync(
        SqliteConnection conn, string table, List<ErdColumn> columns, CancellationToken ct)
    {
        var sets = new List<HashSet<string>>();
        var pk = columns.Where(c => c.IsPk).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pk.Count > 0) sets.Add(pk);

        var uniqueIndexes = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list({Quote(table)})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // seq, name, unique, origin, partial
                if (reader.GetInt64(2) != 0)
                    uniqueIndexes.Add(reader.GetString(1));
            }
        }

        foreach (var index in uniqueIndexes)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA index_info({Quote(index)})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // seqno, cid, name — 표현식 인덱스는 name 이 NULL 이다
                if (!reader.IsDBNull(2)) set.Add(reader.GetString(2));
            }
            if (set.Count > 0) sets.Add(set);
        }
        return sets;
    }

    private static ErdRelation Build(
        SqliteForeignKey fk,
        string childTable,
        List<ErdColumn> childColumns,
        List<HashSet<string>> uniqueSets)
    {
        var child = fk.ChildColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 자식 FK 컬럼 집합이 자식 쪽 PK/UNIQUE 와 정확히 같으면 1:1
        var unique = uniqueSets.Any(s => s.SetEquals(child));
        // 하나라도 nullable 이면 0..N
        var optional = fk.ChildColumns.Any(name =>
            childColumns.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { NotNull: false });

        return new ErdRelation(
            fk.Name,
            $"{SqliteProvider.MainSchema}.{childTable}",
            fk.ChildColumns,
            $"{SqliteProvider.MainSchema}.{fk.ParentTable}",
            fk.ParentColumns,
            unique,
            optional);
    }

    private sealed record SqliteForeignKey(
        string Name,
        string ParentTable,
        List<string> ChildColumns,
        List<string> ParentColumns);
}
