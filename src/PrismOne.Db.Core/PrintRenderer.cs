using System.Text;

namespace PrismOne.Db.Core;

/// <summary>
/// 인쇄용 HTML 생성 (Golden 의 Print / Print Preview).
///
/// Avalonia 에는 인쇄 API 가 없다. 그래서 인쇄할 내용을 HTML 로 만들어 OS 기본 브라우저에
/// 넘기고, 미리보기·용지 설정·프린터 선택은 브라우저의 인쇄 대화상자에 맡긴다.
/// (auto=true 면 열리자마자 인쇄 대화상자를 띄운다 — Golden 의 Print,
///  false 면 페이지만 띄운다 — Golden 의 Print Preview)
/// </summary>
public static class PrintRenderer
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>SQL 스크립트 인쇄 — 줄바꿈·들여쓰기를 그대로 살린다.</summary>
    public static string RenderSql(string sql, string title, string subtitle, DateTimeOffset stamp, bool auto) =>
        Page(title, subtitle, stamp, auto, $"<pre class=\"sql\">{Escape(sql)}</pre>");

    /// <summary>결과 그리드 인쇄 — 로드된 행만 (Golden 도 화면에 있는 것을 찍는다).</summary>
    public static string RenderGrid(
        IReadOnlyList<string> columns,
        IReadOnlyList<string?[]> rows,
        string title,
        string subtitle,
        DateTimeOffset stamp,
        bool auto)
    {
        var body = new StringBuilder();
        body.Append("<table><thead><tr><th class=\"num\">#</th>");
        foreach (var column in columns)
            body.Append($"<th>{Escape(column)}</th>");
        body.Append("</tr></thead><tbody>");

        for (var r = 0; r < rows.Count; r++)
        {
            body.Append($"<tr><td class=\"num\">{r + 1}</td>");
            for (var c = 0; c < columns.Count; c++)
            {
                var value = c < rows[r].Length ? rows[r][c] : null;
                body.Append(value is null
                    ? "<td class=\"null\">NULL</td>"
                    : $"<td>{Escape(value)}</td>");
            }
            body.Append("</tr>");
        }
        body.Append("</tbody></table>");
        body.Append($"<p class=\"count\">{rows.Count:N0} record(s)</p>");
        return Page(title, subtitle, stamp, auto, body.ToString());
    }

    private static string Page(string title, string subtitle, DateTimeOffset stamp, bool auto, string body) =>
        $$"""
        <!doctype html>
        <html lang="ko">
        <head>
        <meta charset="utf-8">
        <title>{{Escape(title)}}</title>
        <style>
          body { font-family: "Segoe UI", "Malgun Gothic", system-ui, sans-serif; font-size: 11pt; margin: 24px; color: #1a1a1a; }
          header { border-bottom: 1px solid #999; padding-bottom: 8px; margin-bottom: 14px; }
          h1 { font-size: 13pt; margin: 0 0 3px; }
          .meta { font-size: 9pt; color: #555; }
          pre.sql { font-family: Consolas, "D2Coding", monospace; font-size: 10pt; white-space: pre-wrap; word-break: break-word; }
          table { border-collapse: collapse; width: 100%; font-size: 9.5pt; }
          th, td { border: 1px solid #bbb; padding: 3px 6px; text-align: left; vertical-align: top; word-break: break-word; }
          th { background: #eee; }
          td.num, th.num { text-align: right; color: #666; width: 3em; }
          td.null { color: #999; font-style: italic; }
          .count { font-size: 9pt; color: #555; margin-top: 10px; }
          @media print {
            body { margin: 0; }
            thead { display: table-header-group; }
            tr { break-inside: avoid; }
          }
        </style>
        </head>
        <body>
        <header>
          <h1>{{Escape(title)}}</h1>
          <div class="meta">{{Escape(subtitle)}} · {{stamp.ToString(TimestampFormat)}}</div>
        </header>
        {{body}}
        {{(auto ? "<script>window.onload = () => window.print();</script>" : "")}}
        </body>
        </html>
        """;

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
