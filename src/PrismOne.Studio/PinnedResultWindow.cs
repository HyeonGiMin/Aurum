using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;

namespace PrismOne.Studio;

/// <summary>
/// Results &gt; Pin Results to New Window — 현재 그리드의 **스냅샷**을 별도 창에 고정한다
/// (DataGrip 의 결과 pin 대응). 다음 쿼리를 돌려도 이 창은 남아 있어 결과를 나란히
/// 비교할 수 있다. 읽기 전용이고, 창을 닫으면 스냅샷도 사라진다.
/// </summary>
public sealed class PinnedResultWindow : Window
{
    public PinnedResultWindow(string title, IReadOnlyList<string> columns, IReadOnlyList<RowItem> rows)
    {
        var summary = title.ReplaceLineEndings(" ").Trim();
        if (summary.Length > 60) summary = summary[..60] + "…";
        Title = $"📌 {summary} — {rows.Count:N0} rows";
        Width = 860;
        Height = 520;
        ShowInTaskbar = false;

        var grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = true,
            ItemsSource = rows.ToList(),   // 고정 시점의 목록 — 탭의 다음 fetch 와 분리
        };
        var noColumn = new DataGridTextColumn
        {
            Header = "#",
            Binding = new Binding(nameof(RowItem.No)),   // 고정본은 fetch 순서 그대로
            Width = new DataGridLength(46),
            CanUserSort = false,
        };
        noColumn.CellStyleClasses.Add("rownum");
        grid.Columns.Add(noColumn);
        for (var i = 0; i < columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i],
                Binding = new Binding($"{nameof(RowItem.Cells)}[{i}]") { Mode = BindingMode.OneWay },
                CustomSortComparer = new CellComparer(i),
                Width = DataGridLength.Auto,
                MaxWidth = 420,
            });
        }
        Content = grid;
    }
}
