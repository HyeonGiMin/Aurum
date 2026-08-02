using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Npgsql;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>오른쪽 스키마 패널의 오브젝트 목록 한 행.</summary>
public sealed record ObjectRow(string Name, string Type, TableInfo Info);

public partial class MainWindow : Window
{
    private ConnectionProfile? _profile;
    private QuerySession? _sharedSession;   // Golden: 탭들이 공유하는 메인 접속
    private readonly ObservableCollection<TabItem> _tabs = [];
    private int _tabCounter;
    private List<TableInfo> _allTables = [];
    private AppOptions _options = AppOptions.Load();

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
        // Golden: 메인 창이 먼저 뜨고(빈 Query1 탭 포함), 로그온은 Ctrl+L 로 연다
        BuildNativeMenu();
        if (Environment.GetEnvironmentVariable("IAPDM_SHOT_DIR") is null)
            Opened += async (_, _) =>
            {
                if (_tabs.Count == 0)
                    await NewTabAsync(null);
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
        Title = $"{profile.DisplayName} - IAP Database Manager";
        StatusLabel.Text = $"Connected: {profile.DisplayName}";

        await LoadBrowserAsync(profile);
        foreach (var v in AllViews())
            v.CompletionTables = _allTables;   // 자동완성 카탈로그 갱신

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
            return;
        }

