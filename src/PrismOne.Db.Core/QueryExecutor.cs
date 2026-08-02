using System.Diagnostics;
using Npgsql;

namespace PrismOne.Db.Core;

/// <summary>하나의 결과 그리드에 해당하는 결과셋 (SELECT면 행 목록, DML/DDL이면 영향 행 수).</summary>
public sealed class QueryResultSet
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required List<string?[]> Rows { get; init; }
    /// <summary>결과셋 없는 문장(INSERT/UPDATE/DDL)의 영향 행 수. 결과셋이 있으면 -1.</summary>
    public int RowsAffected { get; init; } = -1;
    /// <summary>maxRows 초과로 잘렸는지.</summary>
    public bool Truncated { get; init; }

    public bool HasGrid => Columns.Count > 0;
}

public sealed class QueryOutcome
{
    public required List<QueryResultSet> ResultSets { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// SQL 텍스트(여러 문장 가능)를 통째로 실행해 결과셋 목록을 돌려준다.
/// 스크립트/CLI 용 — 점진 fetch 가 필요한 대화형 경로는 QuerySession/ActiveQuery 를 쓴다.
/// </summary>
public static class QueryExecutor
{
    public static async Task<QueryOutcome> ExecuteAsync(
        NpgsqlConnection conn, string sql, int maxRows, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sets = new List<QueryResultSet>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            if (reader.FieldCount == 0)
                continue;   // 결과셋 없는 문장 — 영향 행 수는 마지막에 합산 보고

            var columns = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                columns[i] = reader.GetName(i);

            var rows = new List<string?[]>();
            var truncated = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= maxRows) { truncated = true; break; }
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = ValueFormatter.Format(reader.GetValue(i));
                rows.Add(row);
            }
            sets.Add(new QueryResultSet { Columns = columns, Rows = rows, Truncated = truncated });

            if (truncated)
                break;      // 남은 행/결과셋은 버린다 (Close 가 정리)
        } while (await reader.NextResultAsync(ct));

        var affected = reader.RecordsAffected;   // DML 누적 (SELECT 만 있으면 -1)
        await reader.CloseAsync();
        sw.Stop();

        if (sets.Count == 0)
        {
            sets.Add(new QueryResultSet
            {
                Columns = [],
                Rows = [],
                RowsAffected = affected < 0 ? 0 : affected,
            });
        }

        return new QueryOutcome { ResultSets = sets, Elapsed = sw.Elapsed };
    }
}
