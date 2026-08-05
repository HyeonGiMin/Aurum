using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>범례 한 줄 — 주제영역 색 스와치 + 이름. (XAML 컴파일 바인딩 대상이라 public)</summary>
public sealed record ErdLegendRow(IBrush Swatch, string Label);

/// <summary>상세 패널의 컬럼 한 줄. (XAML 컴파일 바인딩 대상이라 public)</summary>
public sealed record ErdColumnRow(string Marker, IBrush MarkerBrush, string Name, string Type);

/// <summary>상세 패널의 관계 한 줄 — 클릭하면 <c>TargetKey</c> 로 이동한다.</summary>
public sealed record ErdRelationRow(string Text, string TargetKey);

/// <summary>
/// 스키마 ERD 창 (Tools &gt; Diagram). 읽기 전용 — DDL 을 만들거나 바꾸지 않는다.
/// 전체 스키마는 한 화면에 담기지 않으므로 SQL Developer 처럼 "선택 테이블 + N홉"을
/// 기본 시야로 두고, Filter / Depth 로 좁혀 본다.
/// </summary>
public partial class ErdWindow : Window
{
    private const double MinScale = 0.2;
    private const double MaxScale = 3.0;
    /// <summary>이 이상이면 한 장으로 읽기 어렵다 — 상태바로 알린다.</summary>
    private const int CrowdedTableCount = 80;
    /// <summary>PNG 로 뽑을 때의 한 변 상한 (메모리 보호).</summary>
    private const int MaxExportPixels = 8000;
    /// <summary>우리 제품 스키마 — 있으면 이걸 먼저 보여준다.</summary>
    private const string DefaultSchema = "prismone";

    private readonly IErdCatalog _catalog;
    private readonly string _database;
    /// <summary>스크린샷 하니스용 — 접속 없이 미리 만든 그래프를 그릴 때만 채워진다.</summary>
    private readonly ErdGraph? _preset;
    private ErdGraph _full = ErdGraph.Empty;
    private string? _focusKey;
    private bool _suppressReload;

    private bool _panning;
    private Point _panOrigin;
    private Vector _panStartOffset;

    /// <summary>선택 히스토리 — 넓은 스키마를 FK 로 걸어다니다 되돌아오기 위한 것. [ / ] 로 이동.</summary>
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    /// <summary>히스토리 이동으로 인한 선택은 다시 히스토리에 쌓지 않는다.</summary>
    private bool _navigatingHistory;
    private string? _hoveredKey;
    private bool _suppressJump;

    public ErdWindow() : this(ConnectionProfile.Default, null) { }

    public ErdWindow(ConnectionProfile profile, string? focusKey)
    {
        InitializeComponent();
        // provider 가 자기 DB 의 카탈로그를 준다 (PG/Oracle/SQLite)
        _catalog = profile.Provider.CreateErdCatalog(profile);
        _database = profile.Database;
        _focusKey = focusKey;

        Title = $"Diagram - {profile.DisplayName}";
        WindowPlacementTracker.Attach(this, "erd");
        _suppressReload = true;
        FocusBox.IsChecked = focusKey is not null;
        FocusLabel.Text = focusKey ?? "—";
        _suppressReload = false;

        Scroll.PointerPressed += OnSurfacePressed;
        Scroll.PointerMoved += OnSurfaceMoved;
        Scroll.PointerReleased += OnSurfaceReleased;
        Scroll.PointerWheelChanged += OnSurfaceWheel;
        Scroll.DoubleTapped += OnSurfaceDoubleTapped;
        Scroll.PointerExited += (_, _) => SetHover(null);
        MiniMap.Navigate += (_, at) => CenterOn(at);
        // 화면 밖 렌더를 건너뛰려면 캔버스가 지금 보이는 범위를 알아야 한다
        Scroll.ScrollChanged += (_, _) => UpdateViewport();
        Scroll.SizeChanged += (_, _) => UpdateViewport();

        Opened += async (_, _) => await InitAsync();
    }