        UpdateConnectionPill();

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
            Item("Commit", () => OnMenuCommit(this, args)),
            Item("Rollback", () => OnMenuRollback(this, args))));
        root.Items.Add(Sub("Results",
            Item("Fetch All Records (⌘End)", () => OnMenuFetchAll(this, args)),
            new NativeMenuItemSeparator(),
            Item("Transpose Columns/Records (⇧⌘X)", () => OnMenuTranspose(this, args)),
            Item("Size All Columns to Fit", () => OnMenuSizeColumns(this, args)),
            Item("Filter Like Selected Cell", () => OnMenuFilterCell(this, args)),
            new NativeMenuItemSeparator(),
            Item("Export All Rows As CSV… (COPY)", () => OnMenuExport(this, args)),
            Item("Save Grid As TSV…", () => OnMenuExportTsv(this, args)),
            Item("Save Grid As INSERT…", () => OnMenuExportInsert(this, args))));
        root.Items.Add(Sub("View",
            Item("Object Browser (F8)", () => OnMenuToggleBrowser(this, args))));
        root.Items.Add(Sub("Tools",
            Item("Logon… (⌘L)", () => _ = ShowLogonAsync()),
            Item("Session Monitor…", () => OnMenuSessionMonitor(this, args)),
            Item("Options…", () => OnMenuOptions(this, args))));
        root.Items.Add(Sub("Help",
            Item("About IAP Database Manager", () => OnMenuAbout(this, args))));
        NativeMenu.SetMenu(this, root);
    }

    /// <summary>View > Object Browser — Golden 6 기본은 패널 없음, 필요할 때만 켠다.</summary>
    private void OnMenuToggleBrowser(object? sender, RoutedEventArgs e)
    {
        var show = !BrowserPanel.IsVisible;
        BrowserPanel.IsVisible = show;
        BrowserSplitter.IsVisible = show;
        MainGrid.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        MainGrid.ColumnDefinitions[2].Width = new GridLength(show ? 352 : 0);
        BrowserMenuItem.Header = show ? "Object Browser ✓" : "Object Browser";
        BrowserToggleButton.Classes.Set("active", show);
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
            UpdateTxState();
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

    private void OnTabCaretChanged(QueryTabView view, int line, int col)
    {
        if (ReferenceEquals(view, ActiveView))
            CaretLabel.Text = $"{line} : {col}";
    }

    // ---------- Schema browser (오른쪽 패널) ----------

    private async Task LoadBrowserAsync(ConnectionProfile profile)
    {
        try
        {
            await using var conn = await profile.OpenAsync();
            _allTables = await SchemaCatalog.GetTablesAsync(conn);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Schema load failed: {ex.Message}";
            return;
        }

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
        if (ObjectsGrid.SelectedItem is not ObjectRow row || _profile is null)
            return;
        DescribeTitle.Text = $"{row.Type.ToUpperInvariant()} {row.Info.Schema}.{row.Info.Name}";
        try
        {
            await using var conn = await _profile.OpenAsync();
            DescribeGrid.ItemsSource = await SchemaCatalog.GetColumnsAsync(conn, row.Info);
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
        if (e.Key == Key.F9 || (e.Key == Key.Enter && cmdOrCtrl))
        {
            e.Handled = true;
            _ = ActiveView?.ExecuteAtCaretAsync();
        }
        else if (e.Key == Key.F5 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            // Golden: F5/Shift+Enter = Run Script (커서부터 끝까지)
            e.Handled = true;
            _ = ActiveView?.RunScriptAsync();
        }
        else if (e.Key == Key.End && cmdOrCtrl)
        {
            if (ActiveView is { } view)
            {
                e.Handled = true;
                _ = view.FetchAllAsync();
            }
        }
        else if (e.Key == Key.T && cmdOrCtrl)
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                OnMenuNewPrivateTab(sender, e);
            else
                _ = NewTabAsync(_profile);
        }
        else if (e.Key == Key.W && cmdOrCtrl)
        {
            if (QueryTabs.SelectedItem is TabItem item && item.Content is QueryTabView view)
            {
                e.Handled = true;
                _ = CloseTabAsync(item, view);
            }
        }
        else if (e.Key == Key.L && cmdOrCtrl)
        {
            e.Handled = true;
            _ = ShowLogonAsync();
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

    private void OnMenuSessionMonitor(object? sender, RoutedEventArgs e)
    {
        if (_profile is { } profile)
            new SessionMonitorWindow(profile).Show(this);
        else
            StatusLabel.Text = "Session Monitor 는 로그온 후 사용할 수 있습니다 (Ctrl+L)";
    }

    private void OnMenuHistoryPrev(object? sender, RoutedEventArgs e) => ActiveView?.HistoryPrev();
    private void OnMenuHistoryNext(object? sender, RoutedEventArgs e) => ActiveView?.HistoryNext();

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

    private void OnAutoCommitChanged(object? sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
            view.AutoCommit = AutoCommitBox.IsChecked == true;
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
        AutoCommitBox.IsEnabled = connected;
        if (view is not null)
            AutoCommitBox.IsChecked = view.AutoCommit;
    }

    private void OnMenuTranspose(object? sender, RoutedEventArgs e) => ActiveView?.ToggleTranspose();
    private void OnMenuSizeColumns(object? sender, RoutedEventArgs e) => ActiveView?.SizeColumnsToFit();
    private void OnMenuFilterCell(object? sender, RoutedEventArgs e) => ActiveView?.FilterBySelectedCell();

    private async void OnMenuExportTsv(object? sender, RoutedEventArgs e)
        => await SaveGridAsAsync(GridExportFormat.Tsv, "result.tsv", "tsv", "Tab-separated");

    private async void OnMenuExportInsert(object? sender, RoutedEventArgs e)
        => await SaveGridAsAsync(GridExportFormat.Insert, "result.sql", "sql", "SQL script");

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

                view.SetSql(Environment.GetEnvironmentVariable("IAPDM_SHOT_SQL")
                    ?? "select table_schema, table_name from information_schema.tables order by 1, 2;");
                await view.ExecuteAtCaretAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_query.png"));

                await view.ScrollToBottomAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_scrolled.png"));

                view.SetSql("select t.table_name, c.column_name from information_schema.tables t join information_schema.columns c on c.table_name = t.table_name where t.table_schema = 'prismone' order by 1, 2;");
                await view.ExecuteExplainAsync(analyze: true);
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_explain.png"));
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

            var dialog = new ConnectDialog();
            dialog.Show(this);
            await Task.Delay(500);
            SaveShot(dialog, System.IO.Path.Combine(dir, "shot_login.png"));
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>앱 아이콘: IAP 로고 기반 — 검은 라운드 배경 + 파랑→보라 그라데이션 IAP 레터마크.</summary>
    private static void RenderAppIcon(string path)
    {
        var background = Avalonia.Media.Color.Parse("#0A0C10");
        var canvas = new Canvas { Width = 512, Height = 512 };
        canvas.Children.Add(new Border
        {
            Width = 512, Height = 512,
            CornerRadius = new Avalonia.CornerRadius(112),
            Background = new Avalonia.Media.SolidColorBrush(background),
        });

        // 로고의 좌→우 그라데이션 (하늘색 → 파랑 → 보라)
        var gradient = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#55B7F6"), 0.0),
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#3E6CEA"), 0.5),
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#6C3BE9"), 1.0),
            },
        };

        // I + A(counter 포함) + P(counter·왼발 포함) — 한 지오메트리(even-odd)라 그라데이션이 이어진다
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(
                // I
                "M48,184 L90,184 L90,328 L48,328 Z " +
                // A 외곽 + 삼각 카운터
                "M120,328 L178,184 L210,184 L268,328 Z " +
                "M194,236 L232,328 L156,328 Z " +
                // P 외곽(넓고 얕은 볼, 스템은 베이스라인까지) + 둥근 카운터
                "M300,184 L398,184 A66,54 0 0 1 464,238 A66,54 0 0 1 398,292 L342,292 L342,328 L300,328 Z " +
                "M342,222 L398,222 A24,16 0 0 1 422,238 A24,16 0 0 1 398,254 L342,254 Z"),
            Fill = gradient,
        });

        // A 의 슬래시 컷 — 왼쪽 다리는 남기고 안쪽만 비스듬히 잘라낸다
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M170,334 L228,270 L246,292 L188,356 Z"),
            Fill = new Avalonia.Media.SolidColorBrush(background),
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
            Title = "About IAP Database Manager",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "IAP Database Manager", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
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
