using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Studio;

/// <summary>
/// 왼쪽 Database Explorer(스키마 트리)와 오른쪽 Object Browser(F8).
/// 둘 다 "스키마를 훑어보는" 화면이라 실행·편집 흐름과 분리해 둔다.
/// </summary>
public partial class MainWindow
{
    // ---------- Database Explorer (DataGrip 대응, 왼쪽) ----------

    /// <summary>
    /// View > Database Explorer (Alt+1). Golden 에는 없던 패널이다 —
    /// 오른쪽 Object Browser 가 "한 테이블을 골라 describe" 하는 Golden 방식인 반면
    /// 이쪽은 스키마 전체를 트리로 펼쳐두고 걸어다니는 DataGrip 방식이다.
    /// </summary>
    private void OnMenuToggleExplorer(object? sender, RoutedEventArgs e)
    {
        var show = !ExplorerPanel.IsVisible;
        ExplorerPanel.IsVisible = show;
        ExplorerSplitter.IsVisible = show;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(show ? 300 : 0);
        MainGrid.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        ExplorerMenuItem.Header = show
            ? "Database Explorer (왼쪽 스키마 트리) ✓"
            : "Database Explorer (왼쪽 스키마 트리)";
        if (show) RebuildExplorer();
    }

    private void OnExplorerSearchChanged(object? sender, RoutedEventArgs e) => RebuildExplorer();

    private async void OnExplorerRefresh(object? sender, RoutedEventArgs e)
    {
        if (_profile is { } profile)
        {
            _schemaCache?.Invalidate();
            await LoadBrowserAsync(profile);
        }
        RebuildExplorer();
    }

    /// <summary>
    /// 이미 읽어 둔 카탈로그(<c>_allTables</c>)로 트리를 만든다 — 새 조회를 하지 않는다.
    /// Mongo 는 스키마가 하나(collections)뿐이라 자연히 컬렉션 목록이 된다.
    /// </summary>
    private void RebuildExplorer()
    {
        if (ExplorerTree is null) return;

        var needle = ExplorerSearch.Text?.Trim() ?? "";
        var tables = _allTables.AsEnumerable();
        var routines = _allRoutines.AsEnumerable();
        if (needle.Length > 0)
        {
            tables = tables.Where(t => t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
            routines = routines.Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var tableGroups = tables.GroupBy(t => t.Schema).ToDictionary(g => g.Key, g => g.ToList());
        // PL/Edit 4단계 — Oracle 이면 스키마 밑에 프로시저/함수/패키지도 같이 넣는다
        var routineGroups = routines.GroupBy(r => r.Owner).ToDictionary(g => g.Key, g => g.ToList());
        var schemaKeys = tableGroups.Keys.Union(routineGroups.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        // 스키마가 하나뿐이거나 검색 중이면 펼쳐서 보여준다 — 한 번 더 클릭하게 만들 이유가 없다
        var expand = schemaKeys.Count == 1 || needle.Length > 0;

        var nodes = schemaKeys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(schema =>
            {
                tableGroups.TryGetValue(schema, out var schemaTables);
                routineGroups.TryGetValue(schema, out var schemaRoutines);
                var tableNodes = (schemaTables ?? [])
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new ExplorerNode(
                        t.Name, t.IsView ? "view" : "", ExplorerNodeKind.Table, t.QualifiedName, [])
                    {
                        Schema = t.Schema,
                        Icon = IconGeometry(t.IsView ? "IconViewGrid" : "IconTableGrid"),
                        IconBrush = Brush(t.IsView ? "ViewPurpleBrush" : "TableBlueBrush"),
                    });
                var routineNodes = (schemaRoutines ?? [])
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.ObjectType, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new ExplorerNode(
                        r.Name, r.ObjectType.ToLowerInvariant(), ExplorerNodeKind.Routine, $"{r.Owner}.{r.Name}", [])
                    {
                        Schema = r.Owner,
                        ObjectType = r.ObjectType,
                        Icon = IconGeometry("IconCode"),
                        IconBrush = Brush("RoutineOrangeBrush"),
                    });
                var count = (schemaTables?.Count ?? 0) + (schemaRoutines?.Count ?? 0);
                return new ExplorerNode(schema, $"({count})", ExplorerNodeKind.Schema, "",
                    [.. tableNodes, .. routineNodes])
                {
                    Icon = IconGeometry("IconDatabase"),
                    IconBrush = Brush("DbGreenBrush"),
                    IsExpanded = expand,
                };
            })
            .ToList();

        ExplorerTree.ItemsSource = nodes;
        ExplorerHint.Text = nodes.Count == 0
            ? (_allTables.Count == 0 && _allRoutines.Count == 0
                ? "접속하면 스키마가 표시됩니다."
                : "검색과 일치하는 것이 없습니다.")
            : "더블클릭하면 조회 문장이 에디터에 들어갑니다.";
    }

    /// <summary>앱 리소스에서 아이콘 도형을 꺼낸다. 없으면 null (아이콘만 비고 트리는 뜬다).</summary>
    private Geometry? IconGeometry(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;

    private IBrush? Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;

    /// <summary>
    /// 테이블/컬렉션을 더블클릭하면 조회 문장을 에디터에 넣는다.
    /// Mongo 는 SQL 이 아니므로 셸 구문으로 만든다 (Studio3T 에서 컬렉션을 여는 감각).
    /// 프로시저/함수/패키지(Oracle) 는 소스를 새 탭에 연다 (PL/Edit 4단계).
    /// </summary>
    private async void OnExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerNode node) return;

        if (node.Kind == ExplorerNodeKind.Routine)
        {
            await OpenRoutineSourceAsync(node);
            return;
        }
        if (node.Kind != ExplorerNodeKind.Table) return;
        if (ActiveView is not { } view) return;

        if (_profile?.Kind == DbKind.MongoDb)
        {
            // Explorer 는 서버의 모든 DB 를 보여주므로, 접속한 DB 가 아닌 컬렉션을
            // 고를 수 있다 — 그대로 실행하면 엉뚱한 DB 를 조회하게 되니 먼저 옮긴다.
            view.TryUseDatabase(node.Schema);
            view.InsertAtCaret($"db.{node.Name}.find({{}})");
            return;
        }
        view.InsertAtCaret($"select * from {node.Qualified}");
    }

