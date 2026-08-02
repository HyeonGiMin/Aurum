using System.Globalization;
using System.Text;

namespace PrismOne.Db.Core;

public enum GridExportFormat
{
    Csv,
    Tsv,
    Insert,
}

/// <summary>
/// 그리드에 로드된 행을 CSV/TSV/INSERT 문으로 변환한다 (Golden 의 Save Grid As).
/// 전체 행·전문이 필요하면 CopyExporter(COPY TO STDOUT) 를 쓴다.
/// </summary>
public static class GridExporter
{
    public static string Build(
        GridExportFormat format,
        IReadOnlyList<string> columns,
        IReadOnlyList<string?[]> rows,
        string tableName = "table_name",
        bool blankLineBetweenStatements = false)
        => format switch
        {
            GridExportFormat.Csv => Delimited(columns, rows, ','),
            GridExportFormat.Tsv => Delimited(columns, rows, '\t'),
            GridExportFormat.Insert => Inserts(columns, rows, tableName, blankLineBetweenStatements),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    private static string Delimited(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows, char sep)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(sep, columns.Select(c => Field(c, sep))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(sep, row.Select(v => Field(v ?? "", sep))));
        return sb.ToString();
    }

    private static string Field(string value, char sep)
    {
        if (sep == '\t')
            return value.Replace("\t", " ").Replace("\r", "").Replace("\n", " ");
        return value.Contains(sep) || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }

    private static string Inserts(
        IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows, string tableName, bool blankLine)
    {
        var sb = new StringBuilder();
        var columnList = string.Join(", ", columns.Select(Quote));
        foreach (var row in rows)
        {
            sb.Append("INSERT INTO ").Append(tableName).Append(" (").Append(columnList).Append(") VALUES (");
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Literal(row[i]));
            }
            sb.AppendLine(");");
            if (blankLine) sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>NULL 은 NULL, 숫자·boolean 은 그대로, 나머지는 작은따옴표 이스케이프.</summary>
    private static string Literal(string? value)
    {
        if (value is null) return "NULL";
        if (value.Length > 0 &&
            (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
             value is "true" or "false"))
            return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string Quote(string ident) =>
        ident.Length > 0 && !char.IsDigit(ident[0]) &&
        ident.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? ident
            : '"' + ident.Replace("\"", "\"\"") + '"';
}
