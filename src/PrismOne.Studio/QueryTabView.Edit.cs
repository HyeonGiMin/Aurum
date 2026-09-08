using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MongoDB.Bson;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;

namespace PrismOne.Studio;

/// <summary>
/// Run and Edit — Golden 의 EditMode (그리드에서 직접 고치기).
///
/// 행을 어떻게 찾느냐가 DB 마다 다르다: SQL 은 의사 컬럼(PG ctid · Oracle ROWID ·
/// SQLite rowid)을 앞에 붙여 쓰고, Mongo 는 <c>_id</c> 와 원본 문서를 쓴다.
/// 반영은 Golden DBNavigator 처럼 두 단계다 — ✓ Post 로 보내고 상단 Commit/Rollback 으로
/// 확정·취소 (Mongo 는 트랜잭션이 없어 Post 가 곧 확정이다).
/// </summary>
public partial class QueryTabView
{
    // ---------- Run and Edit (Golden EditMode) ----------

    /// <summary>편집 모드일 때의 원본 쿼리·대상 테이블. null 이면 읽기 전용.</summary>
    private EditableQuery? _editSource;

    /// <summary>삭제 표시된 행 (Submit 전까지 DB 에는 반영되지 않는다).</summary>
    private readonly List<RowItem> _deletedRows = [];

    public bool IsEditing => _editSource is not null;

    /// <summary>편집 대상 테이블 — 상태 표시용.</summary>
    public string? EditTable => _editSource?.Table;

    /// <summary>
    /// Mongo 편집 모드인가. SQL 편집과 달리 0번 컬럼이 의사 컬럼이 아니라 <c>_id</c> 이고,
    /// 행 식별은 <see cref="RowItem.MongoContext"/> 가 맡는다.
    /// </summary>
    private bool _mongoEdit;

    public int PendingEditCount => CollectChanges().Count;

    /// <summary>그리드에서 선택된 행 수 — 삭제 확인 문구에 쓴다.</summary>
    public int SelectedRowCount => ResultGrid.SelectedItems.OfType<RowItem>().Count();

    /// <summary>
    /// Golden 의 Run and Edit — 커서 문장(또는 선택 영역)을 행 식별자(ctid/ROWID/rowid)를
    /// 붙여 다시 실행하고 결과 그리드를 편집 가능한 상태로 만든다. 단일 테이블 SELECT 만 가능.
    /// </summary>
    public async Task<bool> RunAndEditAsync()
    {
        if (_session is null)
        {
            SetInfo("Not connected");
            return false;
        }
        // Mongo 는 의사 컬럼(ctid/ROWID)이 아니라 _id 로 행을 찾는다 — 별도 경로
        if (_session.Connection is MongoDbConnection)
            return await RunAndEditMongoAsync();

        var provider = _session.Profile.Provider;
        if (!provider.Capabilities.GridEditing)
        {
            SetInfo($"Run and Edit: {provider.DisplayName} 는 그리드 편집을 지원하지 않습니다");
            return false;
        }
        var sql = StatementForFavorite();
        if (GridEditor.Prepare(sql, provider) is not { } prepared)
        {
            SetInfo("Run and Edit: 단일 테이블 SELECT 만 편집할 수 있습니다 (조인·집계·DISTINCT 불가)");
            return false;
        }
        // 헤더 클릭으로 정렬해 둔 상태를 편집 모드로 가져간다. 편집 SELECT 는 행 식별자가
        // 0번 컬럼으로 앞에 붙으므로 읽기 전용 그리드에서 잡은 인덱스는 하나 밀린다.
        // 편집 중에 같은 문장을 다시 돌리면 잡아둔 정렬을 그대로 쓰고, 다른 문장이면 버린다.
        if (!IsEditing)
            _editSort = CurrentHeaderSort() is { } sort ? (sort.Index + 1, sort.Direction) : null;
        else if (_editSource!.Sql != prepared.Sql)
            _editSort = null;
        _editSource = prepared;
        _deletedRows.Clear();
        await ExecuteStatementsAsync([new SqlStatement(prepared.Sql, 0, prepared.Sql.Length)], explain: false);
        if (_editSource is not null)
            SetInfo($"EditMode: {prepared.Table} — 셀을 고치고 ✓(Post) → Commit(Ctrl+F5) 으로 확정하세요");
        return true;
    }

