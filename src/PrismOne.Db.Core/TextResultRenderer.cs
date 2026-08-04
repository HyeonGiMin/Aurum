using System.Text;

namespace PrismOne.Db.Core;

/// <summary>
/// Golden 의 "Show Text" — 결과를 고정폭으로 맞춰 텍스트로 뽑는다(SQL*Plus 식).
/// 그리드와 달리 그대로 복사해 메일·이슈에 붙일 수 있는 게 목적이라 정렬이 핵심이다.
/// </summary>
public static class TextResultRenderer
{
    /// <summary>NULL 을 빈칸과 구분해서 보이게.</summary>
    public const string NullText = "(null)";

    /// <summary>한 컬럼이 화면을 다 먹지 않도록 자르는 기본 상한.</summary>
    public const int DefaultMaxColumnWidth = 60;

    public static string Render(
        IReadOnlyList<string> columns,
        IReadOnlyList<string?[]> rows,
        int maxColumnWidth = DefaultMaxColumnWidth)
    {
        if (columns.Count == 0) return "No columns.";

        var limit = Math.Max(3, maxColumnWidth);
        var widths = new int[columns.Count];
        for (var c = 0; c < columns.Count; c++)
            widths[c] = Math.Min(limit, columns[c].Length);

        // 행이 컬럼 수보다 짧으면 그 자리는 NULL 로 본다 — 폭 계산에서도 빠뜨리면 안 된다.
        foreach (var row in rows)
            for (var c = 0; c < columns.Count; c++)
                widths[c] = Math.Min(limit, Math.Max(widths[c], Cell(c < row.Length ? row[c] : null).Length));

        var text = new StringBuilder();
        AppendRow(text, columns.Select((name, i) => Fit(name, widths[i])));
        text.AppendLine(string.Join(' ', widths.Select(w => new string('-', w))));

        foreach (var row in rows)
            AppendRow(text, widths.Select((w, i) => Fit(Cell(i < row.Length ? row[i] : null), w)));

        text.AppendLine();
        text.Append(rows.Count == 0 ? "no rows" : $"{rows.Count:N0} row(s)");
        return text.ToString();
    }

    private static void AppendRow(StringBuilder text, IEnumerable<string> cells) =>
        // 오른쪽 공백은 붙여넣을 때 거슬리므로 잘라낸다.
        text.AppendLine(string.Join(' ', cells).TrimEnd());

    private static string Cell(string? value) => value ?? NullText;

    /// <summary>폭에 맞춰 왼쪽 정렬. 넘치면 마지막 글자를 …로 바꿔 잘렸음을 보인다.</summary>
    private static string Fit(string value, int width)
    {
        if (value.Length == width) return value;
        if (value.Length < width) return value.PadRight(width);
        return width <= 1 ? value[..width] : value[..(width - 1)] + "…";
    }
}
