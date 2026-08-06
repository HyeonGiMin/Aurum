using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Npgsql;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Studio;

/// <summary>그리드 한 행: Golden 처럼 왼쪽에 행번호(No)를 붙인다. Raw 는 잘린 셀의 원문.</summary>
/// <summary>Golden 툴바의 결과 보기 선택 — Show DataGrid / Show Text / Show Log.</summary>
public enum ResultViewMode
{
    Grid,
    Text,
    Log,
}

public sealed record RowItem(int No, string?[] Cells, string?[]? Raw = null)
    : System.ComponentModel.INotifyPropertyChanged
{
    private int _seq;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 그리드 맨 왼쪽 순번. 정렬해도 **위에서부터 1,2,3…** 이 되도록 화면 순서로 다시 매긴다
    /// (<see cref="No"/> 는 fetch 순서 그대로라 정렬하면 뒤섞인다).
    /// </summary>
    public int Seq
    {
        get => _seq == 0 ? No : _seq;
        set
        {
            if (_seq == value) return;
            _seq = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Seq)));
        }
    }

    /// <summary>편집 모드(Run and Edit)의 행 식별자 — PG ctid. 새로 추가한 행이면 null.</summary>
    public string? RowId { get; init; }

    /// <summary>편집 모드 진입 시점의 값 — 무엇이 바뀌었는지 판별한다.</summary>
    public string?[]? Original { get; init; }

    /// <summary>
    /// Mongo 전용 — 이 행이 온 원본 문서(<see cref="PrismOne.Db.Core.Mongo.MongoRowContext"/>).
    /// 편집 불가능한 결과(aggregate 등)면 null — Edit Document 메뉴가 이 값으로 활성화 여부를 정한다.
    /// </summary>
    public object? MongoContext { get; init; }

    /// <summary>
    /// 편집 모드 바인딩 경로. DataGrid 는 바인딩 경로의 속성을 리플렉션으로 검사해
    /// 편집 가능 여부를 판정하는데, CLR 배열에는 인덱서 PropertyInfo 가 없어
    /// Cells[i] 경로가 읽기 전용으로 판정된다(BeginEdit 거부). 진짜 인덱서로 우회한다.
    /// </summary>
    public string? this[int index]
    {
        get => index >= 0 && index < Cells.Length ? Cells[index] : null;
        set
        {
            if (index >= 0 && index < Cells.Length)
                Cells[index] = value;
        }
    }
}

/// <summary>
/// 쿼리 탭 하나 = 세션 하나 (Golden 의 쿼리 창 모델).
/// F9 로 커서 위치 문장을 실행하고, 결과는 배치 단위로 점진 fetch 한다.
/// 상태(message / rows / time / caret)는 이벤트로 메인 상태바에 올린다.
/// </summary>
public partial class QueryTabView : UserControl
{
    private const int AutoFetchThreshold = 60;

    /// <summary>옵션(fetch 크기·행수 상한·NULL 표시·timeout) — MainWindow 가 주입.</summary>
    public AppOptions Options { get; set; } = new();
    private int FetchBatch => Options.FetchBatch;

    private QuerySession? _session;

    /// <summary>
    /// Oracle 접속이면 문장 분리기가 SQL*Plus 관례(PL/SQL 블록 + '/' 종결)를 따르게 한다 —
    /// PL/SQL 안의 세미콜론을 잘못 끊으면 블록 실행 자체가 깨진다.
    /// </summary>
    private bool IsOracleSession => _session?.Profile.Kind == DbKind.Oracle;
    private bool _ownsSession;      // private 탭이면 true — 닫을 때 세션도 정리
    private ActiveQuery? _current;
    private CancellationTokenSource? _cts;
    private ObservableCollection<RowItem> _rows = [];
    private IReadOnlyList<string> _columns = [];
    private ScrollBar? _vScroll;
    private bool _fetching;
    private bool _executing;   // 실행 중 재실행 요청 무시 (연타 시 세션 충돌 방지)
    private readonly DispatcherTimer _runTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AvaloniaEdit.CodeCompletion.CompletionWindow? _completion;
    private readonly Dictionary<string, List<ColumnInfo>> _columnCache = new(StringComparer.OrdinalIgnoreCase);

    // SQL 검증 (DATAGRIP_GAP §2): 타자 후 잠깐 쉬면 introspection 캐시와 대조해 밑줄
    private readonly SqlErrorRenderer _errorRenderer = new();
    private readonly DispatcherTimer _validateTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _schemaLoadKicked;

    /// <summary>자동완성용 테이블 카탈로그 — 접속 후 MainWindow 가 채워준다.</summary>
    public List<TableInfo> CompletionTables { get; set; } = [];

    /// <summary>브라우저에서 선택된 스키마 — 자동완성 정렬에서 우선한다.</summary>
    public string? PreferredSchema { get; set; }

    /// <summary>Golden 기본: 수동 커밋 (false). true 면 PG 기본 autocommit 그대로.</summary>
    public bool AutoCommit { get; set; }

    public bool InTransaction => _session?.InTransaction == true;
    private readonly Stopwatch _scriptWatch = new();

    public string InfoMessage { get; private set; } = "Ready";
    public string InfoRows { get; private set; } = "";
    public string InfoTime { get; private set; } = "";

    /// <summary>Golden 상태바의 "Selected N records" — 여러 행을 골랐을 때만 채운다.</summary>
    public string InfoSelection =>
        ResultGrid.SelectedItems.Count > 1
            ? $"Selected {ResultGrid.SelectedItems.Count:N0} records"
            : "";

    /// <summary>
    /// Golden 상태바의 "Modified" — 에디터 내용이 마지막 저장/열기 이후 바뀌었는지.
    /// AvaloniaEdit 의 UndoStack 이 이미 추적하므로 따로 dirty 플래그를 두지 않는다.
    /// </summary>
    public bool IsModified => !Editor.Document.UndoStack.IsOriginalFile;
    public event Action<QueryTabView>? InfoChanged;
    public event Action<QueryTabView, int, int>? CaretChanged;

    public bool IsConnected => _session?.IsAlive == true;
    public string SessionDisplayName => _session?.Profile.DisplayName ?? "not connected";
    public ConnectionProfile? SessionProfile => _session?.Profile;
    public bool IsPrivateSession => _ownsSession;

    /// <summary>마지막으로 그리드를 만든 문장 — COPY export 가 서버에서 다시 실행한다.</summary>
    public string? LastGridSql { get; private set; }

    /// <summary>Transpose: 행/열 전치 표시 (Golden 의 Transpose Columns/Records).</summary>
    private bool _transposed;

    /// <summary>바인드 변수 값 — Golden 처럼 탭 안에서 이전 값을 기억한다.</summary>
    private readonly Dictionary<string, string?> _bindValues = new(StringComparer.OrdinalIgnoreCase);

    private readonly AvaloniaEdit.Search.SearchPanel _search;

