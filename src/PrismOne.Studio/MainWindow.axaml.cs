using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Npgsql;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Studio;

/// <summary>오른쪽 스키마 패널의 오브젝트 목록 한 행.</summary>
public sealed record ObjectRow(string Name, string Type, TableInfo Info);

public enum ExplorerNodeKind { Schema, Table }

/// <summary>
/// Database Explorer 트리의 한 줄. (XAML 컴파일 바인딩 대상이라 public)
/// <paramref name="Qualified"/> 는 테이블 노드에서만 채워지는 스키마 포함 이름이다.
/// </summary>
public sealed record ExplorerNode(
    string Name,
    string Detail,
    ExplorerNodeKind Kind,
    string Qualified,
    IReadOnlyList<ExplorerNode> Children)
{
    /// <summary>이 노드가 속한 스키마 (Mongo 는 데이터베이스 이름). 테이블 노드에만 채운다.</summary>
    public string Schema { get; init; } = "";

    /// <summary>트리 아이콘. 스키마=원통, 테이블=표, 뷰=칸 적은 표.</summary>
    public Geometry? Icon { get; init; }
    public IBrush? IconBrush { get; init; }

    /// <summary>표 아이콘의 머리줄만 옅게 채운다 (윤곽선만이면 밋밋하다).</summary>
    public IBrush? IconFill { get; init; }

    /// <summary>열린 채로 그릴지 — 스키마가 하나뿐이거나 검색 중이면 펼쳐 둔다.</summary>
    public bool IsExpanded { get; init; }
}

public partial class MainWindow : Window
{
    private ConnectionProfile? _profile;
    private QuerySession? _sharedSession;   // Golden: 탭들이 공유하는 메인 접속
    private readonly ObservableCollection<TabItem> _tabs = [];
    private int _tabCounter;
    private List<TableInfo> _allTables = [];
    /// <summary>접속당 하나 — 테이블·컬럼을 한 번만 읽는다 (DataGrip introspection 캐시).</summary>
    private SchemaCache? _schemaCache;
    private AppOptions _options = AppOptions.Load();
    private readonly FavoritesStore _favorites = FavoritesStore.Load();
    private NativeMenu? _nativeFavoritesMenu;   // macOS 네이티브 메뉴바의 Favorites 하위

    public MainWindow()
    {
        InitializeComponent();
        QueryTabs.ItemsSource = _tabs;
        ShowCombo.ItemsSource = new[] { "Tables", "Views", "All" };
        ShowCombo.SelectedIndex = 0;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closed += async (_, _) =>
        {
            foreach (var view in AllViews())
                await view.CloseSessionAsync();
            if (_sharedSession is not null)
                await _sharedSession.DisposeAsync();
        };
        // 창 위치·크기 기억 (UI_POLISH P1-3)
        WindowPlacementTracker.Attach(this, "main");

        // 테마 전환을 즉시 반영 (에디터 하이라이팅·메뉴 체크)
        ActualThemeVariantChanged += (_, _) => OnThemeVariantChanged();
        if (ThemeBrushes.IsDark)
            OnThemeVariantChanged();   // 옵션이 Dark 로 시작한 경우

        // Golden: 메인 창(빈 Query1 탭)이 먼저 그려지고 그 위로 로그온 창이 바로 뜬다.
        // 취소하면 미접속 상태로 남고, 이후엔 Ctrl+L 로 연다.
        BuildNativeMenu();
        RebuildFavoritesMenu();
        if (Environment.GetEnvironmentVariable("IAPDM_SHOT_DIR") is null)
            Opened += async (_, _) =>
            {
                if (_tabs.Count == 0)
                    await NewTabAsync(null);
                await ShowLogonAsync();
            };

        // 자가 스크린샷 모드 (UI 점검용): IAPDM_SHOT_DIR=<dir> 로 실행하면
        // 샘플 데이터로 채운 화면을 PNG 로 저장하고 종료한다.
        // IAPDM_SHOT_CONN="host:port/db|user|pass" 를 함께 주면 실제 접속·쿼리·스크롤까지 재현한다.
        // 앱 아이콘 PNG 재생성: IAPDM_RENDER_ICON=<png경로>
        if (Environment.GetEnvironmentVariable("IAPDM_RENDER_ICON") is { Length: > 0 } iconPath)
        {
            Opened += (_, _) =>
            {
                RenderAppIcon(iconPath);
                Environment.Exit(0);
            };
        }
        else if (Environment.GetEnvironmentVariable("IAPDM_SHOT_DIR") is { Length: > 0 } shotDir)
        {
            if (Environment.GetEnvironmentVariable("IAPDM_SHOT_SIZE")?.Split('x') is [var w, var h]
                && double.TryParse(w, out var width) && double.TryParse(h, out var height))
            {
                Width = width;
                Height = height;
            }
            Opened += (_, _) =>
                _ = Environment.GetEnvironmentVariable("IAPDM_SHOT_CONN") is { Length: > 0 } conn
                    ? CaptureLiveAsync(shotDir, conn)
                    : CaptureUiAsync(shotDir);
        }
    }

    private QueryTabView? ActiveView => (QueryTabs.SelectedItem as TabItem)?.Content as QueryTabView;

    private IEnumerable<QueryTabView> AllViews() =>
        _tabs.Select(t => t.Content).OfType<QueryTabView>();

    // ---------- Logon ----------

    private async void OnMenuConnect(object? sender, RoutedEventArgs e) => await ShowLogonAsync();

