using System.Text;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Db.Core;

/// <summary>
/// CSV/TSV 파서 (RFC 4180 기준). 따옴표 필드, 필드 안 줄바꿈·이스케이프(<c>""</c>)를
/// 처리한다. 우리 TSV 내보내기와 엑셀 CSV 를 둘 다 읽는 것이 목표.
/// </summary>
public static class CsvParser
{
    /// <summary>
    /// 구분자 추정 — 첫 줄(따옴표 밖)에서 탭·콤마·세미콜론 중 가장 많은 것.
    /// 하나도 없으면 콤마.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        int tab = 0, comma = 0, semi = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '\n') break;
            else if (!quoted && c == '\t') tab++;
            else if (!quoted && c == ',') comma++;
            else if (!quoted && c == ';') semi++;
        }
        return tab >= comma && tab >= semi && tab > 0 ? '\t'
             : semi > comma ? ';'
             : ',';
    }

    public static List<string[]> Parse(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = 0;

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRow()
        {
            EndField();
            // 완전히 빈 줄(필드 1개, 내용 없음)은 건너뛴다 — 파일 끝 개행 대응
            if (fields.Count > 1 || fields[0].Length > 0)
                rows.Add(fields.ToArray());
            fields.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    quoted = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
                i++;
            }
            else if (c == delimiter)
            {
                EndField();
                i++;
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                EndRow();
                i += 2;
            }
            else if (c is '\n' or '\r')
            {
                EndRow();
                i++;
            }
            else
            {
                field.Append(c);
                i++;
            }
        }
        if (field.Length > 0 || fields.Count > 0)
            EndRow();
        return rows;
    }
}

/// <summary>파일 컬럼 → 테이블 컬럼 매핑. Unmatched 는 테이블에 없어 무시될 헤더들.</summary>
public sealed record CsvMapping(
    IReadOnlyList<(int FileIndex, ColumnInfo Column)> Columns,
    IReadOnlyList<string> UnmatchedHeaders,
    int FileFieldCount);

public sealed record CsvImportResult(int Inserted, int TotalRows, string? Error, int? ErrorRow)
{
    public bool Success => Error is null;
}

/// <summary>
/// CSV/TSV → 테이블 import (DATAGRIP_GAP §5). **전량 성공 아니면 전량 롤백** —
/// Run and Edit 의 원칙(영향 행이 어긋나면 전체 롤백)과 같다. 절반만 들어간 테이블을
/// 지원 현장에 남기지 않기 위해서다. 값은 전부 문자열로 보내 서버가 컬럼 타입으로
/// 캐스팅하게 한다 (ExecuteEditAsync 경로 재사용).
/// </summary>
public static class CsvImporter
{
    /// <summary>헤더 이름으로 매핑 (대소문자 무시). 중복 헤더는 첫 번째만 쓴다.</summary>
    public static CsvMapping MapByHeader(string[] header, IReadOnlyList<ColumnInfo> tableColumns)
    {
        var mapped = new List<(int, ColumnInfo)>();
        var unmatched = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim();
            var column = tableColumns.FirstOrDefault(c =>
                c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (column is not null && used.Add(column.Name))
                mapped.Add((i, column));
            else
                unmatched.Add(name);
        }
        return new CsvMapping(mapped, unmatched, header.Length);
    }

    /// <summary>헤더 없는 파일 — 파일 컬럼 순서 = 테이블 컬럼 순서로 매핑.</summary>
    public static CsvMapping MapByPosition(int fieldCount, IReadOnlyList<ColumnInfo> tableColumns)
    {
        var mapped = tableColumns.Take(fieldCount).Select((c, i) => (i, c)).ToList();
        return new CsvMapping(mapped, [], fieldCount);
    }

    /// <summary>구조 검사 — 필드 수가 다르면 그 행 번호(1부터, 헤더 제외)와 이유.</summary>
    public static (int Row, string Message)? ValidateRows(CsvMapping mapping, IReadOnlyList<string[]> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Length != mapping.FileFieldCount)
                return (i + 1, $"컬럼 수가 다릅니다 — 기대 {mapping.FileFieldCount}, 실제 {rows[i].Length}");
        }
        return null;
    }

    public static EditStatement BuildInsert(
        IDbProvider provider, string schema, string table,
        CsvMapping mapping, string[] row, bool emptyAsNull)
    {
        var target = string.IsNullOrEmpty(schema)
            ? provider.QuoteIdentifier(table)
            : $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)}";
        var columns = string.Join(", ", mapping.Columns.Select(m => provider.QuoteIdentifier(m.Column.Name)));
        var placeholders = string.Join(", ", mapping.Columns.Select((_, i) => provider.ParameterPlaceholder(i + 1)));
        var values = mapping.Columns
            .Select(m => m.FileIndex < row.Length ? row[m.FileIndex] : null)
            .Select(v => emptyAsNull && v is { Length: 0 } ? null : v)
            .ToList();
        return new EditStatement($"INSERT INTO {target} ({columns}) VALUES ({placeholders})", values);
    }

    /// <summary>
    /// 한 트랜잭션으로 전량 insert. 실패하면 롤백하고 몇 번째 행에서 무엇이 났는지 돌려준다.
    /// </summary>
    public static async Task<CsvImportResult> RunAsync(
        QuerySession session, string schema, string table,
        CsvMapping mapping, IReadOnlyList<string[]> rows, bool emptyAsNull,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (mapping.Columns.Count == 0)
            return new CsvImportResult(0, rows.Count, "매핑된 컬럼이 없습니다", null);
        if (ValidateRows(mapping, rows) is { } bad)
            return new CsvImportResult(0, rows.Count, bad.Message, bad.Row);

        var provider = session.Profile.Provider;
        var inserted = 0;
        try
        {
            await session.EnsureTransactionAsync(ct);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await session.ExecuteEditAsync(BuildInsert(provider, schema, table, mapping, row, emptyAsNull), ct);
                inserted++;
                if (inserted % 100 == 0)
                    progress?.Report(inserted);
            }
            await session.CommitAsync(ct);
            return new CsvImportResult(inserted, rows.Count, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryRollback(session);
            return new CsvImportResult(0, rows.Count, ex.Message, inserted + 1);
        }
        catch (OperationCanceledException)
        {
            await TryRollback(session);
            return new CsvImportResult(0, rows.Count, "취소됨", inserted + 1);
        }
    }

    private static async Task TryRollback(QuerySession session)
    {
        try { await session.RollbackAsync(); } catch { /* 접속이 죽은 경우 — 트랜잭션도 함께 사라진다 */ }
    }
}
