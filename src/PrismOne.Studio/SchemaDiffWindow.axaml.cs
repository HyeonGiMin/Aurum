using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// Tools &gt; Schema Diff — 읽기 전용 스키마 비교 (DATAGRIP_GAP §3a).
///
/// 흐름: 표준 사이트에서 Save Snapshot 으로 JSON 을 떠 두고(버전 관리),
/// 각 사이트에서 그 파일(또는 저장된 다른 접속)을 기준으로 현재 접속을 비교한다.
/// 버전 pill 이 못 잡는 "기록상 패치는 됐는데 실제 스키마가 어긋난" 경우를 잡는다.
/// **동기화 DDL 은 만들지 않는다** — 그건 iapdb 의 몫 (STATUS.md §2·3).
/// </summary>
public partial class SchemaDiffWindow : Window
{
    private readonly ConnectionProfile _profile;

    private sealed record BaselineChoice(string Label, string? FilePath, SavedConnection? Connection)
    {
        public override string ToString() => Label;
    }

    public SchemaDiffWindow() : this(ConnectionProfile.Default) { }

    public SchemaDiffWindow(ConnectionProfile profile)
    {
        InitializeComponent();
        _profile = profile;
        Title = $"Schema Diff - {profile.DisplayName}";
        MinWidth = 560;
        MinHeight = 420;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        WindowPlacementTracker.Attach(this, "diff");

        var choices = new List<BaselineChoice> { new("스냅샷 파일에서 선택…", null, null) };
        // 비밀번호가 저장된 접속만 — 여기서 비밀번호를 물어보는 창까지 두지 않는다
        choices.AddRange(ConnectionStore.Load()
            .Where(c => !string.IsNullOrEmpty(c.Password) && !c.SameTarget(profile))
            .Select(c => new BaselineChoice($"접속: {c.DisplayName}", null, c)));
        BaselineCombo.ItemsSource = choices;
    }

    private BaselineChoice? Selected => BaselineCombo.SelectedItem as BaselineChoice;
    private string? _baselineFile;

