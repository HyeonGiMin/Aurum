using System.Text.Json.Nodes;

namespace PrismOne.Db.Core.Mcp;

/// <summary>
/// 결과 그리드를 AI 에게 넘길 JSON 으로 만든다. NULL 과 빈 문자열을 구분하려고
/// 표 텍스트가 아닌 JSON 을 쓴다. 긴 셀(CLOB·JSON 컬럼)은 잘라 토큰을 아낀다.
/// </summary>
public static class McpResultText
{
    /// <summary>셀 하나의 최대 길이 — 넘으면 자르고 남은 글자 수를 붙인다.</summary>
    public const int MaxCellLength = 500;

    public static string Page(
        IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows, int offset, int limit)
    {
        var start = Math.Clamp(offset, 0, rows.Count);
        var count = Math.Clamp(limit, 0, rows.Count - start);

        var page = new JsonArray();
        for (var i = start; i < start + count; i++)
        {
            var cells = new JsonArray();
            foreach (var cell in rows[i])
                cells.Add(cell is null ? null : JsonValue.Create(Clip(cell)));
            page.Add(cells);
        }

        return new JsonObject
        {
            ["columns"] = new JsonArray(columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["rows"] = page,
            ["offset"] = start,
            ["returned"] = count,
            ["totalLoaded"] = rows.Count,
            ["more"] = start + count < rows.Count,
        }.ToJsonString(McpJson.Options);
    }

    private static string Clip(string cell) =>
        cell.Length <= MaxCellLength
            ? cell
            : $"{cell[..MaxCellLength]}…(+{cell.Length - MaxCellLength} chars)";
}