    /// <summary>
    /// Mongo 의 Run and Edit. SQL 을 다시 쓰지 않고 원래 find 를 그대로 돌린 뒤,
    /// 행마다 딸려 오는 원본 문서(<see cref="MongoRowContext"/>)로 편집한다.
    /// projection·aggregate 결과는 원본이 없어 거부한다.
    ///
    /// Mongo 에는 (replica set 없이) 트랜잭션이 없어 <b>Post 하면 바로 반영</b>된다 —
    /// SQL 처럼 Commit/Rollback 으로 되돌릴 수 없다.
    /// </summary>
    private async Task<bool> RunAndEditMongoAsync()
    {
        var sql = StatementForFavorite();
        if (string.IsNullOrWhiteSpace(sql))
        {
            SetInfo("Run and Edit: 실행할 문장이 없습니다");
            return false;
        }

        _mongoEdit = true;
        _editSource = new EditableQuery("(mongo)", sql);
        _editSort = null;
        _deletedRows.Clear();
        await ExecuteStatementsAsync([new SqlStatement(sql, 0, sql.Length)], explain: false);
        if (_editSource is null)
            return false;   // 실행이 편집 모드를 풀었다 (오류 등)

        if (_rows.Select(r => r.MongoContext).OfType<MongoRowContext>().FirstOrDefault() is not { } context)
        {
            LeaveEditMode();
            SetInfo("Run and Edit: 원본 문서를 알 수 있는 find 결과만 편집할 수 있습니다 "
                  + "(projection·aggregate 는 불가)");
            return false;
        }

        _editSource = new EditableQuery($"{context.Database}.{context.Collection}", sql);
        EditBarLabel.Text = $"Editing: {EditTable}";
        SetInfo($"EditMode: {EditTable} — Mongo 는 트랜잭션이 없어 ✓(Post) 하면 즉시 반영됩니다");
        return true;
    }

    /// <summary>편집 모드 해제 (일반 실행으로 돌아갈 때).</summary>
    private void LeaveEditMode()
    {
        _editSource = null;
        _editSort = null;
        _mongoEdit = false;
        _deletedRows.Clear();
        ResultGrid.IsReadOnly = true;
        EditBar.IsVisible = false;
    }

    /// <summary>선택한 행들을 삭제 표시하고 그리드에서 감춘다. 실제 DELETE 는 Submit 때.</summary>
    public int MarkSelectedRowsDeleted()
    {
        if (!IsEditing)
            return 0;
        var selected = ResultGrid.SelectedItems.OfType<RowItem>().ToList();
        foreach (var row in selected)
        {
            // SQL 은 RowId(ctid/ROWID), Mongo 는 원본 문서로 지울 행을 찾는다
            if (row.RowId is not null || row.MongoContext is not null)
                _deletedRows.Add(row);
            _rows.Remove(row);
        }
        if (selected.Count > 0)
            SetInfo($"EditMode: {selected.Count} record(s) marked for delete — Post(✓) 하면 반영됩니다");
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
        SetInfo("EditMode: 새 행 추가 — 값을 넣고 Post(✓) 하세요");
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
        SetInfo($"EditMode: Paste inserted {pasted.Count} records. — Post(✓) 하면 반영됩니다");
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
    public Task SubmitEditsAsync() => SubmitEditsAsync(forceCommit: false);

    /// <param name="forceCommit">true 면 수동 커밋 모드여도 제출 직후 COMMIT (편집 바의 ✓).</param>
    public async Task SubmitEditsAsync(bool forceCommit)
    {
        // 첫 fetch 가 아직 도는 동안 편집 바가 먼저 보인다 — 같은 접속에 두 작업을 겹치지 않게 막는다
        if (_executing)
        {
            SetInfo("Busy — statement still running. Cancel first.");
            return;
        }
        // 셀 편집 중이던 값이 있으면 먼저 확정한다 — 편집 바 클릭은 포커스를 옮기지 않는다
        ResultGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_editSource is not { } source)
        {
            SetInfo("편집 모드가 아닙니다 (Run and Edit)");
            return;
        }
        if (_mongoEdit)
        {
            await SubmitMongoEditsAsync();
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
            statements = GridEditor.Build(source.Table, changes, _session.Profile.Provider);
        }
        catch (ArgumentException ex)
        {
            SetInfo($"EditMode: {ex.Message}");
            return;
        }

        await AbortPendingFetchAsync();

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

        if (AutoCommit || forceCommit)
            await _session.CommitAsync();

        var pending = AutoCommit || forceCommit ? "" : " — Commit(Ctrl+F5) 으로 확정, Rollback(Ctrl+F6) 으로 취소";
        await RevertEditsAsync();   // ctid 가 바뀌었을 수 있으니 다시 읽는다
        SetInfo($"EditMode: {applied} change(s) posted{pending}");
    }