    /// <summary>PL/Edit 4단계 — 프로시저/함수/패키지 소스를 ALL_SOURCE 에서 읽어
    /// CREATE OR REPLACE 문으로 새 탭에 연다. 그대로 실행하면 재컴파일된다.</summary>
    private async Task OpenRoutineSourceAsync(ExplorerNode node)
    {
        if (_profile is not { Kind: DbKind.Oracle } profile) return;
        if (profile.Provider.CreateErdCatalog(profile) is not OracleErdCatalog catalog) return;

        StatusLabel.Text = $"Loading source for {node.Name}…";
        try
        {
            var source = await catalog.GetSourceAsync(node.Schema, node.Name, node.ObjectType);
            if (source.Length == 0)
            {
                StatusLabel.Text = $"{node.Name}: source not found";
                return;
            }
            await NewTabAsync(profile, $"{node.Name} ({node.ObjectType.ToLowerInvariant()})", source);
            StatusLabel.Text = "";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Load source failed: {ex.Message}";
        }
    }

    /// <summary>탭줄 오른쪽 ▾ — Golden 의 탭 목록 드롭다운.</summary>
    private void OnTabListClick(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var tab in _tabs)
        {
            var item = new MenuItem { Header = tab.Header };
            var target = tab;
            item.Click += (_, _) => QueryTabs.SelectedItem = target;
            flyout.Items.Add(item);
        }
        if (flyout.Items.Count > 0)
            flyout.ShowAt(TabListButton);
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ActiveView is { } view)
        {
            StatusLabel.Text = view.InTransaction ? $"[TX] {view.InfoMessage}" : view.InfoMessage;
            RowsLabel.Text = view.InfoRows;
            TimeLabel.Text = view.InfoTime;
            UpdateEditorStatus(view);
            UpdateTxState();
            UpdateShowButton();
        }
    }

    private void OnTabInfoChanged(QueryTabView view)
    {
        if (!ReferenceEquals(view, ActiveView)) return;
        // 열린 트랜잭션이 있으면 [TX] 표시 (Golden 의 미커밋 알림)
        StatusLabel.Text = view.InTransaction ? $"[TX] {view.InfoMessage}" : view.InfoMessage;
        RowsLabel.Text = view.InfoRows;
        TimeLabel.Text = view.InfoTime;
        UpdateTxState();
    }

    /// <summary>Golden 상태바의 Modified / Selected N records.</summary>
    private void UpdateEditorStatus(QueryTabView view)
    {
        ModifiedLabel.Text = view.IsModified ? "Modified" : "";
        SelectionLabel.Text = view.InfoSelection;
    }

    private void OnTabCaretChanged(QueryTabView view, int line, int col)
    {
        if (ReferenceEquals(view, ActiveView))
            CaretLabel.Text = $"{line} : {col}";
    }

    // ---------- Schema browser (오른쪽 패널) ----------

    private async Task LoadBrowserAsync(ConnectionProfile profile)
    {
        // DataGrip 식 introspection — 접속당 한 번만 읽고 describe·자동완성은 캐시에서.
        // (예전엔 테이블을 고를 때마다 새 접속을 열어 컬럼을 조회했다)
        _schemaCache = SchemaCache.ForProfile(profile);
        try
        {
            _allTables = [.. await _schemaCache.GetTablesAsync()];
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Schema load failed: {ex.Message}";
            return;
        }

        // 왼쪽 Explorer 는 같은 카탈로그를 쓰므로 여기서 함께 갱신한다
        RebuildExplorer();

        var schemas = _allTables.Select(t => t.Schema).Distinct().OrderBy(s => s).ToList();

        // PL/Edit 4단계 — Oracle 이면 프로시저/함수/패키지도 읽어 Explorer 에 얹는다.
        // 실패해도(권한 부족 등) 테이블 탐색은 그대로 되어야 하므로 조용히 넘어간다.
        _allRoutines = [];
        if (profile.Kind == DbKind.Oracle && profile.Provider.CreateErdCatalog(profile) is OracleErdCatalog oracleCatalog)
        {
            try
            {
                _allRoutines = await oracleCatalog.GetRoutinesAsync(schemas);
            }
            catch { /* Object Browser 는 테이블만으로도 쓸 수 있어야 한다 */ }
            RebuildExplorer();
        }

        SchemaCombo.ItemsSource = schemas;
        var preferred = schemas.FirstOrDefault(s => s == _profile?.Database) ?? schemas.FirstOrDefault();
        SchemaCombo.SelectedItem = preferred;
        foreach (var view in AllViews())
            view.PreferredSchema = preferred;
        RefreshObjectList();
    }

    private void OnBrowserFilterChanged(object? sender, RoutedEventArgs e)
    {
        RefreshObjectList();
        // 자동완성이 현재 스키마를 우선하도록 전달
        var schema = SchemaCombo.SelectedItem as string;
        foreach (var view in AllViews())
            view.PreferredSchema = schema;
    }

    private void RefreshObjectList()
    {
        if (SchemaCombo is null || ObjectsGrid is null) return;
        var schema = SchemaCombo.SelectedItem as string;
        var show = ShowCombo.SelectedItem as string ?? "Tables";
        var search = SearchBox?.Text?.Trim() ?? "";

        var rows = _allTables
            .Where(t => schema is null || t.Schema == schema)
            .Where(t => show switch
            {
                "Tables" => !t.IsView,
                "Views" => t.IsView,
                _ => true,
            })
            .Where(t => search.Length == 0 || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(t => new ObjectRow(t.Name, t.IsView ? "view" : "table", t))
            .ToList();
        ObjectsGrid.ItemsSource = rows;
    }

    private async void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ObjectsGrid.SelectedItem is not ObjectRow row || _schemaCache is null)
            return;
        DescribeTitle.Text = $"{row.Type.ToUpperInvariant()} {row.Info.Schema}.{row.Info.Name}";
        try
        {
            // 캐시에서 즉시 — 접속을 새로 열지 않는다
            DescribeGrid.ItemsSource = await _schemaCache.GetColumnsAsync(row.Info);
        }
        catch (Exception ex)
        {
            DescribeGrid.ItemsSource = null;
            StatusLabel.Text = $"Describe failed: {ex.Message}";
        }
    }

    /// <summary>더블클릭 — Golden 처럼 이름을 쿼리에 붙여넣는다 (Use Schema 반영).</summary>
    private void OnObjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ObjectsGrid.SelectedItem is ObjectRow row)
        {
            var name = UseSchemaBox.IsChecked == true ? row.Info.QualifiedName : row.Info.QuotedName;
            ActiveView?.InsertAtCaret(name);
        }
    }

    /// <summary>quick SQL 그리드 셀 클릭 — 해당 단어를 쿼리에 삽입.</summary>
    private void OnQuickCell(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string word })
            ActiveView?.InsertAtCaret(word + " ");
    }

}
