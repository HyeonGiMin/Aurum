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
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Npgsql;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>그리드 한 행: Golden 처럼 왼쪽에 행번호(No)를 붙인다. Raw 는 잘린 셀의 원문.</summary>
public sealed record RowItem(int No, string?[] Cells, string?[]? Raw = null);

/// <summary>
/// 쿼리 탭 하나 = 세션 하나 (Golden 의 쿼리 창 모델).
/// F9 로 커서 위치 문장을 실행하고, 결과는 배치 단위로 점진 fetch 한다.
/// 상태(message / rows / time / caret)는 이벤트로 메인 상태바에 올린다.
/// </summary>
public partial class QueryTabView : UserControl
{
    private const int FetchBatch = 100;   // Golden 기본값: 초기/배치 100행
    private const int AutoFetchThreshold = 60;

    private QuerySession? _session;
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

    /// <summary>자동완성용 테이블 카탈로그 — 접속 후 MainWindow 가 채워준다.</summary>
    public List<TableInfo> CompletionTables { get; set; } = [];

    /// <summary>Golden 기본: 수동 커밋 (false). true 면 PG 기본 autocommit 그대로.</summary>
    public bool AutoCommit { get; set; }

    public bool InTransaction => _session?.InTransaction == true;
    private readonly Stopwatch _scriptWatch = new();

    public string InfoMessage { get; private set; } = "Ready";
    public string InfoRows { get; private set; } = "";
    public string InfoTime { get; private set; } = "";
    public event Action<QueryTabView>? InfoChanged;
    public event Action<QueryTabView, int, int>? CaretChanged;

    public bool IsConnected => _session?.IsAlive == true;
    public string SessionDisplayName => _session?.Profile.DisplayName ?? "not connected";
    public ConnectionProfile? SessionProfile => _session?.Profile;

    /// <summary>마지막으로 그리드를 만든 문장 — COPY export 가 서버에서 다시 실행한다.</summary>
    public string? LastGridSql { get; private set; }

    /// <summary>바인드 변수 값 — Golden 처럼 탭 안에서 이전 값을 기억한다.</summary>
    private readonly Dictionary<string, string?> _bindValues = new(StringComparer.OrdinalIgnoreCase);

    private readonly AvaloniaEdit.Search.SearchPanel _search;