    /// <summary>
    /// Mongo 편집 제출. SQL 과 달리 <b>한 트랜잭션으로 묶을 수 없다</b> —
    /// 문서 하나씩 반영되고, 중간에 실패하면 그 앞까지는 이미 들어간 채로 멈춘다.
    /// (Mongo 의 다중 문서 트랜잭션은 replica set 이 필요하다.)
    /// </summary>
    private async Task SubmitMongoEditsAsync()
    {
        if (_session?.Connection is not MongoDbConnection mongo)
        {
            SetInfo("Mongo 접속이 아닙니다");
            return;
        }

        var updates = new List<(MongoRowContext Context, BsonDocument Update)>();
        var inserts = new List<BsonDocument>();
        var deletes = _deletedRows.Select(r => r.MongoContext).OfType<MongoRowContext>().ToList();

        // 값 → BSON 변환은 여기서 다 끝낸다. 하나라도 타입이 어긋나면 아무것도 보내지 않는다
        // (부분 반영을 되돌릴 수 없으므로 나가기 전에 막는 게 유일한 방어선이다)
        try
        {
            foreach (var row in _rows)
            {
                if (row.MongoContext is not MongoRowContext context)
                {
                    var filled = CellPairs(row, onlyFilled: true);
                    if (filled.Count > 0)
                        inserts.Add(MongoGridEditor.BuildInsert(filled));
                    continue;
                }
                if (row.Original is not { } original)
                    continue;
                var edited = new List<(string, string?)>();
                for (var i = 0; i < _columns.Count && i < row.Cells.Length; i++)
                {
                    if (row.Cells[i] != original[i])
                        edited.Add((_columns[i], NormalizeCell(row.Cells[i])));
                }
                if (edited.Count > 0)
                    updates.Add((context, MongoGridEditor.BuildUpdate(context.Document, edited)));
            }
        }
        catch (ArgumentException ex)
        {
            SetInfo($"EditMode: {ex.Message}");
            return;
        }

        if (updates.Count + inserts.Count + deletes.Count == 0)
        {
            SetInfo("EditMode: 변경된 내용이 없습니다");
            return;
        }

        // 새 문서를 넣을 컬렉션은 지금 보고 있는 그 컬렉션이다
        var target = _rows.Select(r => r.MongoContext).OfType<MongoRowContext>().FirstOrDefault()
                     ?? deletes.FirstOrDefault();
        if (inserts.Count > 0 && target is null)
        {
            SetInfo("EditMode: 새 문서를 넣을 컬렉션을 알 수 없습니다 — 먼저 조회하세요");
            return;
        }

        var applied = 0;
        try
        {
            foreach (var (context, update) in updates)
            {
                await mongo.UpdateDocumentAsync(
                    context.Database, context.Collection, context.Document[MongoGridEditor.IdField], update);
                applied++;
            }
            foreach (var context in deletes)
            {
                await mongo.DeleteDocumentAsync(
                    context.Database, context.Collection, context.Document[MongoGridEditor.IdField]);
                applied++;
            }
            foreach (var document in inserts)
            {
                await mongo.InsertDocumentAsync(target!.Database, target.Collection, document);
                applied++;
            }
        }
        catch (Exception ex)
        {
            await RevertEditsAsync();
            SetInfo($"EditMode 실패: {ex.Message} — 앞선 {applied}건은 이미 반영됐습니다 "
                  + "(Mongo 는 되돌릴 수 없습니다)");
            return;
        }

        await RevertEditsAsync();
        SetInfo($"EditMode: {applied} change(s) applied — Mongo 는 트랜잭션이 없어 즉시 반영됩니다");
    }

