using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MongoDB.Bson;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;

namespace PrismOne.Studio;

/// <summary>
/// 결과 그리드 위에서 값을 들여다보는 기능들 — 찾기 · 셀 상세 창 · Mongo 문서 편집.
/// 실행·fetch 흐름과 섞이면 읽기 어려워 따로 뒀다.
/// </summary>
public partial class QueryTabView
{
    // ---------- Find in Results (Golden 의 "Find in Results…") ----------

    /// <summary>마지막으로 찾은 자리 — Find Next 가 여기서 이어간다.</summary>
    private GridHit? _lastHit;

    /// <summary>지금 그리드에 보이는 행 수 (못 찾았을 때 "받은 N행 안에는 없다"고 알리려고).</summary>
    public int LoadedRowCount => _rows.Count;

    /// <summary>
    /// 이미 가져온 셀에서 찾아 그 칸을 선택하고 스크롤한다.
    /// 아직 fetch 하지 않은 행은 UI 에 없으므로 못 찾는다 — 그 사실을 호출자가 알린다.
    /// </summary>
    /// <returns>찾았으면 true.</returns>
    public bool FindInResults(string term, bool matchCase, bool wholeCell, bool backwards, bool fromStart)
    {
        // 전치 상태에서는 그리드에 묶인 것이 _rows 가 아니라 행/열을 뒤집은 별도 컬렉션이라
        // 여기서 찾은 자리를 그대로 선택하면 엉뚱한 칸을 잡는다 (필터도 같은 이유로 막는다)
        if (_transposed || _rows.Count == 0 || string.IsNullOrEmpty(term))
            return false;

        // SQL 편집 모드는 0번이 감춘 행 식별자(ctid/ROWID)다 — 검색에서 빼야 식별자가
        // 걸리지도 않고, 남은 셀 인덱스가 그리드 컬럼과 정확히 한 칸 차이로 맞는다.
        // (Mongo 편집은 감춘 컬럼이 없다.)
        var hidden = IsEditing && !IsMongoEdit ? 1 : 0;
        var cells = _rows
            .Select(r => r.Cells.Length > hidden ? r.Cells[hidden..] : [])
            .ToList();
        var from = fromStart ? null : _lastHit;
        var hit = GridSearch.FindNext(
            cells, term,
            from?.Row ?? -1, from?.Column ?? -1,
            matchCase, wholeCell, backwards);
        if (hit is not { } found)
            return false;

        _lastHit = found;
        ResultGrid.SelectedIndex = found.Row;
        // 0번은 순번 컬럼이라 셀 인덱스와 한 칸 어긋난다
        var column = found.Column + 1;
        if (column < ResultGrid.Columns.Count)
            ResultGrid.CurrentColumn = ResultGrid.Columns[column];
        ResultGrid.ScrollIntoView(_rows[found.Row], ResultGrid.CurrentColumn);
        return true;
    }

    /// <summary>받은 행 중 몇 칸이 걸리는지 — 찾기 창의 안내용.</summary>
    public int CountInResults(string term, bool matchCase, bool wholeCell)
    {
        var hidden = IsEditing && !IsMongoEdit ? 1 : 0;
        var cells = _rows.Select(r => r.Cells.Length > hidden ? r.Cells[hidden..] : []).ToList();
        return GridSearch.Count(cells, term, matchCase, wholeCell);
    }

    /// <summary>
    /// 지금 찾기를 쓸 수 있는가. 전치 상태에서는 그리드에 묶인 컬렉션이 달라 자리를
    /// 그대로 옮길 수 없다 — 찾기 창이 이유를 알리도록 밖으로 낸다.
    /// </summary>
    public bool CanFindInResults => !_transposed;

    /// <summary>Export 용 — 현재 로드된 행 (Transpose 여부와 무관하게 원본 순서).</summary>
    public (IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows) LoadedSnapshot() => Snapshot();

    // ---------- Cell detail (Golden 의 cell detail window, jsonb pretty-print) ----------

    /// <summary>열려 있는 셀 상세 창 — 하나만 두고 내용을 바꿔 끼운다 (더블클릭마다 쌓이지 않게).</summary>
    private Window? _cellDetail;

    private void OnCellDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        // 편집 모드에서 더블클릭은 셀 편집 시작이다 — 상세 창은 Ctrl+F11 로만
        if (IsEditing)
            return;
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

        if (_cellDetail is { } open)
        {
            open.Title = $"Cell Detail — {column}";
            open.Content = dock;
            open.Activate();
            return;
        }

        var window = new Window
        {
            Title = $"Cell Detail — {column}",
            Width = 720,
            Height = 540,
            Content = dock,
        };
        window.Closed += (_, _) => _cellDetail = null;
        _cellDetail = window;
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
        if (_session is null) return;
        if (_executing || _current is { Completed: false })
        {
            // Golden: "cannot commit transaction - SQL statements in progress"
            SetInfo("Cannot commit — statement still running/fetching. Cancel or Fetch All first.");
            return;
        }
        // EditMode: Post 하지 않은 변경이 남아 있으면 먼저 보내고 확정한다 (Golden 은 Commit 이 Post 를 겸한다)
        if (IsEditing && HasPendingEdits())
        {
            await SubmitEditsAsync(forceCommit: true);
            return;
        }
        if (!_session.InTransaction) return;
        try
        {
            await _session.CommitAsync();
            SetInfo("Commit complete.");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    public async Task RollbackAsync()
    {
        if (_session is null) return;
        if (_executing || _current is { Completed: false })
        {
            SetInfo("Cannot rollback — statement still running/fetching. Cancel or Fetch All first.");
            return;
        }
        var hadTransaction = _session.InTransaction;
        if (hadTransaction)
        {
            try { await _session.RollbackAsync(); }
            catch (Exception ex) { ShowError(ex.Message); return; }
        }
        // EditMode: 되돌린 데이터를 다시 읽고, Post 전 변경도 함께 버린다
        if (IsEditing && (hadTransaction || HasPendingEdits()))
        {
            await RevertEditsAsync();
            SetInfo($"Rollback complete — {EditTable} 다시 조회했습니다");
            return;
        }
        if (hadTransaction)
            SetInfo("Rollback complete.");
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
}
