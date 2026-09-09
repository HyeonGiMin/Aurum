using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// 결과 그리드를 다루는 화면 기능 — 보기 전환(Grid/Text/Log) · 전치 · 컬럼 맞춤 ·
/// 필터 · Goto · Clear. 조회 실행 흐름과 분리해 둔다.
/// </summary>
public partial class QueryTabView
{
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


}
