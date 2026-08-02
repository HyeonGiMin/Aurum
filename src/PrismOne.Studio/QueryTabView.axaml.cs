using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
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

/// <summary>그리드 한 행: Golden 처럼 왼쪽에 행번호(No)를 붙인다.</summary>
public sealed record RowItem(int No, string?[] Cells);

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
    private ActiveQuery? _current;
    private CancellationTokenSource? _cts;
    private ObservableCollection<RowItem> _rows = [];
    private IReadOnlyList<string> _columns = [];
    private ScrollBar? _vScroll;
    private bool _fetching;
    private bool _executing;   // 실행 중 재실행 요청 무시 (연타 시 세션 충돌 방지)
    private readonly DispatcherTimer _runTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _scriptWatch = new();

    public string InfoMessage { get; private set; } = "Ready";
    public string InfoRows { get; private set; } = "";
    public string InfoTime { get; private set; } = "";
    public event Action<QueryTabView>? InfoChanged;
    public event Action<QueryTabView, int, int>? CaretChanged;

    public bool IsConnected => _session?.IsAlive == true;
    public string SessionDisplayName => _session?.Profile.DisplayName ?? "not connected";

    public QueryTabView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = SqlHighlighting.Definition;
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
    }

    // ---------- Session ----------

    public async Task ConnectAsync(ConnectionProfile profile)
    {
        try
        {
            _session = await QuerySession.CreateAsync(profile);
            SetInfo($"Session: {profile.DisplayName}");
        }
        catch (Exception ex)
        {
            SetInfo($"Connect failed: {ex.Message}");
        }
    }

    public async Task CloseSessionAsync()
    {
        _cts?.Cancel();
        if (_current is not null) { await _current.AbortAsync(); _current = null; }
        if (_session is not null) { await _session.DisposeAsync(); _session = null; }
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
    public void EditorCut() => Editor.Cut();
    public void EditorCopy() => Editor.Copy();
    public void EditorPaste() => Editor.Paste();
    public void EditorUndo() => Editor.Undo();
    public void EditorRedo() => Editor.Redo();

    public (IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows) Snapshot()
        => (_columns, _rows.Select(r => r.Cells).ToList());

    public bool HasResult => _columns.Count > 0 && _rows.Count > 0;

    // ---------- Execute ----------

    /// <summary>F9: 선택 영역(있으면) 또는 커서 위치 문장 하나를 실행. explain=true 면 EXPLAIN.</summary>
    public Task ExecuteAtCaretAsync(bool explain = false)
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
        return ExecuteStatementsAsync(statements, explain);
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
        if (explain)
            statements = statements.Select(s => s with { Text = "EXPLAIN " + s.Text }).ToList();

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

                SetInfo(statements.Count == 1
                    ? "Running single statement at cursor."
                    : $"Running statement {ran + 1} of {statements.Count}.", "", null);
                var query = await _session.ExecuteAsync(stmt.Text, ct);
                _current = query;
                ran++;

                if (query.HasGrid)
                {
                    BindGrid(query.Columns);
                    gridShown = true;
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
    }

    public void Cancel() => _cts?.Cancel();

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
        _fetching = true;
        try
        {
            var batch = await _current.FetchAsync(FetchBatch, _cts.Token);
            var no = _rows.Count;
            foreach (var row in batch)
                _rows.Add(new RowItem(++no, row));
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