    /// <summary>행의 (컬럼, 값) 쌍. Mongo 새 문서용 — 빈 칸은 넣지 않는다.</summary>
    private List<(string, string?)> CellPairs(RowItem row, bool onlyFilled)
    {
        var pairs = new List<(string, string?)>();
        for (var i = 0; i < _columns.Count && i < row.Cells.Length; i++)
        {
            if (!onlyFilled || !string.IsNullOrEmpty(row.Cells[i]))
                pairs.Add((_columns[i], row.Cells[i]));
        }
        return pairs;
    }

    /// <summary>
    /// 편집 바의 ✓ (Golden DBNavigator 의 Post) — 고친 내용을 트랜잭션 안에서 DB 로 보낸다.
    /// 확정은 상단 Commit(Ctrl+F5), 취소는 Rollback(Ctrl+F6). AutoCommit 이면 곧바로 확정된다.
    /// </summary>
    public Task PostEditsAsync() => SubmitEditsAsync(forceCommit: false);

    /// <summary>
    /// 편집 바의 ✗ (Golden 의 Cancel) — 아직 Post 하지 않은 변경만 버리고 다시 조회한다.
    /// 이미 Post 한 것은 트랜잭션에 남아 있으므로 Rollback(Ctrl+F6) 으로 되돌린다.
    /// </summary>
    public async Task CancelEditsAsync()
    {
        if (_executing)
        {
            SetInfo("Busy — statement still running. Cancel first.");
            return;
        }
        if (!HasPendingEdits())
        {
            var posted = _session is { InTransaction: true } ? " — Post 한 내용은 Rollback(Ctrl+F6) 으로 되돌립니다" : "";
            SetInfo("EditMode: 취소할 변경이 없습니다" + posted);
            return;
        }
        await AbortPendingFetchAsync();
        await RevertEditsAsync();
    }

    // ---------- 편집 모드의 정렬 유지 ----------

    /// <summary>헤더 클릭으로 마지막에 정렬한 셀 인덱스. 새 결과가 바인딩되면 -1.</summary>
    private int _sortedCellIndex = -1;

