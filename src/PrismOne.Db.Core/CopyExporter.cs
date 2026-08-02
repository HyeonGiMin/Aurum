using System.Text;

namespace PrismOne.Db.Core;

/// <summary>
/// COPY (query) TO STDOUT 기반 고속 CSV export.
/// 그리드에 로드된(표시용으로 잘린) 행이 아니라 쿼리를 서버에서 다시 실행해
/// 전체 행을 원문 그대로 스트리밍한다 — 행 단위 조립보다 수십 배 빠르다.
/// </summary>
public static class CopyExporter
{
    public static string BuildCopySql(string query) =>
        $"COPY ({query.TrimEnd().TrimEnd(';').TrimEnd()}) TO STDOUT WITH (FORMAT CSV, HEADER)";

    /// <summary>내보낸 문자 수를 돌려준다. progress 는 누적 문자 수로 호출된다.</summary>
    public static async Task<long> ExportCsvAsync(
        ConnectionProfile profile,
        string query,
        Stream output,
        Action<long>? progress = null,
        CancellationToken ct = default)
    {
        await using var conn = await profile.OpenAsync(ct);
        using var reader = conn.BeginTextExport(BuildCopySql(query));
        // BOM 포함 UTF-8 — 엑셀에서 한글이 바로 열리게
        var writer = new StreamWriter(output, new UTF8Encoding(true), leaveOpen: true);
        var buffer = new char[64 * 1024];
        long total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteAsync(buffer, 0, read);
            total += read;
            progress?.Invoke(total);
        }
        await writer.FlushAsync(ct);
        return total;
    }
}