    public QueryTabView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = SqlHighlighting.For(ThemeBrushes.IsDark);
        EmptyHint.IsVisible = true;   // 새 탭은 미접속 — 다음 행동 안내 (AttachSession 이 끈다)
        _search = AvaloniaEdit.Search.SearchPanel.Install(Editor);   // Ctrl+F 내장
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretChanged?.Invoke(this, Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
        // 실행 중 경과 시간을 상태바에 실시간 표시 (Golden 의 Running… + 타이머)
        _runTimer.Tick += (_, _) =>
        {
            if (_executing)
                SetInfo(InfoMessage, InfoRows, ScriptTime());
        };
        // 스크롤바는 행이 넘칠 때 비로소 생기므로, 휠/키 입력 시마다 훅을 재시도한다
        ResultGrid.AddHandler(PointerWheelChangedEvent, (_, _) =>
        {
            HookScrollBar();
            Dispatcher.UIThread.Post(MaybeAutoFetch, DispatcherPriority.Background);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ResultGrid.AddHandler(KeyDownEvent, (_, _) =>
        {
            HookScrollBar();
            Dispatcher.UIThread.Post(MaybeAutoFetch, DispatcherPriority.Background);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // 셀 더블클릭 → 전문 상세 창 (jsonb 는 pretty-print)
        ResultGrid.DoubleTapped += OnCellDoubleTapped;
        // 맨 왼쪽 순번을 그리기 직전에 화면 위치로 매긴다 (정렬해도 1,2,3… 유지)
        ResultGrid.LoadingRow += OnResultRowLoading;
        // 상태바의 Selected / Modified 갱신 (Golden 파리티)
        ResultGrid.SelectionChanged += (_, _) => InfoChanged?.Invoke(this);
        Editor.Document.TextChanged += (_, _) => InfoChanged?.Invoke(this);

        // SQL 검증: 타자 후 0.6초 쉬면 없는 테이블/컬럼에 빨간 물결 밑줄 + 호버 툴팁
        Editor.TextArea.TextView.BackgroundRenderers.Add(_errorRenderer);
        _validateTimer.Tick += (_, _) =>
        {
            _validateTimer.Stop();
            ValidateSql();
        };
        Editor.Document.TextChanged += (_, _) => RestartValidation();
        Editor.TextArea.TextView.PointerHover += OnEditorHover;
        Editor.TextArea.TextView.PointerHoverStopped += (_, _) => ToolTip.SetIsOpen(Editor, false);

        // 자동완성: Ctrl+Space 수동 호출, '.' 입력 시 자동 (Golden 의 popup table/field lists)
        // + FROM/JOIN 뒤에서는 스페이스/첫 글자 입력만으로 테이블 목록 자동 팝업
        Editor.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text == ".")
            {
                _ = ShowCompletionAsync();
                return;
            }
            if (_completion is not null || string.IsNullOrEmpty(e.Text))
                return;
            var ch = e.Text[0];
            if (ch != ' ' && !char.IsLetter(ch) && ch != '_')
                return;
            var text = Editor.Text ?? "";
            var caret = Math.Clamp(Editor.CaretOffset, 0, text.Length);
            var wordStart = caret;
            while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
                wordStart--;
            if (SqlCompletion.IsTablePosition(text, wordStart))
                _ = ShowCompletionAsync();
        };
        Editor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            // Ctrl+Space, macOS 에선 Option+Space 도 (Ctrl+Space 가 입력소스 전환과 겹침)
            var trigger = e.Key == Avalonia.Input.Key.Space &&
                          (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) ||
                           (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)));
            if (trigger)
            {
                e.Handled = true;
                _ = ShowCompletionAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>스크린샷 모드 전용 — 현재 떠 있는 자동완성 팝업 창.</summary>
    internal Control? CompletionWindowForShot => _completion?.CompletionList;

    /// <summary>스크린샷 모드 전용 — 자동완성 팝업을 강제로 띄운다.</summary>
    internal Task ShowCompletionForShotAsync()
    {
        Editor.CaretOffset = (Editor.Text ?? "").Length;
        return ShowCompletionAsync();
    }

    // ---------- Autocomplete ----------

    private async Task ShowCompletionAsync()
    {
        var text = Editor.Text ?? "";
        var caret = Math.Clamp(Editor.CaretOffset, 0, text.Length);

        var wordStart = caret;
        while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
            wordStart--;

        string? qualifier = null;
        if (wordStart > 0 && text[wordStart - 1] == '.')
        {
            var qEnd = wordStart - 1;
            var qStart = qEnd;
            while (qStart > 0 && (char.IsLetterOrDigit(text[qStart - 1]) || text[qStart - 1] == '_'))
                qStart--;
            if (qStart < qEnd)
                qualifier = text[qStart..qEnd];
        }

        List<SqlCompletionItem> items;
        if (qualifier is null)
        {
            // 테이블 자리(from/join/into/update 뒤)면 테이블만 — 키워드 잡음 제거
            if (SqlCompletion.IsTablePosition(text, wordStart))
            {
                items = SqlCompletion.TablesOnly(CompletionTables, PreferredSchema);
            }
            else
            {
                items = SqlCompletion.General(CompletionTables, PreferredSchema);
                // WHERE/AND/ON/SELECT 뒤라면 FROM 에 적힌 테이블들의 컬럼을 맨 위에 붙인다
                // (Golden 동작). 카탈로그가 provider 별로 채워지므로 PG·Oracle 모두 동작한다
                if (SqlCompletion.IsColumnPosition(text, wordStart))
                    items = [.. await ColumnsInScopeAsync(text), .. items];
            }
        }
        else
        {
            items = SqlCompletion.SchemaTables(CompletionTables, qualifier)
                    ?? await ColumnItemsAsync(qualifier, text);
        }
        if (items.Count == 0)
            return;

        _completion?.Close();
        var window = new AvaloniaEdit.CodeCompletion.CompletionWindow(Editor.TextArea)
        {
            StartOffset = wordStart,
            Width = 420,
            MaxHeight = 320,
        };
        foreach (var item in items)
            window.CompletionList.CompletionData.Add(item);
        window.Closed += (_, _) => _completion = null;
        _completion = window;
        window.Show();
    }

    /// <summary>접속당 공용 introspection 캐시 (MainWindow 가 주입). 없으면 직접 조회한다.</summary>
    public SchemaCache? SchemaCache
    {
        get => _schemaCache;
        set
        {
            _schemaCache = value;
            // 접속이 바뀌면 이전 스키마 기준 밑줄은 무효 — 새 캐시로 다시 검증한다
            _schemaLoadKicked = false;
            _errorRenderer.Issues = [];
            Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
            RestartValidation();
        }
    }

    private SchemaCache? _schemaCache;

    private void RestartValidation()
    {
        _validateTimer.Stop();
        _validateTimer.Start();
    }

    /// <summary>
    /// 에디터 전체를 캐시와 대조한다. 캐시가 아직 안 읽혔으면 한 번만 적재를 걸어두고,
    /// 끝나면 다시 들어온다 — 타자마다 접속을 여는 일은 없다.
    /// </summary>
    private void ValidateSql()
    {
        if (SchemaCache?.Loaded is not { } snapshot)
        {
            if (_schemaCache is { } cache && !_schemaLoadKicked)
            {
                _schemaLoadKicked = true;
                _ = cache.GetAsync().ContinueWith(
                    _ => Dispatcher.UIThread.Post(ValidateSql), TaskScheduler.Default);
            }
            return;
        }
        _errorRenderer.Issues = SqlValidator.Validate(Editor.Text ?? "", snapshot);
        Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
    }

    private void OnEditorHover(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_errorRenderer.Issues.Count == 0) return;
        var textView = Editor.TextArea.TextView;
        var pos = textView.GetPositionFloor(e.GetPosition(textView) + textView.ScrollOffset);
        if (pos is null) return;
        var offset = Editor.Document.GetOffset(pos.Value.Location);
        foreach (var issue in _errorRenderer.Issues)
        {
            if (offset >= issue.Start && offset <= issue.Start + issue.Length)
            {
                ToolTip.SetTip(Editor, issue.Message);
                ToolTip.SetIsOpen(Editor, true);
                return;
            }
        }
        ToolTip.SetIsOpen(Editor, false);
    }

    /// <summary>
    /// FROM/JOIN 에 적힌 테이블들의 컬럼 — WHERE 뒤 완성용.
    /// 테이블이 여럿이면 어느 테이블 것인지 설명에 붙인다.
    /// </summary>
    private async Task<List<SqlCompletionItem>> ColumnsInScopeAsync(string sql)
    {
        var tables = SqlCompletion.ReferencedTables(CompletionTables, sql);
        if (tables.Count == 0) return [];

        var items = new List<SqlCompletionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            List<ColumnInfo> columns;
            try
            {
                columns = SchemaCache is { } cache
                    ? [.. await cache.GetColumnsAsync(table)]
                    : await LoadColumnsDirectlyAsync(table);
            }
            catch
            {
                continue;   // 한 테이블을 못 읽어도 나머지는 보여준다
            }

            foreach (var c in columns)
            {
                // 같은 이름이 여러 테이블에 있으면 첫 번째만 (별칭으로 구분하면 되니까)
                if (!seen.Add(c.Name)) continue;
                var detail = tables.Count > 1 ? $"{table.Name} · {c.Type}" : c.Type;
                if (c.Pk.Length > 0) detail += " · PK";
                if (c.Fk.Length > 0) detail += " · FK";
                // 컬럼을 키워드·테이블보다 위로 (가중치 5)
                items.Add(new SqlCompletionItem(c.Name, detail, 5, SqlCompletionKind.Column));
            }
        }
        return items;
    }

    /// <summary>캐시가 없을 때의 예전 경로 — 그 테이블만 조회.</summary>
    private async Task<List<ColumnInfo>> LoadColumnsDirectlyAsync(TableInfo table)
    {
        await using var conn = await _session!.Profile.OpenAsync();
        return await SchemaCatalog.GetColumnsAsync(conn, table);
    }

    private async Task<List<SqlCompletionItem>> ColumnItemsAsync(string qualifier, string sql)
    {
        if (SqlCompletion.ResolveTable(CompletionTables, qualifier, sql) is not { } table || _session is null)
            return [];
        var key = $"{table.Schema}.{table.Name}";
        if (!_columnCache.TryGetValue(key, out var columns))
        {
            try
            {
                // 공용 introspection 캐시가 있으면 접속을 열지 않는다 (DataGrip 방식)
                columns = SchemaCache is { } cache
                    ? [.. await cache.GetColumnsAsync(table)]
                    : await LoadColumnsDirectlyAsync(table);
                _columnCache[key] = columns;
            }
            catch
            {
                return [];
            }
        }
        return columns
            .Select(c => new SqlCompletionItem(
                c.Name,
                c.Type + (c.Pk.Length > 0 ? " · PK" : "") + (c.Fk.Length > 0 ? " · FK" : ""),
                4, SqlCompletionKind.Column))
            .ToList();
    }

    // ---------- Session ----------

    /// <summary>Golden: 탭들은 메인 세션을 공유한다.</summary>
    public void AttachSession(QuerySession session, bool owned = false)
    {
        _session = session;
        _ownsSession = owned;
        AutoCommit = Options.AutoCommit;
        if (Options.StatementTimeoutMs > 0)
            _ = session.ExecuteTextAsync($"SET statement_timeout = {Options.StatementTimeoutMs}");
        if (Options.Isolation != session.Isolation)
            _ = ApplyIsolationQuietlyAsync(session, Options.Isolation);
        session.NoticeReceived += line => Dispatcher.UIThread.Post(() => AppendMessage(line));
        SetInfo($"Session: {session.Profile.DisplayName}" + (owned ? " (private)" : ""));
        EmptyHint.IsVisible = false;   // 접속됨 — 빈 상태 안내 해제
    }

    /// <summary>이 탭의 세션 격리 수준 (DataGrip 의 Tx isolation). 미접속이면 옵션 기본값.</summary>
    public TransactionIsolation Isolation => _session?.Isolation ?? Options.Isolation;

    /// <summary>
    /// 툴바에서 격리 수준을 바꿨을 때. 열린 트랜잭션이 있으면 PG 규약상 다음 트랜잭션부터 적용된다.
    /// </summary>
    public async Task SetIsolationAsync(TransactionIsolation level)
    {
        Options.Isolation = level;
        if (_session is null)
        {
            SetInfo($"Tx Isolation → {level.Display()} (접속하면 적용)");
            return;
        }
        try
        {
            await _session.ApplyIsolationAsync(level);
        }
        catch (Exception ex)
        {
            SetInfo($"Tx isolation 변경 실패: {ex.Message}");
            return;
        }
        SetInfo(InTransaction
            ? $"Tx Isolation → {level.Display()} (열린 트랜잭션이 끝난 뒤부터 적용)"
            : $"Tx Isolation → {level.Display()}");
    }

