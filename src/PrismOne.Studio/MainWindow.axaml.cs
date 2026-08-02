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
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>오른쪽 스키마 패널의 오브젝트 목록 한 행.</summary>
public sealed record ObjectRow(string Name, string Type, TableInfo Info);

public partial class MainWindow : Window
{
    private ConnectionProfile? _profile;
    private readonly ObservableCollection<TabItem> _tabs = [];
    private int _tabCounter;
    private List<TableInfo> _allTables = [];

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
        };
        // Golden: 메인 창이 먼저 뜨고, 로그온은 Ctrl+L 로 연다

        // 자가 스크린샷 모드 (UI 점검용): IAPDM_SHOT_DIR=<dir> 로 실행하면
        // 샘플 데이터로 채운 화면을 PNG 로 저장하고 종료한다.
        // IAPDM_SHOT_CONN="host:port/db|user|pass" 를 함께 주면 실제 접속·쿼리·스크롤까지 재현한다.
        if (Environment.GetEnvironmentVariable("IAPDM_SHOT_DIR") is { Length: > 0 } shotDir)
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
        StopButton.IsEnabled = true;
        FetchAllButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        // Golden 타이틀 형식: user@db - Benthic Software: Golden7
        Title = $"{profile.DisplayName} - IAP Database Manager";
        StatusLabel.Text = $"Connected: {profile.DisplayName}";

        await LoadBrowserAsync(profile);
        await NewTabAsync(profile);
    }

    // ---------- Tabs ----------

    private async void OnMenuNewTab(object? sender, RoutedEventArgs e)
    {
        if (_profile is { } profile)
            await NewTabAsync(profile);
    }

    private async void OnMenuCloseTab(object? sender, RoutedEventArgs e)
    {
        if (QueryTabs.SelectedItem is TabItem item && item.Content is QueryTabView view)
            await CloseTabAsync(item, view);
    }

    private async Task<QueryTabView> NewTabAsync(ConnectionProfile profile, string? title = null, string? sql = null)
    {
        var view = new QueryTabView();
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
        await view.ConnectAsync(profile);
        view.FocusEditor();
        return view;
    }

    private async Task CloseTabAsync(TabItem item, QueryTabView view)
    {
        _tabs.Remove(item);
        await view.CloseSessionAsync();
    }

    /// <summary>View > Object Browser — Golden 6 기본은 패널 없음, 필요할 때만 켠다.</summary>
    private void OnMenuToggleBrowser(object? sender, RoutedEventArgs e)
    {
        var show = !BrowserPanel.IsVisible;
        BrowserPanel.IsVisible = show;
        BrowserSplitter.IsVisible = show;
        MainGrid.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        MainGrid.ColumnDefinitions[2].Width = new GridLength(show ? 330 : 0);
        BrowserMenuItem.Header = show ? "Object Browser ✓" : "Object Browser";
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
            StatusLabel.Text = view.InfoMessage;
            RowsLabel.Text = view.InfoRows;
            TimeLabel.Text = view.InfoTime;
        }
    }

    private void OnTabInfoChanged(QueryTabView view)
    {
        if (!ReferenceEquals(view, ActiveView)) return;
        StatusLabel.Text = view.InfoMessage;
        RowsLabel.Text = view.InfoRows;
        TimeLabel.Text = view.InfoTime;
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
        RefreshObjectList();
    }

    private void OnBrowserFilterChanged(object? sender, RoutedEventArgs e) => RefreshObjectList();

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
            if (_profile is { } profile)
            {
                e.Handled = true;
                _ = NewTabAsync(profile);
            }
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
        else if (e.Key == Key.F8)
        {
            e.Handled = true;
            OnMenuToggleBrowser(sender, e);
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
            await view.ExecuteAtCaretAsync(explain: true);
    }

    private void OnMenuCancel(object? sender, RoutedEventArgs e) => ActiveView?.Cancel();

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
        if (ActiveView is not { HasResult: true } view)
        {
            StatusLabel.Text = "No result to export";
            return;
        }
        var (columns, rows) = view.Snapshot();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Grid As CSV",
            SuggestedFileName = "result.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteLineAsync(string.Join(",", columns.Select(CsvField)));
            foreach (var row in rows)
                await writer.WriteLineAsync(string.Join(",", row.Select(v => CsvField(v ?? ""))));
            StatusLabel.Text = $"Exported {rows.Count:N0} record(s) to {file.Name}";
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
                view.SetSql(Environment.GetEnvironmentVariable("IAPDM_SHOT_SQL")
                    ?? "select table_schema, table_name from information_schema.tables order by 1, 2;");
                await view.ExecuteAtCaretAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_query.png"));

                await view.ScrollToBottomAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_scrolled.png"));
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
            // 샘플 데이터로 화면 채우기
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
