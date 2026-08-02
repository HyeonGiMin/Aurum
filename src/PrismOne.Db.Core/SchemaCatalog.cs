using Npgsql;

namespace PrismOne.Db.Core;

public sealed record TableInfo(string Schema, string Name, bool IsView)
{
    /// <summary>쿼리에 안전하게 넣을 수 있는 식별자 (필요할 때만 따옴표).</summary>
    public string QualifiedName => $"{Quote(Schema)}.{Quote(Name)}";

    /// <summary>스키마 없이 이름만 (Use Schema 해제 시 붙여넣기용).</summary>
    public string QuotedName => Quote(Name);

    private static string Quote(string ident) =>
        ident.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') && ident.Length > 0 && !char.IsDigit(ident[0])
            ? ident
            : '"' + ident.Replace("\"", "\"\"") + '"';
}

/// <summary>Describe 패널 한 행 (Golden 의 # / Name / Type / Null? / PK / FK).</summary>
public sealed record ColumnInfo(int No, string Name, string Type, string Nullable, string Pk, string Fk);

/// <summary>스키마 브라우저/Describe 용 카탈로그 조회.</summary>
public static class SchemaCatalog
{
    public static async Task<List<TableInfo>> GetTablesAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        const string sql = """
            SELECT table_schema, table_name, table_type = 'VIEW'
              FROM information_schema.tables
             WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
             ORDER BY table_schema, table_name
            """;
        var result = new List<TableInfo>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new TableInfo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        return result;
    }

    public static async Task<List<ColumnInfo>> GetColumnsAsync(
        NpgsqlConnection conn, TableInfo table, CancellationToken ct = default)
    {
        const string sql = """
            SELECT a.attnum,
                   a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   CASE WHEN a.attnotnull THEN 'no' ELSE 'yes' END,
                   COALESCE('P' || array_position(pc.conkey, a.attnum)::text, ''),
                   COALESCE((SELECT 'F' || array_position(fc.conkey, a.attnum)::text
                               FROM pg_constraint fc
                              WHERE fc.conrelid = a.attrelid
                                AND fc.contype = 'f'
                                AND a.attnum = ANY(fc.conkey)
                              LIMIT 1), '')
              FROM pg_attribute a
              LEFT JOIN pg_constraint pc
                     ON pc.conrelid = a.attrelid AND pc.contype = 'p'
             WHERE a.attrelid = @rel::regclass
               AND a.attnum > 0
               AND NOT a.attisdropped
             ORDER BY a.attnum
            """;
        var result = new List<ColumnInfo>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("rel", table.QualifiedName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ColumnInfo(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        return result;
    }
}