    /// <summary>세션 부착 시 기본 격리 수준 적용 — 실패해도 접속 자체는 살린다(메시지만 남김).</summary>
    private async Task ApplyIsolationQuietlyAsync(QuerySession session, TransactionIsolation level)
    {
        try
        {
            await session.ApplyIsolationAsync(level);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => SetInfo($"Tx Isolation 적용 실패: {ex.Message}"));
        }
    }

    /// <summary>"New Private Tab" — 이 탭만의 전용 접속을 연다.</summary>
    public async Task ConnectPrivateAsync(ConnectionProfile profile)
    {
        try
        {
            AttachSession(await QuerySession.CreateAsync(profile), owned: true);
        }
        catch (Exception ex)
        {
            SetInfo($"Connect failed: {ex.Message}");
        }
    }

    private void AppendMessage(string line)
    {
        MessagesText.Text += line + "\n";
        MessagesPane.IsVisible = true;
        MessagesScroll.ScrollToEnd();
    }

    public async Task CloseSessionAsync()
    {
        _cts?.Cancel();
        if (_current is not null) { await _current.AbortAsync(); _current = null; }
        if (_session is not null && _ownsSession)
            await _session.DisposeAsync();
        _session = null;
    }

    /// <summary>
    /// Results > View Documents as Tree — 로드된 행들의 원본 문서(순수 find 결과에만
    /// 있다, Edit Document 와 같은 조건). 없으면 null.
    /// </summary>
    public IReadOnlyList<MongoTreeNode>? SnapshotMongoTree()
    {
        var nodes = _rows
            .Select(r => r.MongoContext)
            .OfType<MongoRowContext>()
            .Select((context, i) => MongoTree.FromDocument(context.Document, i))
            .ToList();
        return nodes.Count == 0 ? null : nodes;
    }

    /// <summary>Results > Pin — 현재 그리드의 스냅샷 (없으면 null). 편집 모드는 제외.</summary>
    public (IReadOnlyList<string> Columns, IReadOnlyList<RowItem> Rows, string? Sql)? SnapshotResult() =>
        _columns.Count == 0 || IsEditing ? null : (_columns, _rows.ToList(), LastGridSql);

    /// <summary>테마 전환 시 에디터 배색 교체 (MainWindow 가 호출).</summary>
    public void ApplyEditorTheme(bool dark) => Editor.SyntaxHighlighting = SqlHighlighting.For(dark);

    public void FocusEditor() => Editor.Focus();

    public void SetSql(string sql) => Editor.Text = sql;

    public string GetSql() => Editor.Text ?? "";

    /// <summary>브라우저 더블클릭/quick 셀 — 커서 위치에 텍스트 삽입 (Golden 의 paste-into-query).</summary>
    public void InsertAtCaret(string text)
    {
        Editor.Document.Insert(Editor.CaretOffset, text);
        Editor.Focus();
    }

    // 툴바/메뉴 편집 동작 (Golden: cut/copy/paste/undo/redo)
    public void OpenSearch() => _search.Open();

    /// <summary>커서 위치의 식별자 (schema.table 형태 포함) — Ctrl+D describe 용.</summary>
    public string? WordAtCaret()
    {
        var text = Editor.Text ?? "";
        if (text.Length == 0) return null;
        var caret = Math.Clamp(Editor.CaretOffset, 0, text.Length);
        bool IsPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '"';

        var start = caret;
        while (start > 0 && IsPart(text[start - 1])) start--;
        var end = caret;
        while (end < text.Length && IsPart(text[end])) end++;
        var word = text[start..end].Trim('.', '"');
        return word.Length == 0 ? null : word;
    }

    /// <summary>툴바에서 직접 바인드 변수 값을 미리 입력 (실행 시엔 자동으로 뜬다).</summary>
    public async Task EditBindVariablesAsync()
    {
        var variables = BindVariables.Find(Editor.Text ?? "");
        if (variables.Count == 0)
        {
            SetInfo("No bind variables (:name) in this script");
            return;
        }
        var dialog = new BindVariableDialog(variables, _bindValues);
        if (VisualRoot is Window owner)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
        if (dialog.Result is { } values)
        {
            foreach (var (name, value) in values)
                _bindValues[name] = value;
            SetInfo($"{values.Count} bind variable(s) set");
        }
    }

    // ---------- Query history (Golden ◀ ▶) ----------

    private int _historyIndex = -1;   // -1 = 히스토리 밖(작성 중 초안)
    private string _historyDraft = "";

    public void HistoryPrev()
    {
        var items = HistoryStore.Items;
        if (items.Count == 0) return;
        if (_historyIndex == -1)
        {
            _historyDraft = Editor.Text ?? "";
            _historyIndex = items.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        else return;
        Editor.Text = items[_historyIndex];
        Editor.CaretOffset = Editor.Text.Length;
        SetInfo($"History {_historyIndex + 1} / {items.Count}");
    }

    public void HistoryNext()
    {
        if (_historyIndex == -1) return;
        var items = HistoryStore.Items;
        if (_historyIndex < items.Count - 1)
        {
            _historyIndex++;
            Editor.Text = items[_historyIndex];
            Editor.CaretOffset = Editor.Text.Length;
            SetInfo($"History {_historyIndex + 1} / {items.Count}");
        }
        else
        {
            _historyIndex = -1;
            Editor.Text = _historyDraft;
            Editor.CaretOffset = Editor.Text.Length;
            SetInfo("History: back to draft");
        }
    }

    // ---------- Run and Edit (Golden EditMode) ----------

    /// <summary>편집 모드일 때의 원본 쿼리·대상 테이블. null 이면 읽기 전용.</summary>
    private EditableQuery? _editSource;

    /// <summary>삭제 표시된 행 (Submit 전까지 DB 에는 반영되지 않는다).</summary>
    private readonly List<RowItem> _deletedRows = [];

    public bool IsEditing => _editSource is not null;

    /// <summary>편집 대상 테이블 — 상태 표시용.</summary>
    public string? EditTable => _editSource?.Table;

    public int PendingEditCount => CollectChanges().Count;

    /// <summary>그리드에서 선택된 행 수 — 삭제 확인 문구에 쓴다.</summary>
    public int SelectedRowCount => ResultGrid.SelectedItems.OfType<RowItem>().Count();

    /// <summary>
    /// Golden 의 Run and Edit — 커서 문장(또는 선택 영역)을 ctid 를 붙여 다시 실행하고
    /// 결과 그리드를 편집 가능한 상태로 만든다. 단일 테이블 SELECT 만 가능.
    /// </summary>
    public async Task<bool> RunAndEditAsync()
    {
        var sql = StatementForFavorite();
        if (GridEditor.Prepare(sql) is not { } prepared)
        {
            SetInfo("Run and Edit: 단일 테이블 SELECT 만 편집할 수 있습니다 (조인·집계·DISTINCT 불가)");
            return false;
        }
        if (_session is null)
        {
            SetInfo("Not connected");
            return false;
        }
        _editSource = prepared;
        _deletedRows.Clear();
        await ExecuteStatementsAsync([new SqlStatement(prepared.Sql, 0, prepared.Sql.Length)], explain: false);
        if (_editSource is not null)
            SetInfo($"EditMode: {prepared.Table} — 셀을 고치고 Submit(F11) 하세요");
        return true;
    }

    /// <summary>편집 모드 해제 (일반 실행으로 돌아갈 때).</summary>
    private void LeaveEditMode()
    {
        _editSource = null;
        _deletedRows.Clear();
        ResultGrid.IsReadOnly = true;
    }

    /// <summary>선택한 행들을 삭제 표시하고 그리드에서 감춘다. 실제 DELETE 는 Submit 때.</summary>
    public int MarkSelectedRowsDeleted()
    {
        if (!IsEditing)
            return 0;
        var selected = ResultGrid.SelectedItems.OfType<RowItem>().ToList();
        foreach (var row in selected)
        {
            if (row.RowId is not null)
                _deletedRows.Add(row);
            _rows.Remove(row);
        }
        if (selected.Count > 0)
            SetInfo($"EditMode: {selected.Count} record(s) marked for delete — Submit 하면 반영됩니다");
        return selected.Count;
    }

    /// <summary>Golden 의 하단 insert row — 빈 행을 추가한다.</summary>
    public void AddInsertRow()
    {
        if (!IsEditing)
        {
            SetInfo("Run and Edit 로 편집 모드에 들어간 뒤 사용하세요");
            return;
        }
        var cells = new string?[_columns.Count];
        _rows.Add(new RowItem(_rows.Count + 1, cells));
        ResultGrid.ScrollIntoView(_rows[^1], null);
        SetInfo("EditMode: 새 행 추가 — 값을 넣고 Submit 하세요");
    }

    /// <summary>
    /// 클립보드(탭 구분)를 새 행으로 붙여넣는다 — Golden 의
    /// "EditMode: Paste inserted %d records." 엑셀에서 복사한 표를 그대로 넣을 수 있다.
    /// </summary>
    public async Task PasteRowsAsync()
    {
        if (!IsEditing)
        {
            SetInfo("Run and Edit (F11) 로 편집 모드에 들어간 뒤 사용하세요");
            return;
        }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        // Avalonia 12: IClipboard 는 DataTransfer 기반 — 텍스트는 확장 메서드로 꺼낸다
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            SetInfo("EditMode: 클립보드가 비어 있습니다");
            return;
        }

        // 0번은 ctid 자리라 비워 두고 1번 컬럼부터 채운다
        var pasted = GridEditor.ParsePaste(text, _columns, offset: 1);
        foreach (var cells in pasted)
            _rows.Add(new RowItem(_rows.Count + 1, cells));
        if (pasted.Count > 0)
            ResultGrid.ScrollIntoView(_rows[^1], null);
        SetInfo($"EditMode: Paste inserted {pasted.Count} records. — Submit 하면 반영됩니다");
    }

    /// <summary>편집 내용을 되돌린다 — 원래 쿼리를 다시 실행.</summary>
    public async Task RevertEditsAsync()
    {
        if (_editSource is not { } source)
            return;
        _deletedRows.Clear();
        await ExecuteStatementsAsync([new SqlStatement(source.Sql, 0, source.Sql.Length)], explain: false);
        SetInfo($"EditMode: 되돌렸습니다 ({source.Table})");
    }

    /// <summary>
    /// 변경분을 한 트랜잭션으로 반영한다. 영향 행이 1 이 아니면(다른 세션이 먼저 고쳤거나
    /// ctid 가 바뀐 경우) 전부 롤백한다.
    /// </summary>
    public async Task SubmitEditsAsync()
    {
        if (_editSource is not { } source)
        {
            SetInfo("편집 모드가 아닙니다 (Run and Edit)");
            return;
        }
        if (_session is null)
        {
            SetInfo("Not connected");
            return;
        }
        var changes = CollectChanges();
        if (changes.Count == 0)
        {
            SetInfo("EditMode: 변경된 내용이 없습니다");
            return;
        }

        List<EditStatement> statements;
        try
        {
            statements = GridEditor.Build(source.Table, changes);
        }
        catch (ArgumentException ex)
        {
            SetInfo($"EditMode: {ex.Message}");
            return;
        }

        var applied = 0;
        try
        {
            await _session.EnsureTransactionAsync();
            foreach (var statement in statements)
            {
                var affected = await _session.ExecuteEditAsync(statement);
                if (affected != 1)
                {
                    await _session.RollbackAsync();
                    SetInfo($"EditMode: 대상 행을 찾지 못해 되돌렸습니다 (영향 {affected}행) — 다시 조회하세요");
                    await RevertEditsAsync();
                    return;
                }
                applied++;
            }
        }
        catch (Exception ex)
        {
            try { await _session.RollbackAsync(); } catch { /* 접속이 이미 끊긴 경우 */ }
            SetInfo($"EditMode 실패(롤백): {ex.Message}");
            return;
        }

        if (AutoCommit)
            await _session.CommitAsync();

        var pending = AutoCommit ? "" : " — Commit 필요";
        await RevertEditsAsync();   // ctid 가 바뀌었을 수 있으니 다시 읽는다
        SetInfo($"EditMode: {applied} change(s) submitted{pending}");
    }

    /// <summary>그리드 상태에서 UPDATE/DELETE/INSERT 목록을 만든다. 빈 문자열은 NULL 로 본다.</summary>
    private List<GridChange> CollectChanges()
    {
        var changes = new List<GridChange>();
        if (_editSource is null)
            return changes;

        foreach (var row in _rows)
        {
            if (row.RowId is null)
            {
                var filled = new List<(string, string?)>();
                for (var i = 1; i < _columns.Count && i < row.Cells.Length; i++)
                {
                    if (!string.IsNullOrEmpty(row.Cells[i]))
                        filled.Add((_columns[i], row.Cells[i]));
                }
                if (filled.Count > 0)
                    changes.Add(new GridChange.Insert(filled));
                continue;
            }

            if (row.Original is not { } original)
                continue;
            var edited = new List<(string, string?)>();
            for (var i = 1; i < _columns.Count && i < row.Cells.Length; i++)
            {
                if (row.Cells[i] != original[i])
                    edited.Add((_columns[i], NormalizeCell(row.Cells[i])));
            }
            if (edited.Count > 0)
                changes.Add(new GridChange.Update(row.RowId, edited));
        }

        foreach (var row in _deletedRows)
        {
            if (row.RowId is { } id)
                changes.Add(new GridChange.Delete(id));
        }
        return changes;
    }

    /// <summary>빈 칸은 NULL 로 보낸다 (Golden 도 빈 셀을 NULL 로 다룬다).</summary>
    private static string? NormalizeCell(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // ---------- Favorites ----------

    /// <summary>즐겨찾기에 담을 SQL — 선택 영역이 있으면 그것, 없으면 커서 위치 문장.</summary>
    public string StatementForFavorite()
    {
        if (!string.IsNullOrWhiteSpace(Editor.SelectedText))
            return Editor.SelectedText.Trim();
        var text = Editor.Text ?? "";
        return StatementSplitter.StatementAt(text, Editor.CaretOffset, IsOracleSession)?.Text ?? text.Trim();
    }

    /// <summary>즐겨찾기 실행 — 에디터를 해당 SQL 로 바꾸고 처음부터 스크립트로 돌린다.</summary>
    public Task LoadAndRunAsync(string sql)
    {
        Editor.Text = sql;
        Editor.CaretOffset = 0;
        Editor.Focus();
        return RunScriptAsync();
    }

    public void EditorCut() => Editor.Cut();
    public void EditorCopy() => Editor.Copy();
    public void EditorPaste() => Editor.Paste();
    public void EditorUndo() => Editor.Undo();
    public void EditorRedo() => Editor.Redo();

    /// <summary>Golden: Replace (Ctrl+H) — 검색 패널을 치환 모드로 연다.</summary>
    public void OpenReplace()
    {
        _search.IsReplaceMode = true;
        _search.Open();
    }

    /// <summary>Golden: Toggle Between Edit And Results (Ctrl+R).</summary>
    public void ToggleEditResultsFocus()
    {
        if (Editor.TextArea.IsKeyboardFocusWithin)
            ResultGrid.Focus();
        else
            FocusEditor();
    }

    /// <summary>
    /// Golden: Comment Out (Ctrl+-) / Uncomment (Shift+Ctrl+-) — 선택 영역(없으면 커서 줄)의
    /// 각 줄 앞에 "-- " 를 넣거나 제거한다.
    /// </summary>
    public void CommentSelection(bool uncomment)
    {
        var doc = Editor.Document;
        var start = doc.GetLineByOffset(Editor.SelectionStart);
        var end = doc.GetLineByOffset(Editor.SelectionStart + Math.Max(0, Editor.SelectionLength));
        using (doc.RunUpdate())
        {
            for (var line = start; line is not null && line.LineNumber <= end.LineNumber; line = line.NextLine)
            {
                var text = doc.GetText(line.Offset, line.Length);
                if (uncomment)
                {
                    var i = text.TakeWhile(char.IsWhiteSpace).Count();
                    if (text[i..].StartsWith("-- ", StringComparison.Ordinal))
                        doc.Remove(line.Offset + i, 3);
                    else if (text[i..].StartsWith("--", StringComparison.Ordinal))
                        doc.Remove(line.Offset + i, 2);
                }
                else if (text.Trim().Length > 0)
                {
                    doc.Insert(line.Offset, "-- ");
                }
            }
        }
    }

    public (IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows) Snapshot()
        => (_columns, _rows.Select(r => r.Cells).ToList());

    // ---------- 자가 스크린샷 하니스 전용 (Run and Edit 실접속 검증) ----------

    /// <summary>
    /// 하니스 전용 — 실제 DataGrid 편집 경로(BeginEdit → TextBox 입력 → CommitEdit)로 셀 하나를
    /// 고친다. Cells[i] TwoWay 바인딩이 배열에 값을 되쓰는지가 검증 대상이라 배열을 직접 쓰지 않는다.
    /// </summary>
    public async Task<string> EditCellForShotAsync(int rowIndex, int cellIndex, string value)
    {
        if (!IsEditing || rowIndex >= _rows.Count || cellIndex >= _columns.Count)
            return "not editing / index out of range";
        var row = _rows[rowIndex];
        var column = ResultGrid.Columns.FirstOrDefault(c => Equals(c.Header, _columns[cellIndex]));
        if (column is null)
            return $"column '{_columns[cellIndex]}' not found";

        ResultGrid.Focus();
        ResultGrid.SelectedItem = row;
        ResultGrid.CurrentColumn = column;
        ResultGrid.ScrollIntoView(row, column);
        await Task.Delay(150);

        TextBox? editor = null;
        void OnPreparing(object? _, DataGridPreparingCellForEditEventArgs e)
            => editor = e.EditingElement as TextBox;
        ResultGrid.PreparingCellForEdit += OnPreparing;
        try
        {
            var began = ResultGrid.BeginEdit();
            await Task.Delay(150);
            if (editor is null)
                return $"no editing TextBox (BeginEdit={began}, CurrentColumn={ResultGrid.CurrentColumn?.Header})";
            editor.Text = value;
            var committed = ResultGrid.CommitEdit(DataGridEditingUnit.Row, true);
            await Task.Delay(150);
            return row.Cells[cellIndex] == value
                ? "ok"
                : $"cell not written back (CommitEdit={committed}, cell='{row.Cells[cellIndex]}')";
        }
        finally
        {
            ResultGrid.PreparingCellForEdit -= OnPreparing;
        }
    }

    /// <summary>하니스 전용 — 새로 추가한 행(insert row)의 셀을 채운다.</summary>
    public void SetCellForShot(int rowIndex, int cellIndex, string? value)
    {
        if (rowIndex < _rows.Count && cellIndex < _rows[rowIndex].Cells.Length)
            _rows[rowIndex].Cells[cellIndex] = value;
    }

    /// <summary>하니스 전용 — 그리드 행 하나를 선택한다 (삭제 표시용).</summary>
    public void SelectRowForShot(int rowIndex)
    {
        if (rowIndex >= _rows.Count)
            return;
        ResultGrid.SelectedItem = _rows[rowIndex];
        ResultGrid.ScrollIntoView(_rows[rowIndex], null);
    }

    public bool HasResult => _columns.Count > 0 && _rows.Count > 0;

    // ---------- Execute ----------

    /// <summary>F9: 선택 영역(있으면) 또는 커서 위치 문장 하나를 실행.</summary>
    public Task ExecuteAtCaretAsync()
    {
        List<SqlStatement> statements;
        if (!string.IsNullOrEmpty(Editor.SelectedText))
        {
            statements = StatementSplitter.Split(Editor.SelectedText, IsOracleSession);
        }
        else
        {
            var stmt = StatementSplitter.StatementAt(Editor.Text ?? "", Editor.CaretOffset, IsOracleSession);
            statements = stmt is null ? [] : [stmt];
        }
        return ExecuteStatementsAsync(statements, explain: false);
    }

    /// <summary>
    /// Golden 의 Run Selected (Ctrl+F7) — <b>선택 영역만</b> 실행한다.
    /// 선택이 없으면 커서 문장으로 넘어가지 않고 아무것도 실행하지 않는다
    /// (그 동작은 F7/F9 의 몫이다).
    /// </summary>
    public Task ExecuteSelectedAsync()
    {
        if (string.IsNullOrWhiteSpace(Editor.SelectedText))
        {
            SetInfo("선택된 SQL 이 없습니다");
            return Task.CompletedTask;
        }
        return ExecuteStatementsAsync(StatementSplitter.Split(Editor.SelectedText, IsOracleSession), explain: false);
    }

    /// <summary>커서 문장을 EXPLAIN (FORMAT JSON) 으로 실행해 플랜 트리로 표시.
    /// analyze=true 면 실제 실행 — DML 은 pgAdmin 처럼 자동 롤백으로 보호한다.</summary>
    public async Task ExecuteExplainAsync(bool analyze)
    {
        if (_session is null) { SetInfo("Not connected"); return; }
        if (_executing) { SetInfo("Busy — statement still running. Cancel first."); return; }
        var stmt = StatementSplitter.StatementAt(Editor.Text ?? "", Editor.CaretOffset, IsOracleSession);
        if (stmt is null) return;
        if (!_session.TryBeginRun(this))
        {
            SetInfo("Busy — another tab is running on this session.");
            return;
        }

        _executing = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        HideError();
        ClearResultArea();
        _scriptWatch.Restart();
        _runTimer.Start();

        var needRollback = analyze && !QuerySession.IsReadOnlyStatement(stmt.Text);
        var wasInTx = _session.InTransaction;
        try
        {
            if (_current is not null) { await _current.AbortAsync(); _current = null; }
            await _session.EnsureAliveAsync(ct);
            SetInfo(analyze ? "Running EXPLAIN ANALYZE…" : "Running EXPLAIN…", "", null);

            // Mongo — explain 커맨드가 따로 있다 (⚡ᴱ queryPlanner · ⚡ᴬ executionStats).
            // 읽기 연산만 explain 되므로 롤백 처리도 필요 없다.
            if (_session.Connection is MongoDbConnection mongo)
            {
                var mongoPlan = await mongo.ExplainAsync(stmt.Text, executionStats: analyze, ct);
                _scriptWatch.Stop();
                BindPlanTree(mongoPlan, analyze);
                SetInfo("Explain complete — " + (mongoPlan.ExecutionMs is { } mongoMs
                    ? $"Execution {mongoMs:0.###} ms"
                    : "plan only (not executed)"), "", ScriptTime());
                return;
            }

            // ANALYZE 는 DML 을 실제 실행하므로 트랜잭션으로 감싸 되돌린다
            if (needRollback && !wasInTx)
                await _session.EnsureTransactionAsync(ct);

            var options = analyze ? "ANALYZE, BUFFERS, FORMAT JSON" : "FORMAT JSON";
            // 그리드 경로(표시용 500자 컷)를 타면 JSON 이 잘리므로 원문 전용 경로로 받는다
            var json = await _session.ExecuteTextAsync($"EXPLAIN ({options}) {stmt.Text}", ct);

            if (needRollback)
            {
                await _session.RollbackAsync(ct);
                if (wasInTx)
                    await _session.EnsureTransactionAsync(ct);   // 원래 열려 있던 TX 상태 유지는 포기하고 새로 연다
            }
            _scriptWatch.Stop();

            var plan = PlanParser.Parse(json);
            if (plan is null)
            {
                ShowError("플랜을 해석하지 못했습니다:\n" + json);
                return;
            }
            BindPlanTree(plan, analyze);
            var timing = plan.ExecutionMs is { } ms
                ? $"Execution {ms:0.###} ms · Planning {plan.PlanningMs:0.###} ms"
                : "plan only (not executed)";
            SetInfo($"Explain complete — {timing}" + (needRollback ? " · rolled back" : ""),
                "", ScriptTime());
        }
        catch (OperationCanceledException)
        {
            _scriptWatch.Stop();
            SetInfo("Cancelled");
            await RecoverAsync();
        }
        catch (PostgresException ex)
        {
            _scriptWatch.Stop();
            ShowError($"{ex.Severity} {ex.SqlState}: {ex.MessageText}");
            await RecoverAsync();
        }
        catch (Exception ex)
        {
            _scriptWatch.Stop();
            ShowError(ex.Message);
            await RecoverAsync();
        }
        finally
        {
            _executing = false;
            _runTimer.Stop();
            _session?.EndRun(this);
        }
    }

    internal void BindPlanTree(PlanResult plan, bool analyze)
    {
        // 막대의 분모 — ANALYZE 면 실제 self 시간, 아니면 self 비용 (둘 다 누적이 아니라 자기 몫)
        var useMs = analyze && plan.SelfMsTotal > 0;
        var selfTotal = useMs ? plan.SelfMsTotal : plan.SelfCostTotal;
        var items = new List<TreeViewItem>();
        if (analyze)
        {
            items.Add(new TreeViewItem
            {
                Header = new TextBlock
                {
                    Text = $"Planning {plan.PlanningMs:0.###} ms · Execution {plan.ExecutionMs:0.###} ms",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                },
            });
        }
        items.Add(BuildPlanItem(plan.Root, selfTotal, useMs));
        PlanTree.ItemsSource = items;
        PlanTree.IsVisible = true;
        ResultGrid.IsVisible = false;
        NoRecordsPanel.IsVisible = false;
    }

    private static TreeViewItem BuildPlanItem(PlanNode node, double selfTotal, bool useMs)
    {
        var self = useMs ? node.SelfMs ?? 0 : node.SelfCost;
        var fraction = selfTotal > 0 ? Math.Clamp(self / selfTotal, 0, 1) : 0;

        // 강조 팔레트는 diff 와 공유 (빨강=뜨거움 · 주황=주의 · 초록=정상) — 테마 종속
        var hot = ThemeBrushes.Get("DiffRemovedBrush", "#C62828");
        var warm = ThemeBrushes.Get("DiffChangedBrush", "#C77400");
        var cool = ThemeBrushes.Get("DiffAddedBrush", "#2E7D32");

        // DataGrip 식 비용 막대: 이 노드 자신이 전체에서 차지하는 몫
        var barFill = fraction >= 0.5 ? hot
                    : fraction >= 0.2 ? warm
                    : cool;
        var bar = new Border
        {
            Width = 56,
            Height = 9,
            CornerRadius = new Avalonia.CornerRadius(2),
            Background = ThemeBrushes.Get("PlanBarTrackBrush", "#DCDCDC"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 4, 0),
            Child = new Border
            {
                Width = Math.Max(fraction * 56, self > 0 ? 2 : 0),
                CornerRadius = new Avalonia.CornerRadius(2),
                Background = barFill,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            },
        };
        ToolTip.SetTip(bar, useMs
            ? $"self {self:0.###} ms — 전체 실행 시간의 {fraction:P0}"
            : $"self cost {self:0.##} — 전체 비용의 {fraction:P0}");
        var pct = new TextBlock
        {
            Text = $"{fraction * 100,3:0}%",
            FontSize = 10.5,
            Width = 30,
            Foreground = Avalonia.Media.Brushes.Gray,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = node.Title,
            FontWeight = fraction >= 0.2 ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.SemiBold,
            Foreground = fraction >= 0.5 ? hot
                       : fraction >= 0.2 ? warm
                       : ThemeBrushes.Get("TextPrimaryBrush", "#1A1A1A"),
        };
        var detail = new TextBlock
        {
            Text = "   " + node.Detail,
            Foreground = Avalonia.Media.Brushes.Gray,
            FontSize = 11.5,
        };
        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { bar, pct, title, detail },
        };

        // 행수 예측이 10배 이상 어긋난 노드 — 플랜이 틀어진 원인일 때가 많다
        if (node.RowsEstimateError is { } error && error >= 10)
        {
            var badge = new Border
            {
                Background = Avalonia.Media.Brushes.DarkOrange,
                CornerRadius = new Avalonia.CornerRadius(3),
                Padding = new Avalonia.Thickness(4, 0),
                Margin = new Avalonia.Thickness(6, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"rows ×{error:0}",
                    FontSize = 10.5,
                    Foreground = Avalonia.Media.Brushes.White,
                },
            };
            ToolTip.SetTip(badge,
                $"예측 {node.PlanRows:0} 행 vs 실제 {node.ActualRows:0} 행 — {error:0}배 어긋남.\n" +
                "통계가 오래됐거나(ANALYZE 필요) 조건 상관관계를 planner 가 모르는 경우입니다.");
            header.Children.Add(badge);
        }

        var item = new TreeViewItem { Header = header, IsExpanded = true };
        if (node.Extra is not null)
            ToolTip.SetTip(item, node.Extra);
        foreach (var child in node.Children)
            item.Items.Add(BuildPlanItem(child, selfTotal, useMs));
        return item;
    }

    /// <summary>F5/Shift+Enter: Golden 의 Run Script — 커서 위치 문장부터 끝까지 순차 실행.</summary>
    public Task RunScriptAsync()
    {
        List<SqlStatement> statements;
        if (!string.IsNullOrEmpty(Editor.SelectedText))
        {
            statements = StatementSplitter.Split(Editor.SelectedText, IsOracleSession);
        }
        else
        {
            var all = StatementSplitter.Split(Editor.Text ?? "", IsOracleSession);
            var at = StatementSplitter.StatementAt(Editor.Text ?? "", Editor.CaretOffset, IsOracleSession);
            var start = at is null ? 0 : all.FindIndex(s => s.Start == at.Start);
            statements = start <= 0 ? all : all.Skip(start).ToList();
        }
        return ExecuteStatementsAsync(statements, explain: false);
    }

    private async Task ExecuteStatementsAsync(List<SqlStatement> statements, bool explain)
    {
        if (_session is null)
        {
            SetInfo("Not connected");
            return;
        }
        if (statements.Count == 0)
            return;
        // 편집 모드는 그 쿼리를 다시 돌릴 때만 유지된다 — 다른 문장을 실행하면 읽기 전용으로
        if (_editSource is { } editSource &&
            !(statements.Count == 1 && statements[0].Text == editSource.Sql))
        {
            LeaveEditMode();
        }
        // Golden: 실행 중엔 새 실행 요청을 받지 않는다 (Cancel 만 가능)
        if (_executing)
        {
            SetInfo("Busy — statement still running. Cancel first.");
            return;
        }
        // 공유 세션이므로 다른 탭이 실행 중이면 기다려야 한다
        if (!_session.TryBeginRun(this))
        {
            SetInfo("Busy — another tab is running on this session.");
            return;
        }
        if (explain)
            statements = statements.Select(s => s with { Text = "EXPLAIN " + s.Text }).ToList();

        // Golden: :var 가 있으면 값을 묻는다 (이전 값 기억, 취소하면 실행 안 함)
        var variables = statements
            .SelectMany(s => BindVariables.Find(s.Text))
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (variables.Count > 0)
        {
            var dialog = new BindVariableDialog(variables, _bindValues);
            if (VisualRoot is Window owner)
                await dialog.ShowDialog(owner);
            else
                dialog.Show();
            if (dialog.Result is not { } values)
            {
                SetInfo("Cancelled — bind variables not entered");
                return;
            }
            foreach (var (name, value) in values)
                _bindValues[name] = value;
        }

        _executing = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        HideError();
        ClearResultArea();   // Golden: 실행 시작 즉시 이전 결과(헤더 포함) 제거
        _scriptWatch.Restart();
        _runTimer.Start();

        try
        {
            var affectedTotal = 0;
            var gridShown = false;
            var ran = 0;
            foreach (var stmt in statements)
            {
                if (_current is not null) { await _current.AbortAsync(); _current = null; }
                await _session.EnsureAliveAsync(ct);
                // Golden 의 수동 커밋 모드 — 단, PG 특성상 읽기 문장은 트랜잭션을 열지 않는다
                // (idle-in-transaction 이 VACUUM 을 방해). 변경 문장부터 BEGIN.
                if (!AutoCommit && !QuerySession.IsReadOnlyStatement(stmt.Text))
                    await _session.EnsureTransactionAsync(ct);

                SetInfo(statements.Count == 1
                    ? "Running single statement at cursor."
                    : $"Running statement {ran + 1} of {statements.Count}.", "", null);
                var binds = BindVariables.Find(stmt.Text)
                    .ToDictionary(v => v.Name, v => _bindValues.GetValueOrDefault(v.Name), StringComparer.OrdinalIgnoreCase);
                var query = await _session.ExecuteAsync(stmt.Text, ct, binds);
                _session.NoteStatement(stmt.Text);
                HistoryStore.Add(stmt.Text);
                _historyIndex = -1;
                _current = query;
                ran++;

                if (query.HasGrid)
                {
                    BindGrid(query.Columns);
                    gridShown = true;
                    LastGridSql = stmt.Text;
                    await FetchMoreAsync();

                    // Golden 기본처럼 실행 즉시 끝까지 가져온다 (옵션, 기본 꺼짐).
                    // 이러면 로드된 행 수 = 전체라 스크롤바가 정확해지고 COUNT 도 필요 없다
                    if (Options.FetchAllOnExecute)
                        await FetchUntilDoneAsync();
                    // 점진 fetch 로 둔 경우에만 전체 건수를 따로 센다 (옵션, 기본 꺼짐)
                    else if (Options.CountTotalRecords && QuerySession.IsReadOnlyStatement(stmt.Text))
                        _ = CountTotalAsync(stmt.Text);
                    AppendLog(stmt.Text, $"{_rows.Count:N0} row(s), {ScriptTime()}");
                }
                else
                {
                    affectedTotal += Math.Max(0, query.RowsAffected);
                    _current = null;
                    AppendLog(stmt.Text, $"{Math.Max(0, query.RowsAffected)} row(s) affected, {ScriptTime()}");
                    if (IsOracleSession)
                        await ReportOracleCompileErrorsAsync(stmt, ct);
                }
            }
            _scriptWatch.Stop();

            var message = $"Done, ran {ran} of {statements.Count} statements.";
            if (!gridShown)
            {
                BindGrid([]);
                SetInfo(message, $"{affectedTotal} row(s) affected", ScriptTime());
            }
            else
            {
                SetInfo(message, InfoRows, ScriptTime());
            }
        }
        catch (OperationCanceledException)
        {
            _scriptWatch.Stop();
            SetInfo("Cancelled", InfoRows, ScriptTime());
            AppendLog(statements[^1].Text, "Cancelled");
            await RecoverAsync();
        }
        catch (PostgresException ex)
        {
            _scriptWatch.Stop();
            ShowError($"{ex.Severity} {ex.SqlState}: {ex.MessageText}" +
                      (ex.Position > 0 ? $"  (position {ex.Position})" : ""));
            AppendLog(statements[^1].Text, $"{ex.Severity} {ex.SqlState}: {ex.MessageText}");
            await RecoverAsync();
        }
        catch (Exception ex)
        {
            _scriptWatch.Stop();
            ShowError(ex.Message);
            AppendLog(statements[^1].Text, $"Error: {ex.Message}");
            await RecoverAsync();
        }
        finally
        {
            _executing = false;
            _runTimer.Stop();
            _session?.EndRun(this);
        }
    }

    /// <summary>PL/Edit 3단계 — CREATE OR REPLACE 실행 뒤 USER_ERRORS 를 밑줄+Messages 로.
    /// 컴파일 대상 문장이 아니면(SELECT 등) 조용히 넘어간다.</summary>
    private async Task ReportOracleCompileErrorsAsync(SqlStatement stmt, CancellationToken ct)
    {
        if (_session is null || OracleCompileErrorParser.ParseObjectHeader(stmt.Text) is not { } header)
            return;
        var errors = await _session.GetOracleCompileErrorsAsync(header.Name, header.ObjectType, ct);
        if (errors.Count == 0)
            return;

        var issues = errors.Select(e => e.ToSqlIssue(stmt.Text, stmt.Start)).ToList();
        _errorRenderer.Issues = [.. _errorRenderer.Issues, .. issues];
        Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        foreach (var e in errors)
            AppendMessage($"{header.ObjectType} {header.Name}: line {e.Line}, col {e.Position}: {e.Text.Trim()}");
    }

    // ---------- 결과 보기 전환 (Golden: Show DataGrid / Show Text / Show Log) ----------

    private ResultViewMode _resultView = ResultViewMode.Grid;
    private readonly List<string> _log = [];

    /// <summary>
    /// Golden 툴바의 보기 드롭다운. Text/Log 패널은 결과 영역 위에 불투명하게 덮으므로
    /// 그리드·플랜·에러의 기존 표시 규칙은 그대로 둔다(에러는 여전히 맨 위에 뜬다).
    /// </summary>
    public ResultViewMode ResultView
    {
        get => _resultView;
        set
        {
            _resultView = value;
            ApplyResultView();
        }
    }

    private void ApplyResultView()
    {
        if (_resultView == ResultViewMode.Text)
        {
            var (columns, rows) = Snapshot();
            ResultText.Text = columns.Count == 0
                ? "결과 없음 — 쿼리를 실행하면 여기에 텍스트로 표시됩니다."
                : TextResultRenderer.Render(columns, rows);
        }
        else if (_resultView == ResultViewMode.Log)
        {
            LogText.Text = _log.Count == 0
                ? "로그 없음 — 이 탭에서 문장을 실행하면 기록됩니다."
                : string.Join('\n', _log);
        }

        TextPane.IsVisible = _resultView == ResultViewMode.Text;
        LogPane.IsVisible = _resultView == ResultViewMode.Log;
    }

    /// <summary>실행 기록 한 줄. 시각은 HH:mm:ss, SQL 은 첫 줄만 남긴다.</summary>
    private void AppendLog(string sql, string outcome)
    {
        var oneLine = sql.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length > 120) oneLine = oneLine[..117] + "…";
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {oneLine} — {outcome}");
        if (_resultView == ResultViewMode.Log) ApplyResultView();
    }

    /// <summary>
    /// 그리드에 행을 붙인다. DataGridCollectionView 로 감싸 **정렬·필터로 순서가 바뀔 때마다
    /// 맨 왼쪽 순번을 화면 순서로 다시 매긴다** — 그러지 않으면 정렬 후 1,2,3 이 뒤섞인다.
    /// </summary>
    private Avalonia.Collections.DataGridCollectionView? _gridView;

    private void SetGridSource(System.Collections.IEnumerable rows)
    {
        _gridView = new Avalonia.Collections.DataGridCollectionView(rows);
        ResultGrid.ItemsSource = _gridView;
    }

    /// <summary>
    /// 맨 왼쪽 순번은 **행이 그려지기 직전**에 화면 위치로 매긴다.
    ///
    /// 정렬이 끝난 뒤 따로 매기면 정렬된 행이 먼저 그려지고 번호가 나중에 고쳐져
    /// "숫자가 섞였다가 1로 돌아오는" 깜빡임이 생긴다. LoadingRow 는 그리기 직전이라
    /// 깜빡임이 없고, 가상화 덕에 보이는 행만 계산한다(대량 결과에도 싸다).
    /// </summary>
    private void OnResultRowLoading(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is RowItem row)
            row.Seq = e.Row.Index + 1;
    }

    /// <summary>실행 시작 시 이전 결과를 완전히 비운다 (컬럼 헤더 포함, No Records 도 숨김).</summary>
    private void ClearResultArea()
    {
        // 새 문장을 실행하면 이전 전체 건수는 무효 — 세던 것도 취소한다
        _countCts?.Cancel();
        _totalRecords = null;
        _columns = [];
        _rows = [];
        ResultGrid.Columns.Clear();
        ResultGrid.ItemsSource = null;
        ResultGrid.IsVisible = false;
        NoRecordsPanel.IsVisible = false;
        MessagesText.Text = "";
        MessagesPane.IsVisible = false;
        PlanTree.ItemsSource = null;
        PlanTree.IsVisible = false;
    }

    /// <summary>
    /// 이 탭의 접속을 다른 데이터베이스로 돌린다 (Mongo 처럼 한 접속으로 여러 DB 를
    /// 보는 경우). 성공하면 true. 드라이버가 지원하지 않으면 false 를 주고 상태만 알린다.
    /// </summary>
    public bool TryUseDatabase(string database)
    {
        if (_session is null || string.IsNullOrWhiteSpace(database)) return false;
        try
        {
            _session.Connection.ChangeDatabase(database);
            SetInfo($"Using {database}");
            return true;
        }
        catch (Exception ex)
        {
            SetInfo($"데이터베이스 전환 실패: {ex.Message}");
            return false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    // ---------- 그리드 기능 (Golden: Transpose / Size Columns / Filter) ----------

    /// <summary>행/열 전치 — 컬럼이 많은 한 행을 세로로 읽을 때 (Golden Transpose).</summary>
    public void ToggleTranspose()
    {
        if (_columns.Count == 0 || _rows.Count == 0)
        {
            SetInfo("No result to transpose");
            return;
        }
        if (_transposed)
        {
            RenderNormal();
            SetInfo("Transpose off");
            return;
        }

        var source = _rows.ToList();
        ResultGrid.Columns.Clear();
        ResultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Column",
            Binding = new Binding($"{nameof(RowItem.Cells)}[0]"),
            Width = new DataGridLength(200),
        });
        for (var r = 0; r < source.Count; r++)
        {
            ResultGrid.Columns.Add(new DataGridTextColumn
            {
                Header = $"row {source[r].No}",
                Binding = new Binding($"{nameof(RowItem.Cells)}[{r + 1}]"),
                Width = DataGridLength.Auto,
                MaxWidth = 420,
            });
        }
        var transposed = new ObservableCollection<RowItem>();
        for (var c = 0; c < _columns.Count; c++)
        {
            var cells = new string?[source.Count + 1];
            cells[0] = _columns[c];
            for (var r = 0; r < source.Count; r++)
                cells[r + 1] = source[r].Cells[c];
            transposed.Add(new RowItem(c + 1, cells));
        }
        ResultGrid.ItemsSource = transposed;
        _transposed = true;
        ResultGrid.CanUserSortColumns = false;   // 전치 상태는 정렬이 의미 없다
        SetInfo($"Transposed — {_columns.Count} column(s) × {source.Count} row(s)");
    }

    private void RenderNormal()
    {
        ResultGrid.Columns.Clear();
        var noColumn = new DataGridTextColumn
        {
            Header = "#",
            Binding = new Binding(nameof(RowItem.No)),
            Width = new DataGridLength(46),
            IsReadOnly = true,
        };
        noColumn.CellStyleClasses.Add("rownum");
        ResultGrid.Columns.Add(noColumn);
        for (var i = 0; i < _columns.Count; i++)
        {
            ResultGrid.Columns.Add(new DataGridTextColumn
            {
                Header = _columns[i],
                Binding = new Binding($"{nameof(RowItem.Cells)}[{i}]"),
                Width = DataGridLength.Auto,
                MaxWidth = 420,
            });
        }
        SetGridSource(_rows);
        _transposed = false;
    }

    /// <summary>모든 컬럼을 내용에 맞춘다 (Golden: Size All Columns to Fit).</summary>
    public void SizeColumnsToFit()
    {
        foreach (var column in ResultGrid.Columns)
        {
            if (column == ResultGrid.Columns[0] && !_transposed) continue;
            column.Width = DataGridLength.Auto;
        }
        SetInfo("Columns sized to fit");
    }

    /// <summary>선택 셀 값으로 WHERE 절을 만들어 에디터에 덧붙인다 (Golden: Filter records like selected cell).</summary>
    public void FilterBySelectedCell()
    {
        if (_transposed || ResultGrid.SelectedItem is not RowItem row || ResultGrid.CurrentColumn is not { } col)
        {
            SetInfo("Select a cell in the result grid first");
            return;
        }
        var index = ResultGrid.Columns.IndexOf(col) - 1;
        if (index < 0 || index >= _columns.Count) return;
        var value = row.Cells[index];
        var literal = value is null ? "IS NULL" : "= '" + value.Replace("'", "''") + "'";
        var clause = $"{_columns[index]} {literal}";
        Editor.Document.Insert(Editor.Document.TextLength, $"\n-- filter: WHERE {clause}\n");
        Editor.Focus();
        SetInfo($"Filter clause appended: {clause}");
    }

    // ---------- Golden 파리티: 그리드 필터 / Goto / Clear ----------

    /// <summary>필터가 걸려 있으면 원본 행을 여기 보관한다. null 이면 필터 없음.</summary>
    private ObservableCollection<RowItem>? _unfiltered;

    public bool HasFilter => _unfiltered is not null;

    /// <summary>
    /// Golden "Filter records like selected cell" 의 그리드 판 — 선택 셀과 같은 값의 행만 남긴다.
    /// 편집 모드·Transpose 중에는 행 대응이 깨지므로 막는다.
    /// </summary>
    public void FilterBySelectedCellInGrid()
    {
        if (_transposed || IsEditing)
        {
            SetInfo("Filter는 편집 모드·Transpose 중에는 쓸 수 없습니다");
            return;
        }
        if (ResultGrid.SelectedItem is not RowItem row || ResultGrid.CurrentColumn is not { } col)
        {
            SetInfo("Select a cell in the result grid first");
            return;
        }
        var index = ResultGrid.Columns.IndexOf(col) - 1;   // 0번은 행번호 컬럼
        if (index < 0 || index >= _columns.Count) return;

        var wanted = row.Cells[index];
        var source = _unfiltered ?? _rows;
        var kept = new ObservableCollection<RowItem>(
            source.Where(r => index < r.Cells.Length && r.Cells[index] == wanted));

        _unfiltered ??= _rows;
        _rows = kept;
        SetGridSource(_rows);
        SetInfo($"Filtered: {_columns[index]} = {wanted ?? "NULL"}", $"{kept.Count:N0} of {source.Count:N0} record(s)");
    }

    /// <summary>Golden "Clear Filter" — 필터를 풀고 원본 행으로 되돌린다.</summary>
    public void ClearFilter()
    {
        if (_unfiltered is null)
        {
            SetInfo("걸린 필터가 없습니다");
            return;
        }
        _rows = _unfiltered;
        _unfiltered = null;
        SetGridSource(_rows);
        SetInfo("Filter cleared", $"{_rows.Count:N0} record(s)");
    }

    /// <summary>Golden "Goto Record Number" (Ctrl+G) — 행 번호로 스크롤·선택.</summary>
    public void GotoRecord(int recordNo)
    {
        var target = _rows.FirstOrDefault(r => r.No == recordNo);
        if (target is null)
        {
            SetInfo($"Record {recordNo} 없음 (로드된 행 {_rows.Count:N0}개)");
            return;
        }
        ResultGrid.SelectedItem = target;
        ResultGrid.ScrollIntoView(target, null);
        ResultGrid.Focus();
        SetInfo($"Record {recordNo}");
    }

    /// <summary>Golden "Clear Spreadsheet" — 결과 영역만 비운다(에디터·로그는 그대로).</summary>
    public void ClearResults()
    {
        _unfiltered = null;
        ClearResultArea();
        ApplyResultView();
        SetInfo("Results cleared", "", "");
    }

    /// <summary>Golden F12 — DataGrid → Text → Log 순환.</summary>
    public void CycleResultView() => ResultView = ResultView switch
    {
        ResultViewMode.Grid => ResultViewMode.Text,
        ResultViewMode.Text => ResultViewMode.Log,
        _ => ResultViewMode.Grid,
    };

    /// <summary>Golden "Cell Details Window" (Ctrl+F11) — 선택 셀을 별도 창으로.</summary>
    public void ShowCellDetail()
    {
        if (ResultGrid.SelectedItem is not RowItem row || ResultGrid.CurrentColumn is not { } col)
        {
            SetInfo("Select a cell in the result grid first");
            return;
        }
        var index = ResultGrid.Columns.IndexOf(col) - 1;
        if (index < 0 || index >= _columns.Count) return;
        OpenCellDetail(_columns[index], row.No, row.Raw?[index] ?? row.Cells[index]);
    }

    /// <summary>Export 용 — 현재 로드된 행 (Transpose 여부와 무관하게 원본 순서).</summary>
    public (IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows) LoadedSnapshot() => Snapshot();

    // ---------- Cell detail (Golden 의 cell detail window, jsonb pretty-print) ----------

    private void OnCellDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ResultGrid.SelectedItem is not RowItem row || ResultGrid.CurrentColumn is not { } col)
            return;
        var index = ResultGrid.Columns.IndexOf(col) - 1;   // 0번은 행번호 컬럼
        if (index < 0 || index >= _columns.Count)
            return;
        var value = row.Raw?[index] ?? row.Cells[index];
        OpenCellDetail(_columns[index], row.No, value);
    }

    private void OpenCellDetail(string column, int rowNo, string? value)
    {
        var display = value;
        string? note = null;
        var trimmed = value?.TrimStart() ?? "";
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(value!);
                display = System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                note = "JSON pretty-printed";
            }
            catch { /* JSON 이 아니면 원문 그대로 */ }
        }

        var header = new TextBlock
        {
            Text = $"{column} · row {rowNo} · {value?.Length ?? 0:N0} chars" +
                   (note is null ? "" : $" · {note}"),
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Avalonia.Thickness(10, 8),
        };
        var text = new TextBox
        {
            Text = display ?? "(null)",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontFamily = new Avalonia.Media.FontFamily("Menlo, Consolas, Monaco, Courier New"),
            FontSize = 12.5,
            BorderThickness = new Avalonia.Thickness(0),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(text);

        var window = new Window
        {
            Title = $"Cell Detail — {column}",
            Width = 720,
            Height = 540,
            Content = dock,
        };
        if (VisualRoot is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    // ---------- Edit Document (Mongo, Studio3T 대응) ----------

    /// <summary>
    /// 선택한 행이 순수 <c>find</c> 결과(projection 없음)에서 왔으면 문서 편집 창을 연다.
    /// SQL 결과나 Mongo aggregate 결과는 <see cref="RowItem.MongoContext"/> 가 null 이라
    /// 자연히 비활성 상태와 같다(메뉴에서 안내만 하고 아무 일도 하지 않는다).
    /// </summary>
    public async Task EditSelectedDocumentAsync()
    {
        if (ResultGrid.SelectedItem is not RowItem { MongoContext: MongoRowContext context })
        {
            SetInfo("편집할 문서가 없습니다 — Mongo 컬렉션을 조회한 뒤 행을 선택하세요.");
            return;
        }
        if (_session?.Connection is not MongoDbConnection mongoConnection)
        {
            SetInfo("Mongo 접속이 아닙니다.");
            return;
        }

        var dialog = new MongoDocumentDialog(context.Document);
        if (VisualRoot is Window owner)
            await dialog.ShowDialog(owner);
        else
            return;
        if (dialog.Result is not { } updated) return;   // 취소

        try
        {
            await mongoConnection.ReplaceDocumentAsync(
                context.Database, context.Collection, context.Document["_id"], updated);
            SetInfo("문서를 저장했습니다 (다시 조회하면 최신 상태를 봅니다).");
        }
        catch (Exception ex)
        {
            SetInfo($"저장 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 이미 조회된 행 아무거나로 컬렉션 위치(Database+Collection)를 짐작해 그 컬렉션에
    /// 새 문서를 추가한다. 아직 조회한 적이 없으면(또는 결과 0건) 어느 컬렉션인지 알 수
    /// 없어 거부한다 — Explorer 로 컬렉션을 한 번 열어 본 뒤 쓰는 걸 전제한다.
    /// </summary>
    public async Task AddMongoDocumentAsync()
    {
        var context = _rows.Select(r => r.MongoContext).OfType<MongoRowContext>().FirstOrDefault();
        if (context is null)
        {
            SetInfo("추가할 컬렉션을 알 수 없습니다 — 먼저 Mongo 컬렉션을 조회하세요.");
            return;
        }
        if (_session?.Connection is not MongoDbConnection mongoConnection)
        {
            SetInfo("Mongo 접속이 아닙니다.");
            return;
        }

        var dialog = MongoDocumentDialog.ForNewDocument();
        if (VisualRoot is not Window owner) return;
        await dialog.ShowDialog(owner);
        if (dialog.Result is not { } document) return;   // 취소

        try
        {
            await mongoConnection.InsertDocumentAsync(context.Database, context.Collection, document);
            SetInfo($"{context.Collection} 에 문서를 추가했습니다 (다시 조회하면 보입니다).");
        }
        catch (Exception ex)
        {
            SetInfo($"추가 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 선택된 행들의 문서를 <c>_id</c> 로 하나씩 지운다. 성공한 행은 재조회 없이
    /// 그리드에서도 바로 뺀다. Mongo 문서가 아닌 행(SQL·aggregate 결과)은 건너뛴다.
    /// </summary>
    public async Task DeleteSelectedMongoDocumentsAsync()
    {
        var selected = ResultGrid.SelectedItems.OfType<RowItem>()
            .Where(r => r.MongoContext is MongoRowContext)
            .ToList();
        if (selected.Count == 0)
        {
            SetInfo("삭제할 Mongo 문서가 없습니다 — 행을 선택하세요.");
            return;
        }
        if (_session?.Connection is not MongoDbConnection mongoConnection)
        {
            SetInfo("Mongo 접속이 아닙니다.");
            return;
        }

        var deleted = 0;
        string? firstFailure = null;
        var failureCount = 0;
        foreach (var row in selected)
        {
            var context = (MongoRowContext)row.MongoContext!;
            try
            {
                await mongoConnection.DeleteDocumentAsync(
                    context.Database, context.Collection, context.Document["_id"]);
                _rows.Remove(row);
                deleted++;
            }
            catch (Exception ex)
            {
                firstFailure ??= ex.Message;
                failureCount++;
            }
        }

        SetInfo(failureCount == 0
            ? $"{deleted}개 문서를 지웠습니다."
            : $"{deleted}개 지움, {failureCount}개 실패: {firstFailure}");
    }

    // ---------- Commit / Rollback (Golden) ----------

    public async Task CommitAsync()
    {
        if (_session is null || !_session.InTransaction) return;
        if (_executing || _current is { Completed: false })
        {
            // Golden: "cannot commit transaction - SQL statements in progress"
            SetInfo("Cannot commit — statement still running/fetching. Cancel or Fetch All first.");
            return;
        }
        try
        {
            await _session.CommitAsync();
            SetInfo("Commit complete.");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    public async Task RollbackAsync()
    {
        if (_session is null || !_session.InTransaction) return;
        if (_executing || _current is { Completed: false })
        {
            SetInfo("Cannot rollback — statement still running/fetching. Cancel or Fetch All first.");
            return;
        }
        try
        {
            await _session.RollbackAsync();
            SetInfo("Rollback complete.");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task RecoverAsync()
    {
        if (_current is not null) { await _current.AbortAsync(); _current = null; }
        if (_session is not null)
        {
            try { await _session.EnsureAliveAsync(); }
            catch (Exception ex) { SetInfo($"Session lost: {ex.Message}"); }
        }
    }

    // ---------- Incremental fetch ----------

    private void BindGrid(IReadOnlyList<string> columns)
    {
        _columns = columns;
        _rows = [];
        _transposed = false;
        NoRecordsPanel.IsVisible = columns.Count == 0;
        ResultGrid.IsVisible = columns.Count > 0;
        ResultGrid.Columns.Clear();
        if (columns.Count > 0)
        {
            var noColumn = new DataGridTextColumn
            {
                Header = "#",
                // 화면 순서 순번 — 정렬해도 위에서부터 1,2,3… 을 유지한다
                Binding = new Binding(nameof(RowItem.Seq)),
                Width = new DataGridLength(46),
                IsReadOnly = true,
                // 순번 자체로는 정렬하지 않는다 (재번호와 맞물려 의미가 없다)
                CanUserSort = false,
            };
            noColumn.CellStyleClasses.Add("rownum");
            ResultGrid.Columns.Add(noColumn);

            // 편집 모드면 첫 컬럼은 행 식별자(ctid)라 감춘다
            var first = IsEditing ? 1 : 0;
            for (var i = first; i < columns.Count; i++)
            {
                ResultGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = columns[i],
                    // 편집 모드는 RowItem 인덱서 경로 — Cells[i](배열 인덱서)는 DataGrid 가
                    // 읽기 전용으로 판정해 셀 편집이 시작되지 않는다 (RowItem 인덱서 주석 참조)
                    Binding = new Binding(IsEditing ? $"[{i}]" : $"{nameof(RowItem.Cells)}[{i}]")
                    {
                        Mode = IsEditing ? BindingMode.TwoWay : BindingMode.OneWay,
                    },
                    // 인덱서 경로라 기본(경로 반사) 정렬이 안 먹는다 — 컬럼마다 비교자를 붙인다
                    CustomSortComparer = new CellComparer(i),
                    Width = DataGridLength.Auto,
                    // 거대한 값(JSONB 등)이 컬럼 폭 계산을 망가뜨리지 않게 상한
                    MaxWidth = 420,
                });
            }
        }
        // Golden 처럼 헤더 클릭으로 정렬. 단 **이미 fetch 된 행만** 정렬된다(점진 fetch).
        // 전치 상태는 행/열이 뒤바뀌어 의미가 없고, 편집 모드는 행 순서가 흔들리면 혼란스럽다.
        ResultGrid.CanUserSortColumns = !_transposed && !IsEditing;
        ResultGrid.IsReadOnly = !IsEditing;
        SetGridSource(_rows);
        if (columns.Count > 0)
            Dispatcher.UIThread.Post(HookScrollBar, DispatcherPriority.Loaded);
    }

    private async Task FetchMoreAsync()
    {
        if (_current is null || _current.Completed || _fetching || _cts is null)
            return;
        // 공유 세션: 다른 탭이 새 문장을 실행하면 이 결과의 reader 는 닫힌다
        if (_session is not null && !ReferenceEquals(_session.Current, _current))
        {
            SetInfo("Fetch stopped — another tab used this session.", InfoRows, InfoTime);
            _current = null;
            return;
        }
        _fetching = true;
        try
        {
            var want = FetchBatch;
            var limit = EffectiveRowLimit;
            if (limit > 0)
            {
                var remain = limit - _rows.Count;
                if (remain <= 0)
                {
                    SetInfo(InfoMessage, $"Fetched {_rows.Count:N0} records (limit reached)", InfoTime);
                    return;
                }
                want = Math.Min(want, remain);
            }
            var batch = await _current.FetchAsync(want, _cts.Token);
            var no = _rows.Count;
            foreach (var row in batch)
            {
                var cells = row.Cells;
                // 편집 모드에선 NULL 자리표시자를 넣지 않는다 — 그대로 DB 에 되쓰이면 곤란하다
                if (Options.NullText.Length > 0 && !IsEditing)
                {
                    cells = (string?[])cells.Clone();
                    for (var i = 0; i < cells.Length; i++)
                        cells[i] ??= Options.NullText;
                }
                _rows.Add(IsEditing
                    ? new RowItem(++no, cells, row.Raw)
                    {
                        RowId = cells.Length > 0 ? cells[0] : null,
                        Original = (string?[])cells.Clone(),
                        MongoContext = row.RowContext,
                    }
                    : new RowItem(++no, cells, row.Raw) { MongoContext = row.RowContext });
            }
            UpdateFetchInfo();
            // 행이 추가되어 스크롤바가 새로 생겼을 수 있다. 여전히 바닥이면 이어서 fetch (Golden 의 연속 로딩).
            Dispatcher.UIThread.Post(() =>
            {
                HookScrollBar();
                MaybeAutoFetch();
            }, DispatcherPriority.Background);
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>Ctrl+End — 끝까지 fetch (Golden 동작).</summary>
    /// <summary>
    /// 끝까지(또는 RecordsetLimit 까지) 가져온다.
    ///
    /// 상한에 걸리면 FetchMoreAsync 는 아무 행도 넣지 않고 돌아오지만 Completed 는
    /// 여전히 false 다 — 진행이 없으면 빠져나와야 무한 루프가 되지 않는다.
    /// </summary>
    /// <summary>
    /// 실제로 적용할 행 상한. 사용자가 무제한으로 뒀더라도 **풀 fetch 일 때는**
    /// 안전 상한을 건다 — 운영 DB 에서 수백만 행을 통째로 끌어오면 곤란하다.
    /// 0 이하면 무제한.
    /// </summary>
    private int EffectiveRowLimit =>
        Options.RecordsetLimit > 0 ? Options.RecordsetLimit
        : Options.FetchAllOnExecute ? AppOptions.FetchAllSafetyCap
        : 0;

    private async Task FetchUntilDoneAsync()
    {
        while (_current is { Completed: false } && _cts is { IsCancellationRequested: false })
        {
            var before = _rows.Count;
            await FetchMoreAsync();
            if (_rows.Count == before) break;   // 상한 도달 등 — 더 못 가져온다
            SetInfo("Fetching…", $"Fetched {_rows.Count:N0} records", InfoTime);
        }
    }

    public async Task FetchAllAsync()
    {
        if (_current is null || _cts is null || _executing) return;
        try
        {
            await FetchUntilDoneAsync();
            UpdateFetchInfo();
            SetInfo("Done.", InfoRows, InfoTime);
        }
        catch (OperationCanceledException)
        {
            SetInfo("Cancelled", $"Fetched {_rows.Count:N0} records", InfoTime);
            await RecoverAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            await RecoverAsync();
        }
    }

    // ---------- 전체 건수 (Golden 이 레코드 수를 따로 조회하는 방식) ----------

    private long? _totalRecords;
    private CancellationTokenSource? _countCts;

    private void UpdateFetchInfo()
    {
        if (_current is null) return;
        var more = _current.Completed ? "" : " (more)";
        var text = _totalRecords is { } total
            ? $"Fetched {_rows.Count:N0} of {total:N0} records"
            : $"Fetched {_rows.Count:N0} records{more}";
        SetInfo(InfoMessage, text, ScriptTime());
    }

    /// <summary>
    /// COUNT(*) 를 **별도 접속**으로 센다. 공유 세션은 reader 를 하나만 열 수 있어
    /// 여기서 문장을 던지면 진행 중인 결과 fetch 가 끊긴다.
    /// 실패·취소는 조용히 넘긴다 — 어디까지나 표시용이다.
    /// </summary>
    private async Task CountTotalAsync(string sql)
    {
        _countCts?.Cancel();
        _countCts = new CancellationTokenSource();
        var ct = _countCts.Token;
        _totalRecords = null;

        if (_session is null) return;
        var inner = sql.TrimEnd().TrimEnd(';');
        if (inner.Length == 0) return;

        try
        {
            await using var conn = await _session.Profile.OpenDbAsync(ct);
            await using var cmd = conn.CreateCommand();
            // 원본 문장을 그대로 감싼다 (별칭은 PG·Oracle·SQLite 모두 이 형태를 받는다)
            cmd.CommandText = $"select count(*) from ({inner}) aurum_count";
            var value = await cmd.ExecuteScalarAsync(ct);
            if (ct.IsCancellationRequested || value is null || value is DBNull) return;

            _totalRecords = Convert.ToInt64(value);
            UpdateFetchInfo();
        }
        catch
        {
            // 세지 못해도 결과 표시에는 지장이 없다 (권한·구문·시간 초과 등)
        }
    }

    private string ScriptTime() => $"Script: {_scriptWatch.Elapsed.TotalSeconds:0.000}s";

    private void HookScrollBar()
    {
        if (_vScroll is not null) return;
        _vScroll = ResultGrid.GetVisualDescendants()
            .OfType<ScrollBar>()
            .FirstOrDefault(sb => sb.Orientation == Orientation.Vertical);
        if (_vScroll is not null)
            _vScroll.ValueChanged += (_, _) => MaybeAutoFetch();
    }

    private void MaybeAutoFetch()
    {
        if (_vScroll is null || _current is null || _current.Completed || _fetching)
            return;
        if (_vScroll.Maximum - _vScroll.Value <= AutoFetchThreshold)
            _ = FetchMoreAsync();
    }

    /// <summary>스크린샷 모드 전용 — 그리드를 끝까지 스크롤해 자동 fetch 를 검증한다.</summary>
    internal async Task ScrollToBottomAsync()
    {
        HookScrollBar();
        for (var i = 0; i < 30 && _vScroll is not null; i++)
        {
            _vScroll.Value = _vScroll.Maximum;
            MaybeAutoFetch();
            await Task.Delay(150);
            if (_current is null || _current.Completed)
                break;
        }
    }

    /// <summary>스크린샷 모드 전용 — 접속 없이 화면을 채워 UI 를 점검한다.</summary>
    internal void PopulateSample()
    {
        Editor.Text =
            "select s.study_key,\n" +
            "       s.study_id,\n" +
            "       s.patient_id,\n" +
            "       s.study_dttm,\n" +
            "       s.modality\n" +
            "  from prismone.study s\n" +
            " where s.study_dttm >= '2026-07-01'\n" +
            " order by s.study_dttm desc;\n";
        BindGrid(["study_key", "study_id", "patient_id", "study_dttm", "modality"]);
        string[][] sample =
        [
            ["1001", "ST20260801-0007", "P000182", "2026-08-01 09:12:44", "CT"],
            ["1000", "ST20260801-0006", "P004417", "2026-08-01 08:55:02", "MR"],
            ["999", "ST20260731-0142", "P000731", "2026-07-31 17:20:11", "CR"],
            ["998", "ST20260731-0141", "P002204", "2026-07-31 16:58:37", "US"],
            ["997", "ST20260731-0140", "P000182", "2026-07-31 16:44:09", "CT"],
            ["996", "ST20260731-0139", "P003318", "2026-07-31 15:30:55", "DX"],
            ["995", "ST20260731-0138", "P001995", "2026-07-31 14:12:20", "MR"],
            ["994", "ST20260731-0137", "P000406", "2026-07-31 13:47:31", "CT"],
        ];
        var no = 0;
        foreach (var row in sample)
            _rows.Add(new RowItem(++no, row));
    }

    // ---------- Status ----------

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPane.IsVisible = true;
        SetInfo("Error — see message", InfoRows, ScriptTime());
    }

    private void HideError() => ErrorPane.IsVisible = false;

    private void SetInfo(string message, string? rows = null, string? time = null)
    {
        InfoMessage = message;
        if (rows is not null) InfoRows = rows;
        if (time is not null) InfoTime = time;
        InfoChanged?.Invoke(this);
    }
}