    public QueryTabView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = SqlHighlighting.Definition;
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
            items = SqlCompletion.IsTablePosition(text, wordStart)
                ? SqlCompletion.TablesOnly(CompletionTables)
                : SqlCompletion.General(CompletionTables);
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
        };
        foreach (var item in items)
            window.CompletionList.CompletionData.Add(item);
        window.Closed += (_, _) => _completion = null;
        _completion = window;
        window.Show();
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
                await using var conn = await _session.Profile.OpenAsync();
                columns = await SchemaCatalog.GetColumnsAsync(conn, table);
                _columnCache[key] = columns;
            }
            catch
            {
                return [];
            }
        }
        return columns
            .Select(c => new SqlCompletionItem(c.Name, c.Type + (c.Pk.Length > 0 ? " · PK" : ""), 4))
            .ToList();
    }

    // ---------- Session ----------

    /// <summary>Golden: 탭들은 메인 세션을 공유한다.</summary>
    public void AttachSession(QuerySession session, bool owned = false)
    {
        _session = session;
        _ownsSession = owned;
        session.NoticeReceived += line => Dispatcher.UIThread.Post(() => AppendMessage(line));
        SetInfo($"Session: {session.Profile.DisplayName}" + (owned ? " (private)" : ""));
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

    public void EditorCut() => Editor.Cut();
    public void EditorCopy() => Editor.Copy();
    public void EditorPaste() => Editor.Paste();
    public void EditorUndo() => Editor.Undo();
    public void EditorRedo() => Editor.Redo();

    public (IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows) Snapshot()
        => (_columns, _rows.Select(r => r.Cells).ToList());

    public bool HasResult => _columns.Count > 0 && _rows.Count > 0;

    // ---------- Execute ----------

    /// <summary>F9: 선택 영역(있으면) 또는 커서 위치 문장 하나를 실행.</summary>
    public Task ExecuteAtCaretAsync()
    {
        List<SqlStatement> statements;
        if (!string.IsNullOrEmpty(Editor.SelectedText))
        {
            statements = StatementSplitter.Split(Editor.SelectedText);
        }
        else
        {
            var stmt = StatementSplitter.StatementAt(Editor.Text ?? "", Editor.CaretOffset);
            statements = stmt is null ? [] : [stmt];
        }
        return ExecuteStatementsAsync(statements, explain: false);
    }

    /// <summary>커서 문장을 EXPLAIN (FORMAT JSON) 으로 실행해 플랜 트리로 표시.
    /// analyze=true 면 실제 실행 — DML 은 pgAdmin 처럼 자동 롤백으로 보호한다.</summary>
    public async Task ExecuteExplainAsync(bool analyze)
    {
        if (_session is null) { SetInfo("Not connected"); return; }
        if (_executing) { SetInfo("Busy — statement still running. Cancel first."); return; }
        var stmt = StatementSplitter.StatementAt(Editor.Text ?? "", Editor.CaretOffset);
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

    private void BindPlanTree(PlanResult plan, bool analyze)
    {
        var totalMs = plan.ExecutionMs ?? plan.Root.TotalMs ?? 0;
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
        items.Add(BuildPlanItem(plan.Root, totalMs));
        PlanTree.ItemsSource = items;
        PlanTree.IsVisible = true;
        ResultGrid.IsVisible = false;
        NoRecordsPanel.IsVisible = false;
    }

    private static TreeViewItem BuildPlanItem(PlanNode node, double totalMs)
    {
        var fraction = totalMs > 0 && node.TotalMs is { } ms ? ms / totalMs : 0;
        var title = new TextBlock
        {
            Text = node.Title,
            FontWeight = fraction >= 0.5 ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.SemiBold,
            Foreground = fraction >= 0.5 ? Avalonia.Media.Brushes.Firebrick
                       : fraction >= 0.2 ? Avalonia.Media.Brushes.Chocolate
                       : Avalonia.Media.Brushes.Black,
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
            Children = { title, detail },
        };
        var item = new TreeViewItem { Header = header, IsExpanded = true };
        if (node.Extra is not null)
            ToolTip.SetTip(item, node.Extra);
        foreach (var child in node.Children)
            item.Items.Add(BuildPlanItem(child, totalMs));
        return item;
    }

    /// <summary>F5/Shift+Enter: Golden 의 Run Script — 커서 위치 문장부터 끝까지 순차 실행.</summary>
    public Task RunScriptAsync()
    {
        List<SqlStatement> statements;
        if (!string.IsNullOrEmpty(Editor.SelectedText))
        {
            statements = StatementSplitter.Split(Editor.SelectedText);
        }
        else
        {
            var all = StatementSplitter.Split(Editor.Text ?? "");
            var at = StatementSplitter.StatementAt(Editor.Text ?? "", Editor.CaretOffset);
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
                }
                else
                {
                    affectedTotal += Math.Max(0, query.RowsAffected);
                    _current = null;
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
            await RecoverAsync();
        }
        catch (PostgresException ex)
        {
            _scriptWatch.Stop();
            ShowError($"{ex.Severity} {ex.SqlState}: {ex.MessageText}" +
                      (ex.Position > 0 ? $"  (position {ex.Position})" : ""));
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

    /// <summary>실행 시작 시 이전 결과를 완전히 비운다 (컬럼 헤더 포함, No Records 도 숨김).</summary>
    private void ClearResultArea()
    {
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

    public void Cancel() => _cts?.Cancel();

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
        NoRecordsPanel.IsVisible = columns.Count == 0;
        ResultGrid.IsVisible = columns.Count > 0;
        ResultGrid.Columns.Clear();
        if (columns.Count > 0)
        {
            var noColumn = new DataGridTextColumn
            {
                Header = "#",
                Binding = new Binding(nameof(RowItem.No)),
                Width = new DataGridLength(46),
                IsReadOnly = true,
            };
            noColumn.CellStyleClasses.Add("rownum");
            ResultGrid.Columns.Add(noColumn);

            for (var i = 0; i < columns.Count; i++)
            {
                ResultGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = columns[i],
                    Binding = new Binding($"{nameof(RowItem.Cells)}[{i}]"),
                    Width = DataGridLength.Auto,
                    // 거대한 값(JSONB 등)이 컬럼 폭 계산을 망가뜨리지 않게 상한
                    MaxWidth = 420,
                });
            }
        }
        ResultGrid.ItemsSource = _rows;
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
            var batch = await _current.FetchAsync(FetchBatch, _cts.Token);
            var no = _rows.Count;
            foreach (var row in batch)
                _rows.Add(new RowItem(++no, row.Cells, row.Raw));
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
    public async Task FetchAllAsync()
    {
        if (_current is null || _cts is null || _executing) return;
        try
        {
            while (!_current.Completed && !_cts.IsCancellationRequested)
            {
                await FetchMoreAsync();
                SetInfo("Fetching…", $"Fetched {_rows.Count:N0} records", InfoTime);
            }
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

    private void UpdateFetchInfo()
    {
        if (_current is null) return;
        var more = _current.Completed ? "" : " (more)";
        SetInfo(InfoMessage, $"Fetched {_rows.Count:N0} records{more}", ScriptTime());
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