    /// <summary>접속 없이 주어진 그래프를 그린다 (IAPDM_SHOT_DIR 스크린샷 하니스 전용).</summary>
    internal ErdWindow(ErdGraph preset) : this(ConnectionProfile.Default, null)
    {
        _preset = preset;
        Title = "Diagram - sample";
    }

    private int Depth => Math.Max(0, DepthCombo.SelectedIndex);
    private bool KeyColumnsOnly => ColumnsCombo.SelectedIndex == 0;

    private ErdGrouping Grouping => GroupCombo.SelectedIndex switch
    {
        1 => ErdGrouping.Prefix,
        2 => ErdGrouping.None,
        _ => ErdGrouping.Component,
    };
    private string FilterText => FilterBox.Text?.Trim() ?? "";
    private bool FocusActive => FocusBox.IsChecked == true && _focusKey is not null;

    // ---------- 적재 ----------

    private async Task InitAsync()
    {
        if (_preset is not null)
        {
            _suppressReload = true;
            SchemaCombo.ItemsSource = _preset.Tables.Select(t => t.Schema).Distinct().ToList();
            SchemaCombo.SelectedIndex = 0;
            _suppressReload = false;
            _full = _preset;
            Rebuild(fit: true);
            return;
        }

        ErdStatus.Text = "스키마 목록 조회 중…";
        ErdProgress.IsVisible = true;
        try
        {
            var schemas = await _catalog.GetSchemasAsync();
            _suppressReload = true;
            SchemaCombo.ItemsSource = schemas;
            // 우선순위: 사용자가 고른 테이블의 스키마 > 우리 제품 스키마 > 접속 DB 이름 > public
            var wanted = _focusKey?.Split('.')[0];
            SchemaCombo.SelectedItem =
                schemas.FirstOrDefault(s => s == wanted) ??
                schemas.FirstOrDefault(s => s.Equals(DefaultSchema, StringComparison.OrdinalIgnoreCase)) ??
                schemas.FirstOrDefault(s => s.Equals(_database, StringComparison.OrdinalIgnoreCase)) ??
                schemas.FirstOrDefault(s => s == "public") ??
                schemas.FirstOrDefault();
            _suppressReload = false;
        }
        catch (Exception ex)
        {
            _suppressReload = false;
            ErdStatus.Text = $"스키마 조회 실패: {ex.Message}";
            ErdProgress.IsVisible = false;
            return;
        }
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_preset is not null)
        {
            Rebuild();
            return;
        }
        if (SchemaCombo.SelectedItem is not string schema) return;
        ErdStatus.Text = $"{schema} 카탈로그 읽는 중… (대형 스키마는 몇 초 걸립니다)";
        ErdProgress.IsVisible = true;
        try
        {
            _full = await _catalog.LoadAsync([schema]);
        }
        catch (Exception ex)
        {
            _full = ErdGraph.Empty;
            Surface.Diagram = null;
            ErdStatus.Text = $"카탈로그 조회 실패: {ex.Message}";
            return;
        }
        finally
        {
            ErdProgress.IsVisible = false;
        }
        Rebuild(fit: true);
    }

    // ---------- 화면 구성 ----------

    private void Rebuild(bool fit = false)
    {
        var graph = _full;
        var narrowed = false;

        if (FilterText.Length > 0)
        {
            graph = graph.Filter(FilterText);
            narrowed = true;
        }
        if (FocusActive)
        {
            graph = graph.Focus([_focusKey!], Depth);
            narrowed = true;
        }

        var options = ErdLayoutOptions.Default with
        {
            KeyColumnsOnly = KeyColumnsOnly,
            Grouping = Grouping,
        };
        var diagram = ErdLayout.Compute(graph, options);
        Surface.Diagram = diagram;
        Select(_focusKey);
        FocusLabel.Text = _focusKey ?? "—";
        EmptyHint.IsVisible = diagram.Boxes.Count == 0;
        BuildLegend(diagram);
        RefreshJumpSource(diagram);
        MiniMap.Diagram = diagram;
        MiniMapPanel.IsVisible = diagram.Boxes.Count > 0;
        UpdateViewport();

        ErdStatus.Text = Describe(graph, narrowed);
        if (fit) FitToWindow();
    }

    private string Describe(ErdGraph graph, bool narrowed)
    {
        if (graph.Tables.Count == 0)
            return _full.Tables.Count == 0
                ? "테이블이 없습니다."
                : "조건에 맞는 테이블이 없습니다 — Filter 나 Focus 를 확인하세요.";

        var text = $"{graph.Tables.Count:N0} table(s) · {graph.Relations.Count:N0} relation(s)";
        if (narrowed) text += $"  (전체 {_full.Tables.Count:N0})";

        if (_full.Relations.Count == 0)
            text += "  · FK 제약이 없어 관계선이 없습니다 (논리적 관계만 쓰는 스키마일 수 있음)";
        else if (!narrowed && graph.Tables.Count > CrowdedTableCount)
            text += "  · 테이블이 많습니다 — Filter 나 테이블 더블클릭(Focus)으로 좁혀 보세요";

        return text;
    }

    /// <summary>좌상단 범례 — 주제영역 색과 이름. 클릭하면 그 영역 대표 테이블로 Focus.</summary>
    private void BuildLegend(ErdDiagram diagram)
    {
        var groups = diagram.Groups;
        LegendItems.ItemsSource = groups
            .Select(g => new ErdLegendRow(
                new SolidColorBrush(ErdCanvas.GroupColor(g.ColorIndex)),
                $"{g.Name}  ({g.TableCount})"))
            .ToList();
        LegendPanel.IsVisible = LegendBox.IsChecked == true && groups.Count > 0;
    }

    private void OnLegendToggled(object? sender, RoutedEventArgs e)
    {
        if (LegendPanel is null) return;
        LegendPanel.IsVisible = LegendBox.IsChecked == true && Surface.Diagram?.Groups.Count > 0;
    }

    // ---------- 툴바 ----------

    private async void OnSchemaChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReload) return;
        await ReloadAsync();
    }

    private void OnViewOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReload || Surface is null) return;
        Rebuild();
    }

    private async void OnReload(object? sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnFit(object? sender, RoutedEventArgs e) => FitToWindow();

    private void OnZoomIn(object? sender, RoutedEventArgs e) => ZoomAt(1.25, ViewportCenter);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => ZoomAt(1 / 1.25, ViewportCenter);

    private void OnZoomReset(object? sender, RoutedEventArgs e) => ZoomAt(1 / Surface.Scale, ViewportCenter);

    private Point ViewportCenter => new(Scroll.Viewport.Width / 2, Scroll.Viewport.Height / 2);

    private void SetScale(double scale)
    {
        Surface.Scale = Math.Clamp(scale, MinScale, MaxScale);
        ZoomLabel.Text = $"{Surface.Scale * 100:0}%";
    }

    /// <summary>기준점(뷰포트 좌표) 아래의 내용이 제자리에 남도록 확대/축소한다.</summary>
    private void ZoomAt(double factor, Point anchor)
    {
        var before = Surface.Scale;
        if (before <= 0) return;
        var contentX = (Scroll.Offset.X + anchor.X) / before;
        var contentY = (Scroll.Offset.Y + anchor.Y) / before;

        SetScale(before * factor);
        var after = Surface.Scale;
        if (Math.Abs(after - before) < 1e-9) return;

        // Offset 은 Extent 기준으로 잘리므로 새 배율의 레이아웃이 끝난 뒤에 옮긴다.
        Scroll.UpdateLayout();
        Scroll.Offset = new Vector(contentX * after - anchor.X, contentY * after - anchor.Y);
    }

    private void FitToWindow()
    {
        var diagram = Surface.Diagram;
        if (diagram is null || diagram.Width <= 0 || diagram.Height <= 0) return;
        var viewport = Scroll.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;

        var scale = Math.Min(viewport.Width / diagram.Width, viewport.Height / diagram.Height);
        // 확대까지 하면 작은 다이어그램이 우스꽝스러워진다 — 축소 방향으로만 맞춘다.
        SetScale(Math.Min(1.0, scale));
    }

    // ---------- 포인터 ----------

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        var hit = Surface.ColumnAt(e.GetPosition(Surface));
        if (hit is { } h)
        {
            // FK 점프가 켜져 있고 FK 컬럼 행을 짚었으면 참조 대상으로 이동한다
            if (FkJumpBox.IsChecked == true && h.Column is { IsFk: true } column
                && FindFkTarget(h.Box.Table.Key, column.Name) is { } target)
            {
                GoToTable(target);
                return;
            }
            Select(h.Box.Table.Key);
            ErdStatus.Text = DescribeBox(h.Box);
            return;
        }

        Select(null);
        if (!e.GetCurrentPoint(Scroll).Properties.IsLeftButtonPressed) return;
        _panning = true;
        _panOrigin = e.GetPosition(Scroll);
        _panStartOffset = Scroll.Offset;
        Scroll.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>선택 테이블과 그와 FK 로 이어진 이웃을 함께 강조한다.</summary>
    private void Select(string? key)
    {
        Surface.SelectedKey = key;
        Surface.RelatedKeys = key is null
            ? null
            : _full.Relations
                .Where(r => r.ChildKey == key || r.ParentKey == key)
                .Select(r => r.ChildKey == key ? r.ParentKey : r.ChildKey)
                .ToHashSet();

        if (key is not null && !_navigatingHistory) PushHistory(key);
        UpdateDetail(key);
    }

    // ---------- 뷰포트 컬링 ----------

    /// <summary>지금 보이는 범위를 캔버스와 미니맵에 알린다 — 캔버스는 밖을 그리지 않는다.</summary>
    private void UpdateViewport()
    {
        var viewport = Scroll.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            Surface.Viewport = null;
            MiniMap.ViewBox = null;
            return;
        }

        Surface.Viewport = new Rect(Scroll.Offset.X, Scroll.Offset.Y, viewport.Width, viewport.Height);

        // 미니맵은 줌과 무관한 다이어그램 좌표로 받는다
        var scale = Surface.Scale;
        MiniMap.ViewBox = scale <= 0
            ? null
            : new Rect(Scroll.Offset.X / scale, Scroll.Offset.Y / scale,
                       viewport.Width / scale, viewport.Height / scale);
    }

    /// <summary>다이어그램 좌표의 한 점을 화면 가운데로 가져온다 (미니맵 이동용).</summary>
    private void CenterOn(ErdPoint at)
    {
        var scale = Surface.Scale;
        if (scale <= 0) return;
        Scroll.Offset = new Vector(
            Math.Max(0, at.X * scale - Scroll.Viewport.Width / 2),
            Math.Max(0, at.Y * scale - Scroll.Viewport.Height / 2));
    }

    // ---------- 호버 강조 ----------

    private void SetHover(string? key)
    {
        if (HoverBox.IsChecked != true) key = null;
        if (_hoveredKey == key) return;
        _hoveredKey = key;
        Surface.HoveredKey = key;
    }

    // ---------- 선택 히스토리 ----------

    private void PushHistory(string key)
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex] == key) return;
        // 뒤로 갔다가 새 곳을 고르면 앞쪽 기록은 버린다 (브라우저와 같은 규칙)
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(key);
        _historyIndex = _history.Count - 1;
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
    }

    private void GoHistory(int delta)
    {
        var next = _historyIndex + delta;
        if (next < 0 || next >= _history.Count) return;
        _historyIndex = next;
        _navigatingHistory = true;
        GoToTable(_history[next]);
        _navigatingHistory = false;
        UpdateHistoryButtons();
    }

    private void OnHistoryBack(object? sender, RoutedEventArgs e) => GoHistory(-1);

    private void OnHistoryForward(object? sender, RoutedEventArgs e) => GoHistory(1);

    // ---------- 테이블로 이동 ----------

    /// <summary>해당 테이블을 화면 가운데로 스크롤하고 선택한다. 현재 화면에 없으면 알린다.</summary>
    private void GoToTable(string key)
    {
        var box = Surface.Diagram?.Boxes.FirstOrDefault(b => b.Table.Key == key);
        if (box is null)
        {
            ErdStatus.Text = $"{key} 은(는) 지금 화면 조건(Filter/Focus)에 없습니다.";
            return;
        }

        var scale = Surface.Scale;
        Scroll.Offset = new Vector(
            Math.Max(0, box.CenterX * scale - Scroll.Viewport.Width / 2),
            Math.Max(0, box.CenterY * scale - Scroll.Viewport.Height / 2));
        Select(key);
        ErdStatus.Text = DescribeBox(box);
    }

    /// <summary>이동 상자의 후보 목록 — 지금 그려진 테이블들.</summary>
    private void RefreshJumpSource(ErdDiagram diagram)
    {
        _suppressJump = true;
        JumpBox.ItemsSource = diagram.Boxes.Select(b => b.Table.Key).OrderBy(k => k).ToList();
        JumpBox.SelectedItem = null;
        JumpBox.Text = "";
        _suppressJump = false;
    }

    private void OnJumpSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressJump || JumpBox.SelectedItem is not string key) return;
        GoToTable(key);
    }

    // ---------- 상세 패널 ----------

    private void OnDetailToggled(object? sender, RoutedEventArgs e)
    {
        if (DetailPanel is null) return;
        DetailPanel.IsVisible = DetailBox.IsChecked == true;
    }

    private void UpdateDetail(string? key)
    {
        if (DetailColumns is null) return;

        var table = key is null ? null : _full.Tables.FirstOrDefault(t => t.Key == key);
        if (table is null)
        {
            DetailTitle.Text = "테이블을 클릭하세요";
            DetailSub.Text = "";
            DetailColumns.ItemsSource = null;
            DetailRelations.ItemsSource = null;
            DetailNoRelations.IsVisible = false;
            return;
        }

        DetailTitle.Text = table.Name;
        DetailSub.Text = $"{table.Schema}{(table.IsView ? " · view" : "")} · {table.Columns.Count} columns";

        DetailColumns.ItemsSource = table.Columns
            .Select(c => new ErdColumnRow(
                c.IsPk ? "PK" : c.IsFk ? "FK" : "",
                c.IsPk ? PkBrush : FkBrush,
                c.Name + (c.NotNull ? " *" : ""),
                c.Type))
            .ToList();

        // 나가는 FK 와 들어오는 참조를 한 목록에 — 어느 쪽이든 클릭하면 상대로 간다
        var rows = _full.Relations
            .Where(r => r.ChildKey == key || r.ParentKey == key)
            .Select(r => r.ChildKey == key
                ? new ErdRelationRow($"→ {r.ParentKey}  ({string.Join(", ", r.ChildColumns)})", r.ParentKey)
                : new ErdRelationRow($"← {r.ChildKey}  ({string.Join(", ", r.ChildColumns)})", r.ChildKey))
            .ToList();

        _suppressJump = true;
        DetailRelations.ItemsSource = rows;
        DetailRelations.SelectedItem = null;
        _suppressJump = false;
        DetailNoRelations.IsVisible = rows.Count == 0;
    }

    private void OnRelationActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressJump || DetailRelations.SelectedItem is not ErdRelationRow row) return;
        GoToTable(row.TargetKey);
    }

    private static readonly IBrush PkBrush = new SolidColorBrush(Color.Parse("#8A5A00"));
    private static readonly IBrush FkBrush = new SolidColorBrush(Color.Parse("#1F4E79"));

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning)
        {
            SetHover(Surface.BoxAt(e.GetPosition(Surface))?.Table.Key);
            return;
        }
        var delta = _panOrigin - e.GetPosition(Scroll);
        Scroll.Offset = _panStartOffset + delta;
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panning = false;
        Scroll.Cursor = Cursor.Default;
    }

    /// <summary>
    /// Ctrl+휠 = 확대/축소(커서 아래 지점 고정), Shift+휠 = 가로 이동.
    /// 그냥 휠은 손대지 않고 ScrollViewer 의 기본 세로 스크롤에 맡긴다.
    /// </summary>
    private void OnSurfaceWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomAt(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, e.GetPosition(Scroll));
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            Scroll.Offset = new Vector(Scroll.Offset.X - e.Delta.Y * 60, Scroll.Offset.Y);
            e.Handled = true;
        }
    }

    /// <summary>더블클릭한 테이블을 새 Focus 로 삼는다 — 넓은 스키마를 걸어다니는 기본 동선.</summary>
    private void OnSurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        var box = Surface.BoxAt(e.GetPosition(Surface));
        if (box is null) return;
        _focusKey = box.Table.Key;
        _suppressReload = true;
        FocusBox.IsChecked = true;
        _suppressReload = false;
        Rebuild();
    }

    /// <summary>DataGrip·SQL Developer 관례에 맞춘 단축키. 에디터가 없는 창이라 맨키로 받는다.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Source is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomAt(1.25, ViewportCenter);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomAt(1 / 1.25, ViewportCenter);
                break;
            case Key.D0 or Key.NumPad0:
                FitToWindow();
                break;
            case Key.D1 or Key.NumPad1:
                ZoomAt(1 / Surface.Scale, ViewportCenter);
                break;
            case Key.F5:
                _ = ReloadAsync();
                break;
            case Key.Escape:
                Select(null);
                break;
            case Key.T:
                JumpBox.Focus();
                break;
            case Key.H:
                HoverBox.IsChecked = HoverBox.IsChecked != true;
                if (HoverBox.IsChecked != true) SetHover(null);
                break;
            case Key.F:
                FkJumpBox.IsChecked = FkJumpBox.IsChecked != true;
                break;
            case Key.M:
                MiniMapPanel.IsVisible = !MiniMapPanel.IsVisible;
                break;
            case Key.OemOpenBrackets:
                GoHistory(-1);
                break;
            case Key.OemCloseBrackets:
                GoHistory(1);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    /// <summary>이 테이블의 해당 FK 컬럼이 가리키는 부모 테이블. 없으면 null.</summary>
    private string? FindFkTarget(string childKey, string columnName) =>
        _full.Relations
            .FirstOrDefault(r => r.ChildKey == childKey &&
                                 r.ChildColumns.Any(c => c.Equals(columnName, StringComparison.OrdinalIgnoreCase)))
            ?.ParentKey;

    private string DescribeBox(ErdBox box)
    {
        var key = box.Table.Key;
        var outgoing = _full.Relations.Count(r => r.ChildKey == key);
        var incoming = _full.Relations.Count(r => r.ParentKey == key);
        return $"{key} · {box.Table.Columns.Count} column(s) · FK 나감 {outgoing} · 참조됨 {incoming}";
    }

    // ---------- 내보내기 ----------

    private async void OnSavePng(object? sender, RoutedEventArgs e)
    {
        var diagram = Surface.Diagram;
        if (diagram is null || diagram.Boxes.Count == 0)
        {
            ErdStatus.Text = "그릴 다이어그램이 없습니다.";
            return;
        }

        var width = (int)Math.Ceiling(diagram.Width);
        var height = (int)Math.Ceiling(diagram.Height);
        if (width > MaxExportPixels || height > MaxExportPixels)
        {
            ErdStatus.Text = $"다이어그램이 너무 큽니다({width}×{height}px) — Filter/Focus 로 좁힌 뒤 내보내세요.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Diagram As PNG",
            SuggestedFileName = "erd.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }],
        });
        if (file is null) return;

        try
        {
            // 화면 줌과 무관하게 100% 좌표로 렌더해 글자가 또렷하게 남는다.
            var surface = new ErdCanvas { Diagram = diagram, Scale = 1.0 };
            surface.Measure(new Size(diagram.Width, diagram.Height));
            surface.Arrange(new Rect(0, 0, diagram.Width, diagram.Height));

            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(surface);
            await using var stream = await file.OpenWriteAsync();
            bitmap.Save(stream);
            ErdStatus.Text = $"Saved {file.Name} ({width}×{height}px)";
            Toast.Show(this, "PNG 저장 완료", $"{file.Name} ({width}×{height}px)");
        }
        catch (Exception ex)
        {
            ErdStatus.Text = $"저장 실패: {ex.Message}";
        }
    }
}