    private async Task ShowLogonAsync()
    {
        var dialog = new ConnectDialog(_profile ?? ConnectionProfile.Default);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } profile)
            await ApplyProfileAsync(profile);
    }

    private async Task ApplyProfileAsync(ConnectionProfile profile)
    {
        _profile = profile;
        NewTabButton.IsEnabled = true;
        ExecuteButton.IsEnabled = true;
        RunScriptButton.IsEnabled = true;
        ExplainButton.IsEnabled = true;
        ExplainAnalyzeButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        FetchAllButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        // Golden 타이틀 형식: user@db - Benthic Software: Golden7
        Title = $"{profile.DisplayName} - Aurum";
        StatusLabel.Text = $"Connected: {profile.DisplayName}";

        // 쿼리 실행은 이제 드라이버 중립(QuerySession 이 DbConnection 을 쓴다)이지만,
        // Object Browser·자동완성 캐시·스키마 버전 pill 은 아직 PG 카탈로그에 묶여 있다.
        // COPY 기반 대량 내보내기도 provider 가 지원한다고 할 때만 켠다.
        var isPostgres = profile.Kind == DbKind.PostgreSql;
        ExportButton.IsEnabled = profile.Provider.Capabilities.BulkExport;

        // SchemaCache 가 provider 별로 카탈로그를 읽으므로 Object Browser·자동완성도
        // 모든 DB 에서 채워진다 (PG 이외는 ERD 카탈로그를 재활용)
        await LoadBrowserAsync(profile);

        foreach (var v in AllViews())
        {
            v.CompletionTables = _allTables;   // 자동완성 카탈로그 갱신
            v.SchemaCache = _schemaCache;      // 컬럼 완성도 캐시에서 (접속 안 염)
        }

        // Golden: 메인 접속 하나를 공유 세션으로 열고 탭들에 붙인다
        if (_sharedSession is not null)
            await _sharedSession.DisposeAsync();
        try
        {
            _sharedSession = await QuerySession.CreateAsync(profile);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Connect failed: {ex.Message}";
            await ErrorDialog.ShowAsync(this, "Connection failed",
                $"세션을 열지 못했습니다.\n{profile.DisplayName}", ex);
            return;
        }

        UpdateConnectionPill();
        // 스키마 버전 pill 은 PRISMONE.schema_version(PG) 을 읽는다
        if (isPostgres)
            await UpdateSchemaVersionPillAsync(profile);
        else
            StatusLabel.Text =
                $"Connected: {profile.DisplayName} · {profile.Provider.DisplayName} — " +
                "쿼리 실행과 Diagram 은 되고, Object Browser·자동완성은 아직 PostgreSQL 전용입니다";

        var orphans = AllViews().Where(v => !v.IsConnected).ToList();
        if (orphans.Count > 0)
        {
            foreach (var view in orphans)
                view.AttachSession(_sharedSession);
            orphans[0].FocusEditor();
        }
        else
        {
            await NewTabAsync(profile);
        }
        UpdateTxState();   // Tx mode/isolation 콤보를 접속 상태에 맞춘다
    }

    // ---------- Tabs ----------

    private async void OnMenuNewTab(object? sender, RoutedEventArgs e)
        => await NewTabAsync(_profile);   // 미접속이면 세션 없는 탭 (로그인 시 자동 연결)

    private async void OnMenuNewPrivateTab(object? sender, RoutedEventArgs e)
    {
        if (_profile is not { } profile)
        {
            StatusLabel.Text = "Private Tab 은 로그온 후 사용할 수 있습니다 (Ctrl+L)";
            return;
        }
        _tabCounter++;
        await NewTabAsync(profile, $"Private {_tabCounter}", isPrivate: true);
    }

    private async void OnMenuCloseTab(object? sender, RoutedEventArgs e)
    {
        if (QueryTabs.SelectedItem is TabItem item && item.Content is QueryTabView view)
            await CloseTabAsync(item, view);
    }

    private async Task<QueryTabView> NewTabAsync(ConnectionProfile? profile, string? title = null, string? sql = null, bool isPrivate = false)
    {
        var view = new QueryTabView
        {
            CompletionTables = _allTables,
            SchemaCache = _schemaCache,
            Options = _options,
            PreferredSchema = SchemaCombo.SelectedItem as string,
        };
        view.InfoChanged += OnTabInfoChanged;
        view.CaretChanged += OnTabCaretChanged;

        _tabCounter++;
        var item = new TabItem
        {
            Header = title ?? $"Query {_tabCounter}",
            Content = view,
        };
        _tabs.Add(item);
        QueryTabs.SelectedItem = item;
        if (sql is not null)
            view.SetSql(sql);
        if (isPrivate && profile is not null)
            await view.ConnectPrivateAsync(profile);
        else if (_sharedSession is not null)
            view.AttachSession(_sharedSession);
        view.FocusEditor();
        return view;
    }

    private async Task CloseTabAsync(TabItem item, QueryTabView view)
    {
        _tabs.Remove(item);
        await view.CloseSessionAsync();
    }

    /// <summary>macOS: 인앱 메뉴 대신 상단 네이티브 메뉴바 사용. 단축키 자체는 KeyDown 핸들러가 처리.</summary>
    private void BuildNativeMenu()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        MainMenu.IsVisible = false;

        NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }
        NativeMenuItem Sub(string header, params NativeMenuItemBase[] items)
        {
            var item = new NativeMenuItem(header);
            var menu = new NativeMenu();
            foreach (var child in items)
                menu.Items.Add(child);
            item.Menu = menu;
            return item;
        }
        var args = new RoutedEventArgs();

        var root = new NativeMenu();
        root.Items.Add(Sub("File",
            Item("New Query Tab (⌘T)", () => OnMenuNewTab(this, args)),
            Item("New Private Tab (⇧⌘T)", () => OnMenuNewPrivateTab(this, args)),
            Item("Close Tab (⌘W)", () => OnMenuCloseTab(this, args)),
            new NativeMenuItemSeparator(),
            Item("Open Script… (⌘O)", () => OnMenuOpen(this, args)),
            Item("Save Script As… (⌘S)", () => OnMenuSave(this, args)),
            new NativeMenuItemSeparator(),
            Item("Open Workspace…", () => OnMenuOpenWorkspace(this, args)),
            Item("Save Workspace…", () => OnMenuSaveWorkspace(this, args))));
        root.Items.Add(Sub("Edit",
            Item("Undo", () => OnMenuUndo(this, args)),
            Item("Redo", () => OnMenuRedo(this, args)),
            new NativeMenuItemSeparator(),
            Item("Cut", () => OnMenuCut(this, args)),
            Item("Copy", () => OnMenuCopy(this, args)),
            Item("Paste", () => OnMenuPaste(this, args))));
        root.Items.Add(Sub("Script",
            Item("Run Statement (F9)", () => OnMenuExecute(this, args)),
            Item("Run Script (F5)", () => OnMenuRunScript(this, args)),
            Item("Explain Statement", () => OnMenuExplain(this, args)),
            Item("Cancel", () => OnMenuCancel(this, args)),
            new NativeMenuItemSeparator(),
            Item("Describe (⌘D)", () => OnMenuDescribe(this, args)),
            Item("Commit (Ctrl+F5)", () => OnMenuCommit(this, args)),
            Item("Rollback (Ctrl+F6)", () => OnMenuRollback(this, args)),
            new NativeMenuItemSeparator(),
            Item("Run and Edit (Ctrl+E)", () => OnMenuRunAndEdit(this, args)),
            Item("Submit Edits (⇧⌘S)", () => OnMenuSubmitEdits(this, args)),
            Item("Add Row", () => OnMenuAddRow(this, args)),
            Item("Delete Selected Records…", () => OnMenuDeleteRows(this, args)),
            Item("Revert Edits", () => OnMenuRevertEdits(this, args)),
            new NativeMenuItemSeparator(),
            Item("Print SQL… (⌘P)", () => PrintSql(auto: true)),
            Item("Print Preview (SQL)", () => PrintSql(auto: false))));
        root.Items.Add(Sub("Results",
            Item("Fetch All Records (⌘End)", () => OnMenuFetchAll(this, args)),
            new NativeMenuItemSeparator(),
            Item("Transpose Columns/Records (⇧⌘X)", () => OnMenuTranspose(this, args)),
            Item("Size All Columns to Fit", () => OnMenuSizeColumns(this, args)),
            Item("Goto Record Number… (⌘G)", () => OnMenuGotoRecord(this, args)),
            Item("Cell Details… (⌃F11)", () => OnMenuCellDetail(this, args)),
            new NativeMenuItemSeparator(),
            Item("Filter Records Like Selected Cell", () => OnMenuFilterCellGrid(this, args)),
            Item("Clear Filter", () => OnMenuClearFilter(this, args)),
            Item("Append Filter Clause to Editor", () => OnMenuFilterCell(this, args)),
            Item("Clear Results", () => OnMenuClearResults(this, args)),
            Item("Pin Results to New Window", () => OnMenuPinResult(this, args)),
            new NativeMenuItemSeparator(),
            Item("Export All Rows As CSV… (COPY)", () => OnMenuExport(this, args)),
            Item("Save Grid As TSV…", () => OnMenuExportTsv(this, args)),
            Item("Save Grid As INSERT…", () => OnMenuExportInsert(this, args)),
            new NativeMenuItemSeparator(),
            Item("Print Grid…", () => PrintGrid(auto: true)),
            Item("Print Preview (Grid)", () => PrintGrid(auto: false))));
        var favorites = Sub("Favorites",
            Item("Add Current Statement… (⇧⌘F)", () => OnMenuAddFavorite(this, args)),
            Item("Manage Favorites…", () => OnMenuManageFavorites(this, args)),
            new NativeMenuItemSeparator());
        _nativeFavoritesMenu = favorites.Menu;
        root.Items.Add(favorites);
        root.Items.Add(Sub("View",
            Item("Object Browser (F8)", () => OnMenuToggleBrowser(this, args)),
            Item("Toggle DataGrid/Text/Log View (F12)", () => OnMenuCycleResultView(this, args)),
            new NativeMenuItemSeparator(),
            Item("Toggle Dark Mode", () => OnMenuToggleTheme(this, args))));
        root.Items.Add(Sub("Tools",
            Item("Logon… (⌘L)", () => _ = ShowLogonAsync()),
            Item("SQL Builder…", () => OnMenuSqlBuilder(this, args)),
            Item("Diagram (ERD)…", () => OnMenuErd(this, args)),
            Item("Schema Diff…", () => OnMenuSchemaDiff(this, args)),
            Item("Import CSV/TSV…", () => OnMenuImportCsv(this, args)),
            Item("Query History…", () => OnMenuHistory(this, args)),
            Item("Session Monitor…", () => OnMenuSessionMonitor(this, args)),
            Item("Options…", () => OnMenuOptions(this, args))));
        root.Items.Add(Sub("Help",
            Item("About Aurum", () => OnMenuAbout(this, args))));
        NativeMenu.SetMenu(this, root);
    }

    /// <summary>
    /// View > Dark Mode — 라이트/다크 즉시 전환 + 옵션 저장.
    /// (System 으로 두고 싶으면 Options 의 테마 콤보에서 고른다)
    /// </summary>
    private void OnMenuToggleTheme(object? sender, RoutedEventArgs e)
    {
        var next = ThemeBrushes.IsDark ? "Light" : "Dark";
        App.ApplyTheme(next);
        _options.Theme = next;
        _options.Save();
    }

    /// <summary>테마가 바뀌면 코드가 만든 색(에디터 하이라이팅)을 따라 바꾼다.</summary>
    private void OnThemeVariantChanged()
    {
        var dark = ThemeBrushes.IsDark;
        foreach (var view in AllViews())
            view.ApplyEditorTheme(dark);
        ThemeMenuItem.Header = dark ? "Dark Mode ✓" : "Dark Mode";
    }

    /// <summary>View > Object Browser — Golden 6 기본은 패널 없음, 필요할 때만 켠다.</summary>
    private void OnMenuToggleBrowser(object? sender, RoutedEventArgs e)
    {
        var show = !BrowserPanel.IsVisible;
        BrowserPanel.IsVisible = show;
        BrowserSplitter.IsVisible = show;
        // 왼쪽 Explorer 가 앞의 두 컬럼을 쓰므로 오른쪽 패널은 3·4 번이다
        MainGrid.ColumnDefinitions[3].Width = new GridLength(show ? 4 : 0);
        MainGrid.ColumnDefinitions[4].Width = new GridLength(show ? 352 : 0);
        BrowserMenuItem.Header = show ? "Object Browser ✓" : "Object Browser";
        BrowserToggleButton.Classes.Set("active", show);
    }

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
        if (needle.Length > 0)
            tables = tables.Where(t => t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));

        // 스키마가 하나뿐이거나 검색 중이면 펼쳐서 보여준다 — 한 번 더 클릭하게 만들 이유가 없다
        var groups = tables.GroupBy(t => t.Schema).ToList();
        var expand = groups.Count == 1 || needle.Length > 0;

        var nodes = groups
            .AsEnumerable()
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExplorerNode(
                g.Key, $"({g.Count()})", ExplorerNodeKind.Schema, "",
                [.. g.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(t => new ExplorerNode(
                         t.Name,
                         t.IsView ? "view" : "",
                         ExplorerNodeKind.Table,
                         t.QualifiedName,
                         [])
                     {
                         Schema = t.Schema,
                         Icon = Icon(t.IsView ? "IconViewGrid" : "IconTableGrid"),
                         IconBrush = Brush(t.IsView ? "ViewPurpleBrush" : "TableBlueBrush"),
                     })])
            {
                Icon = Icon("IconDatabase"),
                IconBrush = Brush("DbGreenBrush"),
                IsExpanded = expand,
            })
            .ToList();

        ExplorerTree.ItemsSource = nodes;
        ExplorerHint.Text = nodes.Count == 0
            ? (_allTables.Count == 0 ? "접속하면 스키마가 표시됩니다." : "검색과 일치하는 것이 없습니다.")
            : "더블클릭하면 조회 문장이 에디터에 들어갑니다.";
    }

    /// <summary>앱 리소스에서 아이콘 도형을 꺼낸다. 없으면 null (아이콘만 비고 트리는 뜬다).</summary>
    private Geometry? Icon(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;

    private IBrush? Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;

    /// <summary>
    /// 테이블/컬렉션을 더블클릭하면 조회 문장을 에디터에 넣는다.
    /// Mongo 는 SQL 이 아니므로 셸 구문으로 만든다 (Studio3T 에서 컬렉션을 여는 감각).
    /// </summary>
    private void OnExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerNode { Kind: ExplorerNodeKind.Table } node) return;
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

    // ---------- Execute / keyboard ----------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var cmdOrCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // Ctrl+F5/F6 은 무수식 F5/F6 보다 먼저 판정해야 한다 (Golden 키맵)
        if (e.Key == Key.F5 && cmdOrCtrl)
        {
            e.Handled = true;
            OnMenuCommit(sender, e);
        }
        else if (e.Key == Key.F6 && cmdOrCtrl)
        {
            e.Handled = true;
            OnMenuRollback(sender, e);
        }
        else if (e.Key is Key.D1 or Key.NumPad1 && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // DataGrip: Alt+1 = Database Explorer
            e.Handled = true;
            OnMenuToggleExplorer(sender, e);
        }
        else if (e.Key == Key.F7 && cmdOrCtrl)
        {
            // Golden: Ctrl+F7 = Run Selected — 무수식 F7 보다 먼저 판정해야 한다
            e.Handled = true;
            _ = ActiveView?.ExecuteSelectedAsync();
        }
        else if (e.Key == Key.F9 || e.Key == Key.F7 || (e.Key == Key.Enter && cmdOrCtrl))
        {
            // Golden: F7/Ctrl+Enter = Run One Statement At Cursor (F9 는 Golden 8)
            e.Handled = true;
            _ = ActiveView?.ExecuteAtCaretAsync();
        }
        else if (e.Key == Key.F5 || e.Key == Key.F6 || (e.Key == Key.Enter && shift))
        {
            // Golden: F5/Shift+Enter = Run Script, F6 = Run Script From Cursor —
            // 우리 RunScript 는 이미 커서부터 끝까지(Golden 8 시맨틱)라 둘 다 여기로
            e.Handled = true;
            _ = ActiveView?.RunScriptAsync();
        }
        else if (e.Key == Key.E && cmdOrCtrl)
        {
            // Golden: Ctrl+E = Run Script And Go To Edit Mode
            e.Handled = true;
            OnMenuRunAndEdit(sender, e);
        }
        else if (e.Key == Key.End && cmdOrCtrl)
        {
            if (ActiveView is { } view)
            {
                e.Handled = true;
                _ = view.FetchAllAsync();
            }
        }
        else if ((e.Key == Key.T || e.Key == Key.N) && cmdOrCtrl)
        {
            // Ctrl+T(관행) · Ctrl+N(Golden "New Tab") — Shift 붙으면 Private Tab
            e.Handled = true;
            if (shift)
                OnMenuNewPrivateTab(sender, e);
            else
                _ = NewTabAsync(_profile);
        }
        else if (e.Key == Key.W && cmdOrCtrl && shift)
        {
            // Golden: Shift+Ctrl+W = Save Workspace
            e.Handled = true;
            OnMenuSaveWorkspace(sender, e);
        }
        else if ((e.Key == Key.W || e.Key == Key.F4) && cmdOrCtrl)
        {
            // Ctrl+W(관행) · Ctrl+F4(Golden "Close Tab")
            if (QueryTabs.SelectedItem is TabItem item && item.Content is QueryTabView view)
            {
                e.Handled = true;
                _ = CloseTabAsync(item, view);
            }
        }
        else if (e.Key == Key.Tab && cmdOrCtrl)
        {
            // Golden: Ctrl+Tab / Shift+Ctrl+Tab = 다음/이전 탭
            var count = _tabs.Count;
            if (count > 1)
            {
                e.Handled = true;
                QueryTabs.SelectedIndex = (QueryTabs.SelectedIndex + (shift ? count - 1 : 1)) % count;
            }
        }
        else if ((e.Key == Key.L || e.Key == Key.J) && cmdOrCtrl)
        {
            // Golden: Login = Ctrl+L or Ctrl+J
            e.Handled = true;
            _ = ShowLogonAsync();
        }
        else if (e.Key == Key.H && cmdOrCtrl)
        {
            // Golden: Ctrl+H = Replace
            e.Handled = true;
            ActiveView?.OpenReplace();
        }
        else if (e.Key == Key.R && cmdOrCtrl)
        {
            // Golden: Ctrl+R = Toggle Between Edit And Results
            e.Handled = true;
            ActiveView?.ToggleEditResultsFocus();
        }
        else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && cmdOrCtrl)
        {
            // Golden: Ctrl+- = Comment Out, Shift+Ctrl+- = Uncomment
            e.Handled = true;
            ActiveView?.CommentSelection(uncomment: shift);
        }
        else if (e.Key == Key.X && cmdOrCtrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            ActiveView?.ToggleTranspose();
        }
        else if (e.Key == Key.D && cmdOrCtrl)
        {
            e.Handled = true;
            OnMenuDescribe(sender, e);
        }
        else if (e.Key == Key.F8)
        {
            e.Handled = true;
            OnMenuToggleBrowser(sender, e);
        }
        else if (e.Key == Key.F11 && cmdOrCtrl)
        {
            // Golden 6: Cell Details Window = Ctrl+F11 (F11 단독은 우리 Run and Edit 별칭)
            e.Handled = true;
            OnMenuCellDetail(sender, e);
        }
        else if (e.Key == Key.F11)
        {
            e.Handled = true;
            OnMenuRunAndEdit(sender, e);
        }
        else if (e.Key == Key.F12)
        {
            // Golden 6 View 메뉴: Toggle DataGrid/Text View/Log View
            e.Handled = true;
            OnMenuCycleResultView(sender, e);
        }
        else if (e.Key == Key.G && cmdOrCtrl)
        {
            e.Handled = true;
            OnMenuGotoRecord(sender, e);
        }
        else if (e.Key == Key.P && cmdOrCtrl)
        {
            e.Handled = true;
            PrintSql(auto: true);
        }
        else if (e.Key == Key.S && cmdOrCtrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            OnMenuSubmitEdits(sender, e);
        }
        else if (e.Key == Key.Up && cmdOrCtrl)
        {
            e.Handled = true;
            ActiveView?.HistoryPrev();
        }
        else if (e.Key == Key.Down && cmdOrCtrl)
        {
            e.Handled = true;
            ActiveView?.HistoryNext();
        }
        else if (e.Key == Key.F && cmdOrCtrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            OnMenuAddFavorite(sender, e);
        }
        else if (e.Key == Key.O && cmdOrCtrl)
        {
            e.Handled = true;
            OnMenuOpen(sender, e);
        }
        else if (e.Key == Key.S && cmdOrCtrl)
        {
            e.Handled = true;
            OnMenuSave(sender, e);
        }
    }

    private async void OnMenuExecute(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.ExecuteAtCaretAsync();
    }

    private async void OnMenuRunScript(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.RunScriptAsync();
    }

    private async void OnMenuRunSelected(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.ExecuteSelectedAsync();
    }

    private async void OnMenuExplain(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.ExecuteExplainAsync(analyze: false);
    }

    private async void OnMenuExplainAnalyze(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.ExecuteExplainAsync(analyze: true);
    }

    private void OnMenuCancel(object? sender, RoutedEventArgs e) => ActiveView?.Cancel();

    private void OnMenuFind(object? sender, RoutedEventArgs e) => ActiveView?.OpenSearch();

    private async void OnMenuBindVars(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.EditBindVariablesAsync();
    }

    private async void OnMenuOptions(object? sender, RoutedEventArgs e)
    {
        var dialog = new OptionsDialog(_options);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } options)
        {
            _options = options;
            foreach (var view in AllViews())
                view.Options = options;
            StatusLabel.Text = "Options saved";
        }
    }

    // ---------- Print / Print Preview (Golden) ----------

    private void OnMenuPrintSql(object? sender, RoutedEventArgs e) => PrintSql(auto: true);
    private void OnMenuPreviewSql(object? sender, RoutedEventArgs e) => PrintSql(auto: false);
    private void OnMenuPrintGrid(object? sender, RoutedEventArgs e) => PrintGrid(auto: true);
    private void OnMenuPreviewGrid(object? sender, RoutedEventArgs e) => PrintGrid(auto: false);

    private void PrintSql(bool auto)
    {
        if (ActiveView is not { } view)
            return;
        var sql = view.GetSql();
        if (sql.Trim().Length == 0)
        {
            StatusLabel.Text = "인쇄할 SQL 이 없습니다";
            return;
        }
        var title = (QueryTabs.SelectedItem as TabItem)?.Header as string ?? "Query";
        OpenPrintPage(PrintRenderer.RenderSql(sql, title, ConnectionSubtitle(), DateTimeOffset.Now, auto), "sql");
    }

    private void PrintGrid(bool auto)
    {
        if (ActiveView is not { HasResult: true } view)
        {
            StatusLabel.Text = "인쇄할 결과가 없습니다";
            return;
        }
        var (columns, rows) = view.LoadedSnapshot();
        var title = (QueryTabs.SelectedItem as TabItem)?.Header as string ?? "Result";
        OpenPrintPage(
            PrintRenderer.RenderGrid(columns, rows, title, ConnectionSubtitle(), DateTimeOffset.Now, auto),
            "grid");
    }

    private string ConnectionSubtitle() => _profile?.DisplayName ?? "not connected";

    /// <summary>
    /// Avalonia 에 인쇄 API 가 없어 HTML 로 뽑아 OS 기본 브라우저에 넘긴다.
    /// 미리보기·프린터 선택은 브라우저 인쇄 대화상자가 맡는다.
    /// </summary>
    private void OpenPrintPage(string html, string kind)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aurum-print");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            System.IO.File.WriteAllText(path, html, new UTF8Encoding(true));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
            StatusLabel.Text = $"Print: 브라우저에서 열었습니다 ({System.IO.Path.GetFileName(path)})";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Print failed: {ex.Message}";
        }
    }

    // ---------- Run and Edit (Golden EditMode) ----------

    private async void OnMenuRunAndEdit(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.RunAndEditAsync();
    }

    private async void OnMenuSubmitEdits(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
        {
            await view.SubmitEditsAsync();
            UpdateTxState();
        }
    }

    private void OnMenuAddRow(object? sender, RoutedEventArgs e) => ActiveView?.AddInsertRow();

    /// <summary>Golden 의 "EditMode: Paste inserted %d records." — 클립보드 표를 새 행으로.</summary>
    private async void OnMenuPasteRows(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.PasteRowsAsync();
    }

    private async void OnMenuRevertEdits(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.RevertEditsAsync();
    }

    /// <summary>Golden 문구 그대로 "Delete N selected records?" 로 확인한다.</summary>
    private async void OnMenuDeleteRows(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { IsEditing: true } view)
        {
            StatusLabel.Text = "Run and Edit (F11) 로 편집 모드에 들어간 뒤 사용하세요";
            return;
        }
        var count = view.SelectedRowCount;
        if (count == 0)
        {
            StatusLabel.Text = "삭제할 행을 선택하세요";
            return;
        }
        if (await ConfirmAsync($"Delete {count} selected records?"))
            view.MarkSelectedRowsDeleted();
    }

    /// <summary>예/아니오 확인 창 — 그리드 삭제처럼 되돌리기 어려운 동작에만 쓴다.</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        var result = false;
        var yes = new Button { Content = "Yes", MinWidth = 80, MinHeight = 30, IsDefault = true };
        var no = new Button { Content = "No", MinWidth = 80, MinHeight = 30, IsCancel = true };
        var dialog = new Window
        {
            Title = "Aurum",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // Golden 처럼 작업표시줄에는 메인 창 하나만 — 부속 창은 창 선택 목록에 안 뜬다
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { no, yes },
                    },
                },
            },
        };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }

    // ---------- Favorites (Golden 의 즐겨찾기 메뉴) ----------

    /// <summary>고정 항목(Add·Manage·Separator) 뒤를 저장된 즐겨찾기로 다시 채운다.</summary>
    private void RebuildFavoritesMenu()
    {
        const int fixedItems = 3;
        var items = FavoritesMenu.Items;
        while (items.Count > fixedItems)
            items.RemoveAt(items.Count - 1);

        if (_favorites.Items.Count == 0)
        {
            items.Add(new MenuItem { Header = "(즐겨찾기 없음)", IsEnabled = false });
        }
        else
        {
            foreach (var favorite in _favorites.Items)
            {
                var item = new MenuItem { Header = favorite.Name };
                ToolTip.SetTip(item, favorite.Sql);
                var target = favorite;
                item.Click += (_, _) => _ = RunFavoriteAsync(target);
                items.Add(item);
            }
        }
        RebuildNativeFavoritesMenu(fixedItems);
    }

    /// <summary>macOS 네이티브 메뉴바에도 같은 목록을 반영한다 (인앱 메뉴가 숨겨져 있으므로).</summary>
    private void RebuildNativeFavoritesMenu(int fixedItems)
    {
        if (_nativeFavoritesMenu is not { } menu)
            return;
        while (menu.Items.Count > fixedItems)
            menu.Items.RemoveAt(menu.Items.Count - 1);
        foreach (var favorite in _favorites.Items)
        {
            var item = new NativeMenuItem(favorite.Name);
            var target = favorite;
            item.Click += (_, _) => _ = RunFavoriteAsync(target);
            menu.Items.Add(item);
        }
    }

    /// <summary>Golden: 메뉴에서 고른 즐겨찾기는 바로 실행된다. 기본은 SELECT 만 허용.</summary>
    private async Task RunFavoriteAsync(FavoriteQuery favorite) =>
        await RunFavoriteSqlAsync(favorite.Sql, favorite.Name);

    private async Task RunFavoriteSqlAsync(string sql, string label)
    {
        if (ActiveView is not { } view)
        {
            StatusLabel.Text = "실행할 탭이 없습니다 (Ctrl+T)";
            return;
        }
        if (!_options.AllowNonSelectFavorites && !FavoriteSql.IsSelectOnly(sql))
        {
            StatusLabel.Text = $"'{label}' 은 SELECT 문이 아닙니다 — Options 에서 허용할 수 있습니다";
            return;
        }
        await view.LoadAndRunAsync(sql);
    }

    /// <summary>Ctrl+Shift+F — 현재 문장(또는 선택 영역)을 이름 붙여 저장.</summary>
    private async void OnMenuAddFavorite(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view)
        {
            StatusLabel.Text = "즐겨찾기에 담을 탭이 없습니다 (Ctrl+T)";
            return;
        }
        var sql = view.StatementForFavorite();
        if (sql.Length == 0)
        {
            StatusLabel.Text = "즐겨찾기에 담을 SQL 이 없습니다";
            return;
        }
        await ShowFavoritesAsync(sql);
    }

    private async void OnMenuManageFavorites(object? sender, RoutedEventArgs e)
        => await ShowFavoritesAsync(null);

    private async Task ShowFavoritesAsync(string? seedSql)
    {
        var dialog = new FavoritesDialog(_favorites, seedSql);
        await dialog.ShowDialog(this);
        RebuildFavoritesMenu();

        if (dialog.RunSql is { } runSql)
        {
            await RunFavoriteSqlAsync(runSql, "선택한 즐겨찾기");
        }
        else if (dialog.InsertSql is { } insertSql)
        {
            ActiveView?.InsertAtCaret(insertSql);
            StatusLabel.Text = "즐겨찾기 SQL 을 에디터에 삽입했습니다";
        }
        else
        {
            StatusLabel.Text = $"Favorites: {_favorites.Items.Count} item(s)";
        }
    }

    // ---------- 워크스페이스 (Golden: 열린 탭 세트 저장/복원) ----------

    private static readonly FilePickerFileType WorkspaceFileType =
        new("IAP Workspace") { Patterns = ["*.iapws"] };

    private async void OnMenuSaveWorkspace(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Workspace",
            SuggestedFileName = "workspace.iapws",
            DefaultExtension = "iapws",
            FileTypeChoices = [WorkspaceFileType],
        });
        if (file is null) return;
        try
        {
            var workspace = new Workspace { Connection = _profile?.DisplayName };
            foreach (var item in _tabs)
            {
                if (item.Content is not QueryTabView view) continue;
                workspace.Tabs.Add(new WorkspaceTab
                {
                    Title = item.Header as string ?? "Query",
                    Sql = view.GetSql(),
                    IsPrivate = view.IsPrivateSession,
                });
            }
            workspace.Save(file.Path.LocalPath);
            StatusLabel.Text = $"Workspace saved: {file.Name} ({workspace.Tabs.Count} tabs)";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Workspace save failed: {ex.Message}";
        }
    }

    private async void OnMenuOpenWorkspace(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Workspace",
            FileTypeFilter = [WorkspaceFileType],
            AllowMultiple = false,
        });
        if (files.Count == 0) return;
        var workspace = Workspace.Load(files[0].Path.LocalPath);
        if (workspace is null)
        {
            StatusLabel.Text = "Workspace 파일을 읽지 못했습니다";
            return;
        }
        foreach (var item in _tabs.ToList())
        {
            if (item.Content is QueryTabView view)
                await CloseTabAsync(item, view);
        }
        foreach (var tab in workspace.Tabs)
            await NewTabAsync(_profile, tab.Title, tab.Sql, tab.IsPrivate && _profile is not null);
        StatusLabel.Text = $"Workspace loaded: {files[0].Name} ({workspace.Tabs.Count} tabs)" +
                           (workspace.Connection is { } c ? $" · saved with {c}" : "");
    }

    /// <summary>Tools > SQL Builder — 만든 SELECT 를 에디터에 넣는다 (실행은 사용자가).</summary>
    private async void OnMenuSqlBuilder(object? sender, RoutedEventArgs e)
    {
        if (_allTables.Count == 0)
            StatusLabel.Text = "SQL Builder: 로그온하면 테이블 목록이 채워집니다 (Ctrl+L)";

        var dialog = new SqlBuilderDialog(_allTables, _profile) { SchemaCache = _schemaCache };
        await dialog.ShowDialog(this);
        if (dialog.Result is not { Length: > 0 } sql)
            return;

        var view = ActiveView ?? await NewTabAsync(_profile);
        view.InsertAtCaret(sql);
        view.FocusEditor();
        StatusLabel.Text = "SQL Builder: 에디터에 삽입했습니다 (F9 로 실행)";
    }

    /// <summary>
    /// Tools > Diagram (ERD) — SQL Developer 의 relational model 대응. 읽기 전용.
    /// Object Browser 에서 테이블을 골라둔 상태면 그 테이블을 Focus 로 열어준다.
    /// </summary>
    private void OnMenuErd(object? sender, RoutedEventArgs e)
    {
        if (_profile is not { } profile)
        {
            StatusLabel.Text = "Diagram 은 로그온 후 사용할 수 있습니다 (Ctrl+L)";
            return;
        }

        var focus = ObjectsGrid.SelectedItem is ObjectRow row
            ? $"{row.Info.Schema}.{row.Info.Name}"
            : null;
        new ErdWindow(profile, focus).Show(this);
    }

    private void OnMenuSessionMonitor(object? sender, RoutedEventArgs e)
    {
        if (_profile is { } profile)
            new SessionMonitorWindow(profile).Show(this);
        else
            StatusLabel.Text = "Session Monitor 는 로그온 후 사용할 수 있습니다 (Ctrl+L)";
    }

    /// <summary>Tools > Schema Diff — 읽기 전용 비교 (기준 스냅샷/접속 ↔ 현재 접속).</summary>
    private void OnMenuSchemaDiff(object? sender, RoutedEventArgs e)
    {
        if (_profile is { } profile)
            new SchemaDiffWindow(profile).Show(this);
        else
            StatusLabel.Text = "Schema Diff 는 로그온 후 사용할 수 있습니다 (Ctrl+L)";
    }

    /// <summary>Tools > Import CSV/TSV — 파일을 테이블로 (전량 성공 아니면 전량 롤백).</summary>
    private void OnMenuImportCsv(object? sender, RoutedEventArgs e)
    {
        if (_profile is { } profile && _schemaCache is { } cache)
            new CsvImportDialog(profile, cache).Show(this);
        else
            StatusLabel.Text = "Import 는 로그온 후 사용할 수 있습니다 (Ctrl+L)";
    }

    private void OnMenuHistoryPrev(object? sender, RoutedEventArgs e) => ActiveView?.HistoryPrev();
    private void OnMenuHistoryNext(object? sender, RoutedEventArgs e) => ActiveView?.HistoryNext();

    /// <summary>Results > Pin — 현재 결과 스냅샷을 새 창에 (다음 쿼리와 나란히 비교).</summary>
    private void OnMenuPinResult(object? sender, RoutedEventArgs e)
    {
        if (ActiveView?.SnapshotResult() is { } snap)
            new PinnedResultWindow(snap.Sql ?? "results", snap.Columns, snap.Rows).Show(this);
        else
            StatusLabel.Text = "고정할 결과가 없습니다 (편집 모드에서는 고정할 수 없습니다)";
    }

    /// <summary>Tools > Query History — 검색해서 에디터에 삽입 (실행은 사용자가).</summary>
    private async void OnMenuHistory(object? sender, RoutedEventArgs e)
    {
        var dialog = new HistoryDialog();
        await dialog.ShowDialog(this);
        if (dialog.SelectedSql is { } sql && ActiveView is { } view)
            view.InsertAtCaret(sql);
    }

    private async void OnMenuCommit(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
        {
            await view.CommitAsync();
            UpdateTxState();
        }
    }

    private async void OnMenuRollback(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
        {
            await view.RollbackAsync();
            UpdateTxState();
        }
    }

    // ---------- Tx mode / Tx isolation (DataGrip 툴바) ----------

    /// <summary>DataGrip 의 Tx Isolation 목록·순서 그대로.</summary>
    private static readonly TransactionIsolation[] IsolationLevels =
    [
        TransactionIsolation.DatabaseDefault,
        TransactionIsolation.ReadUncommitted,
        TransactionIsolation.ReadCommitted,
        TransactionIsolation.RepeatableRead,
        TransactionIsolation.Serializable,
    ];

    /// <summary>
    /// DataGrip 의 Tx 드롭다운 — 한 팝업에 Transaction Mode(Auto·Manual)와 Tx Isolation 을 함께 둔다.
    /// 현재 값 앞에 ✓ 를 붙인다.
    /// </summary>
    private void OnTxButtonClick(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view)
            return;

        static MenuItem Section(string title) => new()
        {
            Header = title,
            IsEnabled = false,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        static string Mark(bool selected) => selected ? "✓ " : "     ";

        var flyout = new MenuFlyout();
        flyout.Items.Add(Section("Transaction Mode"));
        foreach (var auto in new[] { true, false })
        {
            var item = new MenuItem { Header = $"{Mark(view.AutoCommit == auto)}{(auto ? "Auto" : "Manual")}" };
            ToolTip.SetTip(item, auto
                ? "데이터베이스로 제출한 변경이 자동 커밋"
                : "Commit / Rollback 으로 직접 확정");
            var target = auto;
            item.Click += (_, _) => SetTxMode(view, target);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        flyout.Items.Add(Section("Tx Isolation"));
        foreach (var level in IsolationLevels)
        {
            var item = new MenuItem { Header = $"{Mark(view.Isolation == level)}{level.Display()}" };
            ToolTip.SetTip(item, level.Description());
            var target = level;
            item.Click += (_, _) => _ = SetIsolationAsync(view, target);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(TxButton);
    }

    /// <summary>Golden 의 결과 보기 드롭다운 — 그리드 / 고정폭 텍스트 / 실행 로그.</summary>
    private void OnShowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;

        var flyout = new MenuFlyout();
        foreach (var (mode, label) in ResultViewChoices)
        {
            var item = new MenuItem { Header = $"{(view.ResultView == mode ? "✓ " : "     ")}Show {label}" };
            var target = mode;
            item.Click += (_, _) => SetResultView(view, target);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(ShowButton);
    }

    private static readonly (ResultViewMode Mode, string Label)[] ResultViewChoices =
    [
        (ResultViewMode.Grid, "DataGrid"),
        (ResultViewMode.Text, "Text"),
        (ResultViewMode.Log, "Log"),
    ];

    private void SetResultView(QueryTabView view, ResultViewMode mode)
    {
        view.ResultView = mode;
        UpdateShowButton();
        StatusLabel.Text = $"Show → {Label(mode)}";
    }

    /// <summary>탭을 옮기면 그 탭의 보기 상태로 라벨을 맞춘다.</summary>
    private void UpdateShowButton()
    {
        if (ShowButton is null) return;
        ShowButton.Content = $"Show: {Label(ActiveView?.ResultView ?? ResultViewMode.Grid)} ▾";
    }

    private static string Label(ResultViewMode mode) =>
        ResultViewChoices.First(c => c.Mode == mode).Label;

    private void SetTxMode(QueryTabView view, bool auto)
    {
        view.AutoCommit = auto;
        StatusLabel.Text = auto
            ? "Tx Mode → Auto (문장마다 자동 커밋)"
            : "Tx Mode → Manual (Commit/Rollback 으로 확정)";
        UpdateTxState();
    }

    private async Task SetIsolationAsync(QueryTabView view, TransactionIsolation level)
    {
        _options.Isolation = level;      // 다음 세션 기본값으로 기억
        _options.Save();
        await view.SetIsolationAsync(level);
        UpdateTxState();
    }

    /// <summary>상태바의 접속 pill 갱신 — 미접속이면 빨강, 접속되면 초록.</summary>
    private void UpdateConnectionPill()
    {
        var connected = _profile is not null && _sharedSession?.IsAlive == true;
        ConnPill.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(connected ? "#D9EFD9" : "#F3D6D6"));
        ConnPill.BorderBrush = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(connected ? "#9CC79C" : "#D89A9A"));
        ConnDot.Fill = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(connected ? "#2E7D32" : "#C0392B"));
        ConnText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(connected ? "#1E5B20" : "#7B241C"));
        ConnText.Text = connected ? _profile!.DisplayName : "Disconnected";
        if (!connected)
            SchemaPill.IsVisible = false;
    }

    /// <summary>
    /// 상태바 스키마 버전 pill — PRISMONE.schema_version 을 읽어 마지막 적용 패치를 보여준다.
    /// 조회 전용(설계 원칙: 패치 적용은 iapdb CLI 배포 키트의 몫). PRISMONE DB 가 아니면 숨긴다.
    /// </summary>
    private async Task UpdateSchemaVersionPillAsync(ConnectionProfile profile)
    {
        try
        {
            await using var conn = await profile.OpenAsync();
            var info = await SchemaVersion.LoadAsync(conn);
            SchemaPill.IsVisible = info is not null;
            if (info is null)
                return;
            SchemaText.Text = $"Schema: {info.Label}";
            ToolTip.SetTip(SchemaPill, info.LatestVersionId is null
                ? "PRISMONE schema_version — 적용된 패치 기록 없음 (baseline)"
                : $"마지막 패치: {info.LatestVersionId}\n적용 시각: {info.AppliedAt:yyyy-MM-dd HH:mm:ss}\n적용된 패치 {info.AppliedCount}건 — 적용은 iapdb CLI 로");
        }
        catch
        {
            SchemaPill.IsVisible = false;   // 조회 실패가 접속 흐름을 막으면 안 된다
        }
    }

    /// <summary>Ctrl+D — 커서 위치 테이블을 브라우저 describe 로 보여준다 (Golden).</summary>
    private void OnMenuDescribe(object? sender, RoutedEventArgs e)
    {
        if (ActiveView?.WordAtCaret() is not { Length: > 0 } word)
        {
            StatusLabel.Text = "커서를 테이블 이름 위에 두고 Ctrl+D 를 누르세요";
            return;
        }
        var parts = word.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[^1];
        var schema = parts.Length > 1 ? parts[^2] : null;

        var match = _allTables.FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (schema is null || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));
        if (match is null)
        {
            StatusLabel.Text = $"'{word}' 테이블을 찾지 못했습니다";
            return;
        }

        if (!BrowserPanel.IsVisible)
            OnMenuToggleBrowser(this, e);
        SchemaCombo.SelectedItem = match.Schema;
        if (ShowCombo.SelectedItem as string != "All" && match.IsView)
            ShowCombo.SelectedItem = "Views";
        RefreshObjectList();
        if (ObjectsGrid.ItemsSource is IEnumerable<ObjectRow> rows)
        {
            var target = rows.FirstOrDefault(r => r.Info.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                ObjectsGrid.SelectedItem = target;
                ObjectsGrid.ScrollIntoView(target, null);
            }
        }
        StatusLabel.Text = $"Describe: {match.Schema}.{match.Name}";
    }

    /// <summary>활성 탭의 트랜잭션 상태를 툴바(Commit/Rollback/Auto)에 반영.</summary>
    private void UpdateTxState()
    {
        var view = ActiveView;
        var connected = view?.IsConnected == true;
        CommitButton.IsEnabled = connected && view!.InTransaction;
        RollbackButton.IsEnabled = connected && view!.InTransaction;
        TxButton.IsEnabled = connected;
        // 버튼 라벨은 DataGrip 의 action.tx.text ("Tx: {0}") 형식
        var mode = view?.AutoCommit == true ? "Auto" : "Manual";
        TxButton.Content = $"Tx: {mode} ▾";
        ToolTip.SetTip(TxButton, view is null
            ? "Transaction Mode / Tx Isolation"
            : $"Transaction Mode: {mode} · Tx Isolation: {view.Isolation.Display()}");
    }

    private void OnMenuTranspose(object? sender, RoutedEventArgs e) => ActiveView?.ToggleTranspose();
    private void OnMenuSizeColumns(object? sender, RoutedEventArgs e) => ActiveView?.SizeColumnsToFit();
    private void OnMenuFilterCell(object? sender, RoutedEventArgs e) => ActiveView?.FilterBySelectedCell();

    // Golden 파리티 (Golden 6 Results/View 메뉴 실물 확인 기준)
    private void OnMenuFilterCellGrid(object? sender, RoutedEventArgs e) => ActiveView?.FilterBySelectedCellInGrid();
    private void OnMenuClearFilter(object? sender, RoutedEventArgs e) => ActiveView?.ClearFilter();
    private void OnMenuClearResults(object? sender, RoutedEventArgs e) => ActiveView?.ClearResults();
    private void OnMenuCellDetail(object? sender, RoutedEventArgs e) => ActiveView?.ShowCellDetail();

    /// <summary>Golden F12 — DataGrid → Text → Log 순환. 툴바 라벨도 같이 맞춘다.</summary>
    private void OnMenuCycleResultView(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        view.CycleResultView();
        UpdateShowButton();
        StatusLabel.Text = $"Show → {Label(view.ResultView)}";
    }

    /// <summary>Golden "Goto Record Number" (Ctrl+G) — 행 번호를 물어보고 그 행으로 간다.</summary>
    private async void OnMenuGotoRecord(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;

        var input = new TextBox { Width = 160, PlaceholderText = "record number" };
        var ok = new Button { Content = "Go", IsDefault = true, MinWidth = 70 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 70 };
        var dialog = new Window
        {
            Title = "Goto Record Number",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel },
                    },
                },
            },
        };

        int? result = null;
        ok.Click += (_, _) =>
        {
            if (int.TryParse(input.Text?.Trim(), out var no)) result = no;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(this);

        if (result is { } record) view.GotoRecord(record);
    }

    private async void OnMenuExportTsv(object? sender, RoutedEventArgs e)
        => await SaveGridAsAsync(GridExportFormat.Tsv, "result.tsv", "tsv", "Tab-separated");

    private async void OnMenuExportInsert(object? sender, RoutedEventArgs e)
        => await SaveGridAsAsync(GridExportFormat.Insert, "result.sql", "sql", "SQL script");

    /// <summary>Golden 의 Save Grid As xlsx — 로드된 행을 Excel 통합문서로.</summary>
    private async void OnMenuExportXlsx(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { HasResult: true } view)
        {
            StatusLabel.Text = "No result to export";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Grid As xlsx",
            SuggestedFileName = "result.xlsx",
            DefaultExtension = "xlsx",
            FileTypeChoices = [new FilePickerFileType("Excel Workbook") { Patterns = ["*.xlsx"] }],
        });
        if (file is null) return;

        try
        {
            var (columns, rows) = view.LoadedSnapshot();
            await using var stream = await file.OpenWriteAsync();
            var written = XlsxExporter.Write(stream, columns, rows, TableNameForInsert(view));
            StatusLabel.Text = written < rows.Count
                ? $"Saved {written:N0} of {rows.Count:N0} record(s) to {file.Name} (Excel row limit)"
                : $"Saved {written:N0} loaded record(s) to {file.Name}";
            Toast.Show(this, "xlsx 저장 완료", $"{file.Name} — {written:N0}행");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>Golden 의 Save Grid As — 로드된 행을 TSV / INSERT 문으로.</summary>
    private async Task SaveGridAsAsync(GridExportFormat format, string suggested, string ext, string label)
    {
        if (ActiveView is not { HasResult: true } view)
        {
            StatusLabel.Text = "No result to export";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save Grid As {label}",
            SuggestedFileName = suggested,
            DefaultExtension = ext,
            FileTypeChoices = [new FilePickerFileType(label) { Patterns = [$"*.{ext}"] }],
        });
        if (file is null) return;

        try
        {
            var (columns, rows) = view.LoadedSnapshot();
            var table = TableNameForInsert(view);
            var text = GridExporter.Build(format, columns, rows, table);
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteAsync(text);
            StatusLabel.Text = $"Saved {rows.Count:N0} loaded record(s) to {file.Name}";
            Toast.Show(this, "저장 완료", $"{file.Name} — {rows.Count:N0}행");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>INSERT 문 대상 테이블 추정 — 마지막 쿼리의 FROM 절, 없으면 자리표시자.</summary>
    private static string TableNameForInsert(QueryTabView view)
    {
        var sql = view.LastGridSql ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(
            sql, @"\bfrom\s+([A-Za-z_][\w.]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "table_name";
    }

    private async void OnMenuFetchAll(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            await view.FetchAllAsync();
    }

    // ---------- Edit ----------

    private void OnMenuCut(object? sender, RoutedEventArgs e) => ActiveView?.EditorCut();
    private void OnMenuCopy(object? sender, RoutedEventArgs e) => ActiveView?.EditorCopy();
    private void OnMenuPaste(object? sender, RoutedEventArgs e) => ActiveView?.EditorPaste();
    private void OnMenuUndo(object? sender, RoutedEventArgs e) => ActiveView?.EditorUndo();
    private void OnMenuRedo(object? sender, RoutedEventArgs e) => ActiveView?.EditorRedo();

    // ---------- Script file open/save ----------

    private static readonly FilePickerFileType SqlFileType = new("SQL Script") { Patterns = ["*.sql"] };

    private async void OnMenuOpen(object? sender, RoutedEventArgs e)
    {
        if (_profile is not { } profile)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Script",
            FileTypeFilter = [SqlFileType],
            AllowMultiple = false,
        });
        if (files.Count == 0)
            return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new System.IO.StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            await NewTabAsync(profile, files[0].Name, sql);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Open failed: {ex.Message}";
        }
    }

    private async void OnMenuSave(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view)
            return;
        var suggested = (QueryTabs.SelectedItem as TabItem)?.Header as string ?? "script";
        if (!suggested.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            suggested += ".sql";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Script As",
            SuggestedFileName = suggested,
            DefaultExtension = "sql",
            FileTypeChoices = [SqlFileType],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream);
            await writer.WriteAsync(view.GetSql());
            StatusLabel.Text = $"Saved {file.Name}";
            Toast.Show(this, "저장 완료", file.Name);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save failed: {ex.Message}";
        }
    }

    // ---------- Export / misc ----------

    private async void OnMenuExport(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view || (view.LastGridSql is null && !view.HasResult))
        {
            StatusLabel.Text = "No result to export";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Grid As CSV",
            SuggestedFileName = "result.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;

        // 1차: COPY (query) TO STDOUT — 서버에서 전체 행을 원문 그대로 고속 스트리밍
        if (view.LastGridSql is { } sql && view.SessionProfile is { } profile)
        {
            try
            {
                await using var stream = await file.OpenWriteAsync();
                var chars = await CopyExporter.ExportCsvAsync(profile, sql, stream,
                    total => StatusLabel.Text = $"Exporting… {total / 1_000_000.0:0.0}M chars");
                StatusLabel.Text = $"Exported to {file.Name} via COPY ({chars:N0} chars, 전체 행·전문)";
                Toast.Show(this, "CSV 내보내기 완료", $"{file.Name} (COPY, 전체 행)");
                return;
            }
            catch (PostgresException ex)
            {
                // COPY 가 못 받는 문장(비 SELECT 등) → 로드된 행으로 폴백
                StatusLabel.Text = $"COPY export 불가({ex.SqlState}) — 로드된 행으로 내보냅니다";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Export failed: {ex.Message}";
                return;
            }
        }

        // 2차 폴백: 그리드에 로드된 행(표시용 500자 컷 적용) 그대로
        try
        {
            var (columns, rows) = view.Snapshot();
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteLineAsync(string.Join(",", columns.Select(CsvField)));
            foreach (var row in rows)
                await writer.WriteLineAsync(string.Join(",", row.Select(v => CsvField(v ?? ""))));
            StatusLabel.Text = $"Exported {rows.Count:N0} loaded record(s) to {file.Name}";
            Toast.Show(this, "CSV 내보내기 완료", $"{file.Name} — {rows.Count:N0}행");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Export failed: {ex.Message}";
        }
    }

    private static string CsvField(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private void OnMenuExit(object? sender, RoutedEventArgs e) => Close();

    // ---------- 자가 스크린샷 (IAPDM_SHOT_DIR) ----------

    /// <summary>실접속 재현: 로그인 → 브라우저 로드 → 쿼리 실행 → 끝까지 스크롤 → 캡처.</summary>
    private async Task CaptureLiveAsync(string dir, string conn)
    {
        try
        {
            var parts = conn.Split('|');
            var target = parts[0];
            var slash = target.IndexOf('/');
            var left = target[..slash];
            var db = target[(slash + 1)..];
            var colon = left.LastIndexOf(':');
            var host = colon < 0 ? left : left[..colon];
            var port = colon < 0 ? 5432 : int.Parse(left[(colon + 1)..]);
            var profile = new ConnectionProfile(host, port, db,
                parts.Length > 1 ? parts[1] : "postgres",
                parts.Length > 2 ? parts[2] : "");

            await ApplyProfileAsync(profile);
            await Task.Delay(600);
            SaveShot(this, System.IO.Path.Combine(dir, "live_after_login.png"));

            if (ActiveView is { } view)
            {
                // describe 진단: 브라우저 첫 테이블 선택 → describe 로드
                OnMenuToggleBrowser(this, new RoutedEventArgs());
                await Task.Delay(300);
                if (ObjectsGrid.ItemsSource is System.Collections.IEnumerable objs)
                {
                    foreach (var o in objs) { ObjectsGrid.SelectedItem = o; break; }
                }
                await Task.Delay(900);
                SaveShot(this, System.IO.Path.Combine(dir, "live_describe.png"));

                // 자동완성 팝업 캡처: "select * from " 뒤에서 목록 표시
                view.SetSql("select * from ");
                view.FocusEditor();
                await Task.Delay(200);
                await view.ShowCompletionForShotAsync();
                await Task.Delay(700);
                if (view.CompletionWindowForShot is { } popup && popup.Bounds.Width > 1)
                {
                    var size = new Avalonia.PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height);
                    using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
                    bmp.Render(popup);
                    bmp.Save(System.IO.Path.Combine(dir, "live_completion.png"));
                }
                else
                {
                    SaveShot(this, System.IO.Path.Combine(dir, "live_completion.png"));
                }

                var shotSql = Environment.GetEnvironmentVariable("IAPDM_SHOT_SQL")
                    ?? "select table_schema, table_name from information_schema.tables order by 1, 2;";
                view.SetSql(shotSql);
                await view.ExecuteAtCaretAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_query.png"));

                // 즐겨찾기 실행 경로 점검 — 메뉴에서 고른 것과 같은 경로(SELECT 게이트 + LoadAndRun)
                await RunFavoriteSqlAsync("select current_database(), now();", "shot favorite");
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_favorite.png"));

                // 즐겨찾기가 에디터를 덮어썼으므로 스크롤 캡처용 쿼리를 다시 돌린다
                view.SetSql(shotSql);
                await view.ExecuteAtCaretAsync();
                await Task.Delay(400);

                await view.ScrollToBottomAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_scrolled.png"));

                view.SetSql("select t.table_name, c.column_name from information_schema.tables t join information_schema.columns c on c.table_name = t.table_name where t.table_schema = 'prismone' order by 1, 2;");
                await view.ExecuteExplainAsync(analyze: true);
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_explain.png"));

                // Run and Edit 전체 경로 검증 — DB 에 쓰기가 발생하므로 옵트인(IAPDM_SHOT_RAE=1).
                // 검증용 임시 테이블을 만들어 그 위에서만 수정·추가·삭제한다.
                if (Environment.GetEnvironmentVariable("IAPDM_SHOT_RAE") == "1")
                    await VerifyRunAndEditAsync(dir, view);
            }
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "live_error.txt"), ex.ToString());
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Run and Edit 전체 경로(셀 편집 → Submit → 커밋 → 재조회) 실접속 검증.
    /// 핵심은 DataGrid 셀의 Cells[i] TwoWay 바인딩이 실제로 값을 되쓰는지 —
    /// EditCellForShotAsync 가 진짜 편집 경로(BeginEdit → TextBox → CommitEdit)를 탄다.
    /// 결과는 live_editmode_result.txt (PASS/FAIL 항목별) 로 남긴다.
    /// </summary>
    private async Task VerifyRunAndEditAsync(string dir, QueryTabView view)
    {
        const string table = "__iapdm_rae_verify";
        var log = new System.Text.StringBuilder();
        var pass = true;
        void Check(string name, bool ok)
        {
            pass &= ok;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        }

        var prevAuto = view.AutoCommit;
        view.AutoCommit = true;
        try
        {
            view.SetSql(
                $"drop table if exists {table};\n" +
                $"create table {table}(id int primary key, name text, note text);\n" +
                $"insert into {table} values (1, 'alpha', 'one'), (2, 'beta', 'two'), (3, 'gamma', 'three');");
            await view.RunScriptAsync();
            await Task.Delay(300);

            view.SetSql($"select * from {table} order by id");
            Check("Run and Edit 진입", await view.RunAndEditAsync());
            await Task.Delay(600);

            // 1) 셀 수정 — 실제 DataGrid 편집 경로 (컬럼: [0]=ctid, [1]=id, [2]=name, [3]=note)
            var editResult = await view.EditCellForShotAsync(0, 2, "alpha-edited");
            Check($"DataGrid 셀 편집 — Cells[i] TwoWay 바인딩 ({editResult})", editResult == "ok");
            SaveShot(this, System.IO.Path.Combine(dir, "live_editmode.png"));

            // 2) 행 추가 (id=4)
            view.AddInsertRow();
            view.SetCellForShot(3, 1, "4");
            view.SetCellForShot(3, 2, "delta");
            view.SetCellForShot(3, 3, "four");

            // 3) 행 삭제 (id=3)
            view.SelectRowForShot(2);
            Check("행 삭제 표시", view.MarkSelectedRowsDeleted() == 1);

            await view.SubmitEditsAsync();
            await Task.Delay(600);
            SaveShot(this, System.IO.Path.Combine(dir, "live_editmode_after.png"));

            // 편집 모드 밖의 새 SELECT 로 DB 최종 상태 확인
            view.SetSql($"select id, name, note from {table} order by id");
            await view.ExecuteAtCaretAsync();
            await Task.Delay(400);
            var (_, rows) = view.Snapshot();
            Check("행 수 3 (1 update + 1 insert + 1 delete 반영)", rows.Count == 3);
            Check("UPDATE 반영 — id=1 name=alpha-edited", rows.Count > 0 && rows[0][1] == "alpha-edited");
            Check("DELETE 반영 — id=3 없음", rows.All(r => r[0] != "3"));
            Check("INSERT 반영 — id=4 delta", rows.Any(r => r[0] == "4" && r[1] == "delta"));
        }
        catch (Exception ex)
        {
            pass = false;
            log.AppendLine("EXCEPTION  " + ex);
        }
        finally
        {
            try
            {
                view.SetSql($"drop table if exists {table};");
                await view.RunScriptAsync();
                await Task.Delay(300);
            }
            catch { /* 뒷정리 실패는 결과 파일로만 남긴다 */ }
            view.AutoCommit = prevAuto;
            log.Insert(0, (pass ? "PASS" : "FAIL") + " — Run and Edit live verification\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "live_editmode_result.txt"), log.ToString());
        }
    }

    private async Task CaptureUiAsync(string dir)
    {
        try
        {
            // 샘플 데이터로 화면 채우기 (브라우저 패널 포함)
            OnMenuToggleBrowser(this, new RoutedEventArgs());
            var view = new QueryTabView();
            _tabs.Add(new TabItem { Header = "Query 1", Content = view });
            _tabs.Add(new TabItem { Header = "study-search.sql", Content = new QueryTabView() });
            QueryTabs.SelectedIndex = 0;
            await Task.Delay(400);   // 에디터가 붙은 뒤에 채워야 TextView 가 라인을 만든다
            view.PopulateSample();

            _allTables =
            [
                new TableInfo("prismone", "study", false),
                new TableInfo("prismone", "series", false),
                new TableInfo("prismone", "sop_instance", false),
                new TableInfo("prismone", "patient", false),
                new TableInfo("prismone", "examlist", false),
                new TableInfo("prismone", "v_study_summary", true),
            ];
            // SQL 검증 밑줄도 오프라인으로 확인한다 — 샘플 카탈로그로 캐시를 채워둔다
            var sampleColumns = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal)
            {
                ["prismone.study"] =
                [
                    new(1, "study_key", "bigint", "no", "P1", ""),
                    new(2, "study_id", "varchar(64)", "no", "", ""),
                    new(3, "patient_key", "bigint", "no", "", "F1"),
                    new(4, "patient_id", "varchar(64)", "no", "", ""),
                    new(5, "study_dttm", "timestamp", "yes", "", ""),
                    new(6, "modality", "varchar(16)", "yes", "", ""),
                ],
            };
            var sampleCache = new SchemaCache(
                _ => Task.FromResult(new SchemaSnapshot(_allTables, sampleColumns)));
            await sampleCache.GetAsync();
            view.SchemaCache = sampleCache;

            SchemaCombo.ItemsSource = new[] { "prismone" };
            SchemaCombo.SelectedIndex = 0;
            RefreshObjectList();
            ObjectsGrid.SelectedIndex = 0;
            DescribeTitle.Text = "TABLE prismone.study";
            DescribeGrid.ItemsSource = new List<ColumnInfo>
            {
                new(1, "study_key", "bigint", "no", "P1", ""),
                new(2, "study_id", "varchar(64)", "no", "", ""),
                new(3, "patient_key", "bigint", "no", "", "F1"),
                new(4, "study_dttm", "timestamp", "yes", "", ""),
                new(5, "modality", "varchar(16)", "yes", "", ""),
            };

            StatusLabel.Text = "Done, ran 1 of 1 statements.";
            CaretLabel.Text = "4 : 17";
            RowsLabel.Text = "Fetched 8 records";
            TimeLabel.Text = "Script: 0.062s";
            foreach (var b in new[] { NewTabButton, ExecuteButton, RunScriptButton, ExplainButton, StopButton, FetchAllButton, ExportButton })
                b.IsEnabled = true;

            await Task.Delay(800);
            SaveShot(this, System.IO.Path.Combine(dir, "shot_main.png"));

            // 왼쪽 Database Explorer — 트리 템플릿이 실제로 그려지는지 확인용
            OnMenuToggleExplorer(this, new RoutedEventArgs());
            await Task.Delay(600);
            SaveShot(this, System.IO.Path.Combine(dir, "shot_explorer.png"));
            OnMenuToggleExplorer(this, new RoutedEventArgs());

            // Golden 의 결과 보기 전환 (Show Text) — 접속 없이도 렌더를 확인한다
            if (ActiveView is { } textView)
            {
                SetResultView(textView, ResultViewMode.Text);
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "shot_text.png"));
                // F12 경로로 한 번 더 돌려 Log 보기까지 확인 (Text → Log)
                OnMenuCycleResultView(this, new RoutedEventArgs());
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "shot_log.png"));
                SetResultView(textView, ResultViewMode.Grid);
            }

            // SQL 검증 — 없는 컬럼(study_dtm)·없는 테이블(stduy)에 물결 밑줄
            view.SetSql("""
                select s.study_key, s.study_dtm
                  from prismone.study s;

                select * from prismone.stduy;
                """);
            await Task.Delay(1200);   // 검증 타이머(0.6s) 경과 대기
            SaveShot(this, System.IO.Path.Combine(dir, "shot_validation.png"));

            // Explain Plan 시각화 — self 비중 막대 + 행수 예측 오차 배지
            var samplePlan = PlanParser.Parse("""
                [{
                  "Plan": {
                    "Node Type": "Nested Loop", "Join Type": "Inner",
                    "Startup Cost": 0.4, "Total Cost": 1650.2, "Plan Rows": 120,
                    "Actual Total Time": 48.2, "Actual Rows": 118, "Actual Loops": 1,
                    "Plans": [
                      { "Node Type": "Seq Scan", "Relation Name": "study", "Alias": "s",
                        "Startup Cost": 0, "Total Cost": 1520.0, "Plan Rows": 40,
                        "Actual Total Time": 41.3, "Actual Rows": 4183, "Actual Loops": 1,
                        "Filter": "(study_dttm >= '2026-07-01'::timestamp)",
                        "Rows Removed by Filter": 95817 },
                      { "Node Type": "Index Scan", "Relation Name": "examlist", "Alias": "e",
                        "Index Name": "pk_examlist",
                        "Startup Cost": 0.4, "Total Cost": 3.2, "Plan Rows": 1,
                        "Actual Total Time": 0.001, "Actual Rows": 1, "Actual Loops": 4183,
                        "Index Cond": "(study_key = s.study_key)" }
                    ]
                  },
                  "Planning Time": 0.42,
                  "Execution Time": 48.9
                }]
                """);
            view.BindPlanTree(samplePlan!, analyze: true);
            await Task.Delay(500);
            SaveShot(this, System.IO.Path.Combine(dir, "shot_plan.png"));

            // Schema Diff — 합성 기준/대상 그래프로 렌더 확인 (접속 없이)
            var diffBaseline = new ErdGraph(
                [
                    new ErdTable("prismone", "study", false,
                    [
                        new ErdColumn("study_key", "bigint", true, true, false),
                        new ErdColumn("study_dttm", "timestamp", false, false, false),
                        new ErdColumn("audit_yn", "char(1)", true, false, false),
                    ]),
                    new ErdTable("prismone", "study_note", false,
                        [new ErdColumn("note_key", "bigint", true, true, false)]),
                ],
                [new ErdRelation("fk_note_study", "prismone.study_note", ["study_key"],
                    "prismone.study", ["study_key"], false, false)]);
            var diffTarget = new ErdGraph(
                [
                    new ErdTable("prismone", "study", false,
                    [
                        new ErdColumn("study_key", "bigint", true, true, false),
                        new ErdColumn("study_dttm", "timestamptz", true, false, false),
                    ]),
                    new ErdTable("prismone", "scratch_tmp", false,
                        [new ErdColumn("id", "integer", false, false, false)]),
                ],
                []);
            var diffWin = new SchemaDiffWindow();
            diffWin.Show(this);
            diffWin.BindResult(SchemaDiff.Compare(diffBaseline, diffTarget));
            await Task.Delay(500);
            SaveShot(diffWin, System.IO.Path.Combine(dir, "shot_diff.png"));
            diffWin.Close();

            // CSV Import — 샘플 파일로 매핑·미리보기 렌더 확인 (접속 없이)
            var importWin = new CsvImportDialog(ConnectionProfile.Default, sampleCache);
            importWin.Show(this);
            await Task.Delay(400);   // 테이블 목록 적재 대기
            importWin.LoadText("study_batch.csv",
                "study_key,study_id,study_dttm,modality,exam_note\n" +
                "2001,ST20260804-0001,2026-08-04 09:10:00,CT,follow-up\n" +
                "2002,ST20260804-0002,2026-08-04 09:25:00,MR,\n" +
                "2003,\"ST20260804,0003\",2026-08-04 10:02:00,US,\"quoted, note\"\n");
            importWin.TableCombo.SelectedIndex = 0;
            await Task.Delay(500);
            SaveShot(importWin, System.IO.Path.Combine(dir, "shot_import.png"));
            importWin.Close();

            // Query History — 가짜 항목으로 렌더 확인 (실제 히스토리 파일을 읽지 않는다)
            var historyWin = new HistoryDialog(
            [
                new HistoryEntry("select * from prismone.study where study_dttm >= '2026-07-01' order by study_dttm desc",
                    new DateTime(2026, 8, 3, 14, 22, 5)),
                new HistoryEntry("update prismone.examlist set status = 'DONE' where exam_key = 1234",
                    new DateTime(2026, 8, 3, 15, 2, 41)),
                new HistoryEntry("select e.exam_key, e.status from prismone.examlist e join prismone.study s on s.study_key = e.study_key",
                    new DateTime(2026, 8, 4, 9, 12, 0)),
            ]);
            historyWin.Show(this);
            await Task.Delay(400);
            SaveShot(historyWin, System.IO.Path.Combine(dir, "shot_history.png"));
            historyWin.Close();

            // Pin Results — 샘플 그리드 스냅샷을 새 창에
            if (ActiveView?.SnapshotResult() is { } pinSnap)
            {
                var pin = new PinnedResultWindow(pinSnap.Sql ?? "study search", pinSnap.Columns, pinSnap.Rows);
                pin.Show(this);
                await Task.Delay(500);
                SaveShot(pin, System.IO.Path.Combine(dir, "shot_pin.png"));
                pin.Close();
            }

            var dialog = new ConnectDialog();
            dialog.Show(this);
            await Task.Delay(500);
            SaveShot(dialog, System.IO.Path.Combine(dir, "shot_login.png"));
            dialog.ShowFilterForShot();
            await Task.Delay(300);
            SaveShot(dialog, System.IO.Path.Combine(dir, "shot_login_filter.png"));
            dialog.Close();

            var builder = new SqlBuilderDialog(_allTables, null);
            builder.Show(this);
            await Task.Delay(500);
            SaveShot(builder, System.IO.Path.Combine(dir, "shot_sqlbuilder.png"));
            builder.Close();

            var favorites = new FavoritesDialog(_favorites,
                "select * from prismone.study where study_dttm > now() - interval '7 days';");
            favorites.Show(this);
            await Task.Delay(500);
            SaveShot(favorites, System.IO.Path.Combine(dir, "shot_favorites.png"));
            favorites.Close();

            var erd = new ErdWindow(SampleErdGraph());
            erd.Show(this);
            await Task.Delay(700);
            SaveShot(erd, System.IO.Path.Combine(dir, "shot_erd.png"));
            erd.Close();
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>스크린샷용 합성 스키마 — 접속 없이 ERD 렌더를 눈으로 확인하기 위한 가짜 데이터.</summary>
    private static ErdGraph SampleErdGraph()
    {
        static ErdColumn Pk(string name) => new(name, "bigint", true, IsPk: true, IsFk: false);
        static ErdColumn Fk(string name) => new(name, "bigint", true, IsPk: false, IsFk: true);
        static ErdColumn Col(string name, string type, bool notNull = true) =>
            new(name, type, notNull, IsPk: false, IsFk: false);
        static ErdRelation Rel(string child, string parent, string column, bool optional = false) =>
            new($"fk_{child}_{parent}", $"public.{child}", [column], $"public.{parent}", [column],
                ChildUnique: false, ChildOptional: optional);

        var tables = new List<ErdTable>
        {
            // Dicom Image 도메인
            new("public", "patient", false, [Pk("patient_key"), Col("patient_id", "varchar(64)")]),
            new("public", "study", false,
                [Pk("study_key"), Fk("patient_key"), Col("study_dttm", "timestamp", notNull: false)]),
            new("public", "series", false, [Pk("series_key"), Fk("study_key"), Col("modality", "varchar(16)")]),
            new("public", "image", false, [Pk("image_key"), Fk("series_key"), Col("sop_uid", "varchar(128)")]),
            new("public", "study_note", false, [Pk("note_key"), Fk("study_key"), Col("body", "text", false)]),
            // Interface 도메인
            new("public", "interface_msg", false,
                [Pk("msg_key"), Col("msg_type", "varchar(16)"), Col("payload", "text", false)]),
            new("public", "interface_log", false, [Pk("log_key"), Fk("msg_key"), Col("result", "varchar(16)")]),
            new("public", "interface_queue", false, [Pk("queue_key"), Fk("msg_key"), Col("retry_cnt", "int")]),
            // Routing 도메인
            new("public", "router", false, [Pk("router_key"), Col("router_name", "varchar(64)")]),
            new("public", "routing_rule", false,
                [Pk("rule_key"), Fk("router_key"), Col("priority", "int"), Col("expr", "text", false)]),
            // Archive 도메인
            new("public", "archive_job", false, [Pk("job_key"), Col("state", "varchar(16)")]),
            new("public", "archive_target", false, [Pk("target_key"), Fk("job_key"), Col("path", "text")]),
            // User Management 도메인
            new("public", "app_user", false, [Pk("user_key"), Col("login_id", "varchar(64)")]),
            new("public", "user_role", false, [Pk("user_key"), Fk("role_key")]),
            new("public", "role_perm", false, [Pk("role_key"), Pk("perm_code"), Col("granted", "boolean")]),
            // 그 밖
            new("public", "folder", false, [Pk("folder_key"), Fk("parent_key"), Col("name", "text")]),
            new("public", "v_study_summary", true, [Col("study_key", "bigint"), Col("series_cnt", "bigint")]),
        };
        var relations = new List<ErdRelation>
        {
            Rel("study", "patient", "patient_key"),
            Rel("series", "study", "study_key"),
            Rel("image", "series", "series_key"),
            Rel("study_note", "study", "study_key", optional: true),
            Rel("interface_log", "interface_msg", "msg_key"),
            Rel("interface_queue", "interface_msg", "msg_key"),
            Rel("routing_rule", "router", "router_key"),
            Rel("archive_target", "archive_job", "job_key"),
            Rel("user_role", "app_user", "user_key"),
            Rel("user_role", "role_perm", "role_key"),
            Rel("folder", "folder", "parent_key", optional: true),
        };
        return new ErdGraph(tables, relations);
    }

    /// <summary>앱 아이콘: Aurum(Au, 금) — 주기율표 타일. 다크 배경 + 금 그라데이션 "Au" + 원자번호 79.</summary>
    private static void RenderAppIcon(string path)
    {
        var background = Avalonia.Media.Color.Parse("#17130C");
        var canvas = new Canvas { Width = 512, Height = 512 };

        // 금 그라데이션 (밝은 금 → 진한 금, 대각선)
        var gold = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#F7DE8B"), 0.0),
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#E4B54A"), 0.55),
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#B9821F"), 1.0),
            },
        };

        // 타일: 다크 배경 + 가는 금 테두리 (주기율표 원소 칸)
        canvas.Children.Add(new Border
        {
            Width = 512, Height = 512,
            CornerRadius = new Avalonia.CornerRadius(112),
            Background = new Avalonia.Media.SolidColorBrush(background),
        });
        canvas.Children.Add(new Border
        {
            Width = 512 - 2 * 26, Height = 512 - 2 * 26,
            [Canvas.LeftProperty] = 26.0,
            [Canvas.TopProperty] = 26.0,
            CornerRadius = new Avalonia.CornerRadius(88),
            BorderBrush = gold,
            BorderThickness = new Avalonia.Thickness(7),
        });

        var inter = new Avalonia.Media.FontFamily("fonts:Inter#Inter");

        // 원자번호 79 — 타일 좌상단
        canvas.Children.Add(new TextBlock
        {
            Text = "79",
            FontFamily = inter,
            FontSize = 76,
            FontWeight = Avalonia.Media.FontWeight.Medium,
            Foreground = gold,
            [Canvas.LeftProperty] = 78.0,
            [Canvas.TopProperty] = 62.0,
        });

        // 원소기호 Au — 중앙보다 살짝 아래 (주기율표 배치)
        canvas.Children.Add(new TextBlock
        {
            Text = "Au",
            FontFamily = inter,
            FontSize = 252,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = gold,
            Width = 512,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            [Canvas.LeftProperty] = 0.0,
            [Canvas.TopProperty] = 148.0,
        });

        canvas.Measure(new Avalonia.Size(512, 512));
        canvas.Arrange(new Avalonia.Rect(0, 0, 512, 512));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                   new Avalonia.PixelSize(512, 512), new Avalonia.Vector(96, 96)))
        {
            bitmap.Render(canvas);
            bitmap.Save(path);
        }
        // .icns 최상위(512@2x)용 1024px — 같은 512 좌표계를 2배 DPI 로 렌더
        using (var bitmap2x = new Avalonia.Media.Imaging.RenderTargetBitmap(
                   new Avalonia.PixelSize(1024, 1024), new Avalonia.Vector(192, 192)))
        {
            bitmap2x.Render(canvas);
            bitmap2x.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "icon_1024.png"));
        }
    }

    private static void SaveShot(Window window, string path)
    {
        var size = new Avalonia.PixelSize(
            Math.Max(1, (int)window.Bounds.Width),
            Math.Max(1, (int)window.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
    }

    private async void OnMenuAbout(object? sender, RoutedEventArgs e)
    {
        var about = new Window
        {
            Title = "About Aurum",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Aurum", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = "Golden 스타일 PostgreSQL 쿼리 툴 (IAP/PrismOne)" },
                    new TextBlock
                    {
                        Text = "F9: Run Statement · F5: Run Script · Ctrl+End: Fetch All\nCtrl+L: Logon · Ctrl+T: New Tab · Ctrl+W: Close Tab",
                        FontSize = 12,
                        Opacity = 0.7,
                    },
                },
            },
        };
        await about.ShowDialog(this);
    }
}
