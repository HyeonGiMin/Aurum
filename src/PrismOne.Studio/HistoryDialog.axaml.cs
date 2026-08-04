using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>목록 한 줄 (XAML 컴파일 바인딩 대상이라 public).</summary>
public sealed record HistoryRow(HistoryEntry Entry)
{
    public string When => Entry.At == default ? "" : Entry.At.ToString("yyyy-MM-dd HH:mm:ss");
    public string Preview
    {
        get
        {
            var line = Entry.Sql.ReplaceLineEndings(" ").Trim();
            return line.Length > 120 ? line[..120] + "…" : line;
        }
    }
}

/// <summary>
/// Query &gt; History — 실행했던 문장을 검색해 다시 쓴다.
/// Ctrl+↑↓ 순환(Golden)은 최근 몇 개를 되짚을 때고, 이 창은 "지난주에 돌린
/// 그 쿼리"를 찾을 때다. 더블클릭 또는 Insert 로 에디터에 넣는다 (실행은 사용자가).
/// </summary>
public partial class HistoryDialog : Window
{
    private readonly List<HistoryRow> _all;

    /// <summary>선택된 SQL — 닫힌 뒤 호출부(에디터 삽입)가 읽는다.</summary>
    public string? SelectedSql { get; private set; }

    public HistoryDialog() : this(HistoryStore.Entries) { }

    /// <summary>목록 주입 — 스크린샷 하니스가 실제 히스토리 파일을 건드리지 않게.</summary>
    internal HistoryDialog(IReadOnlyList<HistoryEntry> entries)
    {
        InitializeComponent();
        MinWidth = 480;
        MinHeight = 380;
        // 최근 것이 위로
        _all = entries.Reverse().Select(e => new HistoryRow(e)).ToList();
        Rebuild();
        FilterBox.AttachedToVisualTree += (_, _) => FilterBox.Focus();
    }

    private void Rebuild()
    {
        var filter = FilterBox.Text?.Trim() ?? "";
        var rows = filter.Length == 0
            ? _all
            : _all.Where(r => r.Entry.Sql.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        HistoryList.ItemsSource = rows;
        StatusText.Text = filter.Length == 0
            ? $"{_all.Count:N0}개 (최근 500개 보존)"
            : $"{rows.Count:N0}/{_all.Count:N0}개";
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => Rebuild();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var row = HistoryList.SelectedItem as HistoryRow;
        SqlBox.Text = row?.Entry.Sql ?? "";
        InsertButton.IsEnabled = row is not null;
    }

    private void OnInsert(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        SelectedSql = row.Entry.Sql;
        Close();
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