    /// <summary>
    /// 편집 모드에 들어갈 때 잡아둔 정렬. 편집 그리드는 헤더 정렬을 막아 두므로(행이 흔들리면
    /// 혼란) 재조회(Post·Cancel·Rollback)마다 메모리에서 한 번 정렬해 순서를 유지한다.
    /// </summary>
    private (int Index, System.ComponentModel.ListSortDirection Direction)? _editSort;

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e) =>
        _sortedCellIndex = (e.Column.CustomSortComparer as CellComparer)?.Index ?? -1;

    /// <summary>지금 그리드에 걸린 헤더 정렬 (셀 인덱스 + 방향). 없으면 null.</summary>
    private (int Index, System.ComponentModel.ListSortDirection Direction)? CurrentHeaderSort()
    {
        if (_sortedCellIndex < 0 || _gridView is null || _gridView.SortDescriptions.Count == 0)
            return null;
        return (_sortedCellIndex, _gridView.SortDescriptions[0].Direction);
    }

    /// <summary>편집 모드로 읽어 온 행을 기억해 둔 정렬대로 다시 세운다 (안정 정렬, 순번은 화면 순서).</summary>
    private void ApplyEditSort()
    {
        if (_editSort is not { } sort || sort.Index >= _columns.Count || _rows.Count < 2)
            return;
        var comparer = new CellComparer(sort.Index);
        var byCell = Comparer<RowItem>.Create((a, b) => comparer.Compare(a, b));
        var ordered = sort.Direction == System.ComponentModel.ListSortDirection.Ascending
            ? _rows.OrderBy(r => r, byCell)
            : _rows.OrderByDescending(r => r, byCell);
        _rows = new ObservableCollection<RowItem>(ordered);
        SetGridSource(_rows);
        var arrow = sort.Direction == System.ComponentModel.ListSortDirection.Ascending ? "▲" : "▼";
        EditBarLabel.Text = $"Editing: {EditTable} · sorted by {_columns[sort.Index]} {arrow}";
    }

    /// <summary>편집 중이던 셀을 행에 확정한 뒤, 아직 Post 하지 않은 변경이 있는지 본다.</summary>
    private bool HasPendingEdits()
    {
        ResultGrid.CommitEdit(DataGridEditingUnit.Row, true);
        return CollectChanges().Count > 0;
    }

    /// <summary>점진 fetch 가 reader 를 물고 있으면 트랜잭션 명령을 보낼 수 없다 — 먼저 닫는다.</summary>
    private async Task AbortPendingFetchAsync()
    {
        if (_current is { Completed: false })
        {
            await _current.AbortAsync();
            _current = null;
        }
    }

    // ---------- EditBar (Golden 의 DBNavigator 줄) ----------

    /// <summary>행 이동. int.MinValue = 처음, int.MaxValue = 끝.</summary>
    private void NavigateRow(int delta)
    {
        if (_rows.Count == 0)
            return;
        var next = delta switch
        {
            int.MinValue => 0,
            int.MaxValue => _rows.Count - 1,
            _ => Math.Clamp(ResultGrid.SelectedIndex + delta, 0, _rows.Count - 1),
        };
        ResultGrid.SelectedIndex = next;
        ResultGrid.ScrollIntoView(_rows[next], null);
    }

    private void OnEditNavFirst(object? sender, RoutedEventArgs e) => NavigateRow(int.MinValue);
    private void OnEditNavPrev(object? sender, RoutedEventArgs e) => NavigateRow(-1);
    private void OnEditNavNext(object? sender, RoutedEventArgs e) => NavigateRow(+1);
    private void OnEditNavLast(object? sender, RoutedEventArgs e) => NavigateRow(int.MaxValue);
    private void OnEditAddRow(object? sender, RoutedEventArgs e) => AddInsertRow();
    private async void OnEditPasteRows(object? sender, RoutedEventArgs e) => await PasteRowsAsync();
    private async void OnEditPost(object? sender, RoutedEventArgs e) => await PostEditsAsync();
    private async void OnEditCancel(object? sender, RoutedEventArgs e) => await CancelEditsAsync();
    private async void OnEditRefresh(object? sender, RoutedEventArgs e) => await RefreshEditsAsync();

    /// <summary>Delete record — Golden 처럼 "Delete N selected records?" 를 묻고 삭제 표시한다.</summary>
    private async void OnEditDeleteRows(object? sender, RoutedEventArgs e)
    {
        var count = SelectedRowCount;
        if (count == 0)
        {
            SetInfo("삭제할 행을 선택하세요");
            return;
        }
        if (VisualRoot is Window owner && await ConfirmDialog.ShowAsync(owner, $"Delete {count} selected records?"))
            MarkSelectedRowsDeleted();
    }

    /// <summary>Edit record (DBNavigator) — 현재 셀 편집을 시작한다. 선택이 없으면 첫 행부터.</summary>
    private void OnEditRecord(object? sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0 || ResultGrid.Columns.Count < 2)
            return;
        if (ResultGrid.SelectedIndex < 0)
            ResultGrid.SelectedIndex = 0;
        ResultGrid.CurrentColumn ??= ResultGrid.Columns[1];   // 0번은 순번 컬럼
        ResultGrid.Focus();
        ResultGrid.BeginEdit();
    }

    /// <summary>Refresh data (DBNavigator) — 다시 조회한다. Post 전 변경이 있으면 버려도 되는지 묻는다.</summary>
    public async Task RefreshEditsAsync()
    {
        if (_executing)
        {
            SetInfo("Busy — statement still running. Cancel first.");
            return;
        }
        if (HasPendingEdits() &&
            !(VisualRoot is Window owner && await ConfirmDialog.ShowAsync(owner, "Discard unposted changes and refresh?")))
            return;
        await AbortPendingFetchAsync();
        await RevertEditsAsync();
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
}