    private async void OnBaselineChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { FilePath: null, Connection: null }) { CompareButton.IsEnabled = Selected is not null; return; }
        // "파일에서 선택…" — 고르는 즉시 파일 대화상자
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "기준 스냅샷 선택",
            FileTypeFilter = [new FilePickerFileType("Schema snapshot") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) { BaselineCombo.SelectedItem = null; CompareButton.IsEnabled = false; return; }
        _baselineFile = files[0].Path.LocalPath;
        DirectionText.Text = $"기준: {System.IO.Path.GetFileName(_baselineFile)} ↔ 현재 접속 {_profile.DisplayName}";
        CompareButton.IsEnabled = true;
    }

    private async void OnCompare(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Selected is null) return;
        CompareButton.IsEnabled = false;
        BusyBar.IsVisible = true;
        try
        {
            StatusText.Text = "기준 스키마 읽는 중…";
            ErdGraph baseline;
            string baselineLabel;
            if (Selected.Connection is { } conn)
            {
                var profile = new ConnectionProfile(conn.Host, conn.Port, conn.Database,
                    conn.Username, conn.Password!, ReadOnly: true, conn.Kind);
                baseline = await LoadGraphAsync(profile);
                baselineLabel = conn.DisplayName;
            }
            else if (_baselineFile is { } file)
            {
                var doc = SchemaSnapshotFile.Load(file);
                baseline = doc.Graph;
                baselineLabel = $"{System.IO.Path.GetFileName(file)} ({doc.Source}, {doc.SavedAtUtc:yyyy-MM-dd})";
            }
            else return;

            StatusText.Text = "현재 접속 스키마 읽는 중…";
            var target = await LoadGraphAsync(_profile);

            var diff = SchemaDiff.Compare(baseline, target);
            BindResult(diff);
            DirectionText.Text = $"기준: {baselineLabel} ↔ 대상: {_profile.DisplayName}";
            StatusText.Text = diff.Summary + "   (읽기 전용 — DDL 은 만들지 않습니다)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"비교 실패: {ex.Message}";
        }
        finally
        {
            CompareButton.IsEnabled = true;
            BusyBar.IsVisible = false;
        }
    }

    private async void OnSaveSnapshot(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "현재 접속의 스키마 스냅샷 저장",
            SuggestedFileName = $"schema-{_profile.Database}-{DateTime.Now:yyyyMMdd}.json",
            FileTypeChoices = [new FilePickerFileType("Schema snapshot") { Patterns = ["*.json"] }],
        });
        if (file is null) return;
        BusyBar.IsVisible = true;
        try
        {
            StatusText.Text = "스키마 읽는 중…";
            var graph = await LoadGraphAsync(_profile);
            SchemaSnapshotFile.Save(file.Path.LocalPath,
                new SchemaSnapshotDoc(_profile.DisplayName, DateTime.UtcNow, graph));
            StatusText.Text = $"저장했습니다: {file.Path.LocalPath} — 테이블 {graph.Tables.Count}개, FK {graph.Relations.Count}개";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"저장 실패: {ex.Message}";
        }
        finally
        {
            BusyBar.IsVisible = false;
        }
    }

    /// <summary>모든 사용자 스키마의 테이블·컬럼·FK 를 읽는다 (ERD 카탈로그 재사용).</summary>
    private static async Task<ErdGraph> LoadGraphAsync(ConnectionProfile profile)
    {
        var catalog = profile.Provider.CreateErdCatalog(profile);
        var schemas = await catalog.GetSchemasAsync();
        return schemas.Count == 0 ? ErdGraph.Empty : await catalog.LoadAsync(schemas);
    }

    // ---------- 결과 트리 ----------

    internal void BindResult(SchemaDiffResult diff)
    {
        var items = new List<TreeViewItem>();
        if (diff.IsEmpty)
        {
            items.Add(new TreeViewItem { Header = Text("차이 없음 — 스키마가 일치합니다", ThemeBrushes.Get("DiffAddedBrush", "#2E7D32"), bold: true) });
        }
        else
        {
            AddGroup(items, $"빠진 테이블 ({diff.MissingTables.Count}) — 기준에는 있는데 대상에 없음",
                ThemeBrushes.Get("DiffRemovedBrush", "#C62828"), diff.MissingTables.Select(t => Leaf($"− {t.Key}", ThemeBrushes.Get("DiffRemovedBrush", "#C62828"),
                    string.Join("\n", t.Columns.Select(c => $"{c.Name} {c.Type}")))));
            AddGroup(items, $"추가된 테이블 ({diff.ExtraTables.Count}) — 대상에만 있음",
                ThemeBrushes.Get("DiffAddedBrush", "#2E7D32"), diff.ExtraTables.Select(t => Leaf($"+ {t.Key}", ThemeBrushes.Get("DiffAddedBrush", "#2E7D32"),
                    string.Join("\n", t.Columns.Select(c => $"{c.Name} {c.Type}")))));
            AddGroup(items, $"달라진 테이블 ({diff.ChangedTables.Count})",
                ThemeBrushes.Get("DiffChangedBrush", "#C77400"), diff.ChangedTables.Select(BuildChangedTable));
            AddGroup(items, $"빠진 FK ({diff.MissingRelations.Count})",
                ThemeBrushes.Get("DiffRemovedBrush", "#C62828"), diff.MissingRelations.Select(r => Leaf($"− {r.Describe}", ThemeBrushes.Get("DiffRemovedBrush", "#C62828"), r.Name)));
            AddGroup(items, $"추가된 FK ({diff.ExtraRelations.Count})",
                ThemeBrushes.Get("DiffAddedBrush", "#2E7D32"), diff.ExtraRelations.Select(r => Leaf($"+ {r.Describe}", ThemeBrushes.Get("DiffAddedBrush", "#2E7D32"), r.Name)));
        }
        ResultTree.ItemsSource = items;
    }

    private static TreeViewItem BuildChangedTable(TableChange change)
    {
        var item = new TreeViewItem { Header = Text($"~ {change.Key}", ThemeBrushes.Get("DiffChangedBrush", "#C77400"), bold: true), IsExpanded = true };
        foreach (var col in change.MissingColumns)
            item.Items.Add(Leaf($"− {col.Name}  {col.Type}{(col.NotNull ? " NOT NULL" : "")}", ThemeBrushes.Get("DiffRemovedBrush", "#C62828"), null));
        foreach (var col in change.ExtraColumns)
            item.Items.Add(Leaf($"+ {col.Name}  {col.Type}{(col.NotNull ? " NOT NULL" : "")}", ThemeBrushes.Get("DiffAddedBrush", "#2E7D32"), null));
        foreach (var c in change.ChangedColumns)
            item.Items.Add(Leaf($"~ {c.Column}  {c.Aspect}: {c.Baseline} → {c.Target}", ThemeBrushes.Get("DiffChangedBrush", "#C77400"), null));
        return item;
    }

    private static void AddGroup(List<TreeViewItem> items, string title, IBrush brush, IEnumerable<TreeViewItem> children)
    {
        var list = children.ToList();
        if (list.Count == 0) return;
        var group = new TreeViewItem { Header = Text(title, brush, bold: true), IsExpanded = true };
        foreach (var child in list)
            group.Items.Add(child);
        items.Add(group);
    }

    private static TreeViewItem Leaf(string text, IBrush brush, string? tooltip)
    {
        var item = new TreeViewItem { Header = Text(text, brush, bold: false) };
        if (!string.IsNullOrEmpty(tooltip))
            ToolTip.SetTip(item, tooltip);
        return item;
    }

    private static TextBlock Text(string text, IBrush brush, bool bold) => new()
    {
        Text = text,
        Foreground = brush,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
    };
}
