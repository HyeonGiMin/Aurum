using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>즐겨찾기 목록 한 행 (이름 + SQL 한 줄 미리보기).</summary>
public sealed record FavoriteRow(string Name, string Preview, FavoriteQuery Item);

/// <summary>
/// Golden 의 Favorites 창 — 필터·실행·에디터로 삽입·이름/SQL 수정·삭제.
/// 실행은 창을 닫고 MainWindow 가 맡는다 (SELECT 이외 차단 옵션을 한 곳에서 판정하려고).
/// </summary>
public partial class FavoritesDialog : Window
{
    private const int PreviewLength = 90;
    private const int SuggestedNameLength = 40;

    private readonly FavoritesStore _store;

    /// <summary>편집 중인 기존 항목 이름. null 이면 새 항목.</summary>
    private string? _editingName;

    /// <summary>Run 을 눌렀을 때의 SQL. 누르지 않았으면 null.</summary>
    public string? RunSql { get; private set; }

    /// <summary>Insert into Editor 를 눌렀을 때의 SQL.</summary>
    public string? InsertSql { get; private set; }

    public FavoritesDialog() : this(FavoritesStore.Load(), null) { }

    /// <param name="seedSql">Add 모드로 열 때 채워 넣을 SQL (현재 문장).</param>
    public FavoritesDialog(FavoritesStore store, string? seedSql)
    {
        _store = store;
        InitializeComponent();
        RefreshList();

        if (!string.IsNullOrWhiteSpace(seedSql))
        {
            Title = "Add to Favorites - Aurum";
            SqlBox.Text = seedSql;
            NameBox.Text = SuggestName(seedSql);
            StatusText.Text = "이름을 확인하고 Save 를 누르세요.";
            Opened += (_, _) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }
    }

    private void RefreshList(string? selectName = null)
    {
        var rows = FavoritesStore.Filter(_store.Items, FilterBox.Text)
            .Select(f => new FavoriteRow(f.Name, Preview(f.Sql), f))
            .ToList();
        FavoriteList.ItemsSource = rows;
        if (selectName is not null)
            FavoriteList.SelectedItem = rows.FirstOrDefault(
                r => string.Equals(r.Name, selectName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => RefreshList();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FavoriteList.SelectedItem is not FavoriteRow row)
            return;
        _editingName = row.Item.Name;
        NameBox.Text = row.Item.Name;
        SqlBox.Text = row.Item.Sql;
        StatusText.Text = "";
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        var sql = SqlBox.Text ?? "";
        try
        {
            if (_editingName is { } original && _store.Find(original) is not null)
                _store.Update(original, name, sql);
            else
                _store.Add(name, sql);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"저장 실패: {ex.Message} ({_store.FilePath})";
            return;
        }
        _editingName = name;
        RefreshList(name);
        StatusText.Text = $"Saved: {name}";
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (FavoriteList.SelectedItem is not FavoriteRow row)
        {
            StatusText.Text = "삭제할 항목을 목록에서 선택하세요.";
            return;
        }
        try
        {
            _store.Remove(row.Item.Name);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"삭제 실패: {ex.Message} ({_store.FilePath})";
            return;
        }
        _editingName = null;
        NameBox.Text = "";
        SqlBox.Text = "";
        RefreshList();
        StatusText.Text = $"Deleted: {row.Item.Name}";
    }

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        if (SqlBox.Text is not { Length: > 0 } sql)
        {
            StatusText.Text = "실행할 SQL 이 비어 있습니다.";
            return;
        }
        RunSql = sql;
        Close();
    }

    /// <summary>목록 더블클릭 = Run (Golden 의 즐겨찾기 실행).</summary>
    private void OnRun(object? sender, TappedEventArgs e) => OnRun(sender, (RoutedEventArgs)e);

    private void OnInsert(object? sender, RoutedEventArgs e)
    {
        if (SqlBox.Text is not { Length: > 0 } sql)
        {
            StatusText.Text = "삽입할 SQL 이 비어 있습니다.";
            return;
        }
        InsertSql = sql;
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>목록용 한 줄 미리보기 — 줄바꿈·연속 공백을 접는다.</summary>
    private static string Preview(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > PreviewLength ? flat[..PreviewLength] + "…" : flat;
    }

    /// <summary>Add 모드 기본 이름 — SQL 앞부분에서 딴다.</summary>
    private static string SuggestName(string sql)
    {
        var flat = Preview(sql);
        return flat.Length > SuggestedNameLength ? flat[..SuggestedNameLength].TrimEnd() : flat;
    }
}
