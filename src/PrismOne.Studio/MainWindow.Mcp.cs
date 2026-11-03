using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mcp;

namespace PrismOne.Studio;

/// <summary>
/// MCP 서버 — Claude Code 가 실행 중인 Aurum 을 브라우저 자동화처럼 조작한다
/// (스키마 조회 → 에디터에 SQL → 실행 → 결과 읽기).
///
/// 모든 도구는 UI 스레드에서 <b>하나씩</b> 돈다 (<see cref="_mcpGate"/>) — 화면에 보이는 그대로가
/// AI 가 보는 상태다. 앞 호출이 오래 걸리면(승인 대기·긴 쿼리) 뒤 호출은 줄 서지 않고 busy 를 받는다:
/// 클라이언트가 타임아웃 뒤 재전송한 쓰기가 승인 한 번에 두 번 실행되는 일을 막는다.
/// 쓰기 SQL·COMMIT·ROLLBACK 은 매번 사용자 승인을 받고, 실행·승인 내역은 감사 로그에 남는다.
/// </summary>
public partial class MainWindow
{
    private const string ClaudeTabTitle = "Claude";
    private const int DefaultResultRows = 50;
    private const int DefaultTableListLimit = 300;
    private const int MaxTableListLimit = 2000;
    private static readonly TimeSpan McpGateWait = TimeSpan.FromSeconds(10);

    /// <summary>Claude 전용 탭 표식 (TabItem.Tag). 사용자가 쓰던 탭을 덮어쓰지 않으려고 따로 둔다.</summary>
    private static readonly object ClaudeTabTag = new();

    private const string McpInstructions =
        "Aurum is a desktop SQL client (PostgreSQL / Oracle / SQLite / MongoDB) that the user is watching live. " +
        "These tools drive its UI. Start with aurum_status. Use list_tables / describe_table for schema " +
        "(served from Aurum's introspection cache, no DB round trip) and validate_sql before run_query. " +
        "run_query writes the SQL into the visible editor (a dedicated 'Claude' tab unless `tab` is given) and runs it. " +
        "Plain SELECT runs immediately; anything else (DML, DDL, locking reads, side-effect functions) asks the user " +
        "for approval in a dialog. Sessions default to manual commit: changes stay uncommitted until commit " +
        "(also user-approved) — say so when you leave a transaction open. If a call returns busy, wait; never " +
        "resend a write. Result rows are capped; page with get_results. Do not use :bind variables — inline literals.";

    private McpSettings _mcpSettings = McpSettings.Load();
    private McpHttpServer? _mcpServer;
    private string? _mcpError;
    private string? _mcpRunningTool;
    private bool _mcpDialogOpen;
    private readonly SemaphoreSlim _mcpGate = new(1, 1);
    private readonly McpAuditLog _mcpAudit = new();

    // ---------- 수명 ----------

    private void StartMcpIfEnabled()
    {
        if (_mcpSettings.Enabled)
            StartMcp();
    }

    private void StartMcp()
    {
        McpHttpServer? server = null;
        try
        {
            _mcpSettings = PersistToken(_mcpSettings);
            server = new McpHttpServer(_mcpSettings.Port, _mcpSettings.Token!, BuildMcpDispatcher());
            server.Start();
            _mcpServer = server;
            _mcpError = null;
            StatusLabel.Text = $"MCP server listening — {server.Url}";
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException
                                       or UnauthorizedAccessException or CryptographicException)
        {
            _mcpError = $"포트 {_mcpSettings.Port} 를 열 수 없습니다 ({ex.Message})";
            StatusLabel.Text = "MCP: " + _mcpError;
            if (server is not null)
                _ = server.DisposeAsync().AsTask();
        }
    }

    private static McpSettings PersistToken(McpSettings settings)
    {
        var ensured = settings.EnsureToken();
        if (!ReferenceEquals(ensured, settings))
            ensured.Save();
        return ensured;
    }

    private async Task StopMcpAsync()
    {
        if (_mcpServer is not { } server)
            return;
        _mcpServer = null;
        await server.DisposeAsync();
    }

    private async void OnMenuMcp(object? sender, RoutedEventArgs e)
    {
        // 재시작(Stop → Start) 도중 메뉴를 또 눌러 서버가 둘 뜨지 않게
        if (_mcpDialogOpen)
            return;
        _mcpDialogOpen = true;
        try
        {
            _mcpSettings = PersistToken(_mcpSettings);
            var dialog = new McpSettingsDialog(_mcpSettings, _mcpServer?.Url, _mcpError, _mcpAudit.FilePath);
            await dialog.ShowDialog(this);
            if (dialog.Result is not { } next)
                return;

            next.Save();
            _mcpSettings = next;
            await StopMcpAsync();
            _mcpError = null;
            if (next.Enabled)
                StartMcp();
            else
                StatusLabel.Text = "MCP server stopped";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            StatusLabel.Text = $"MCP 설정을 저장하지 못했습니다: {ex.Message}";
        }
        finally
        {
            _mcpDialogOpen = false;
        }
    }

    // ---------- 도구 목록 ----------

    private McpDispatcher BuildMcpDispatcher() => new(
        "aurum",
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        McpInstructions,
        [
            Tool("aurum_status",
                "Connection and every query tab: number, title, connected session, db kind, executing, " +
                "auto/manual commit, open transaction, status line, loaded rows, last error.",
                Schema(),
                McpStatus),
            Tool("list_tables",
                "List tables/views of the connected database from Aurum's schema cache.",
                Schema(
                    ("schema", "string", "only this schema", false),
                    ("filter", "string", "case-insensitive substring of the table name", false),
                    ("limit", "integer", $"max names to return (default {DefaultTableListLimit})", false)),
                McpListTablesAsync),
            Tool("describe_table",
                "Columns of one table: name, type, nullable, primary key, foreign key.",
                Schema(("table", "string", "table name, optionally schema-qualified (schema.table)", true)),
                McpDescribeTableAsync),
            Tool("validate_sql",
                "Check SQL for unknown tables/columns against the schema cache (the red squiggles in the editor). " +
                "Does not execute anything and does not check syntax.",
                Schema(("sql", "string", "SQL text", true)),
                McpValidateSqlAsync),
            Tool("get_editor",
                "Read the SQL text (and current selection) of a tab's editor.",
                Schema(TabProperty()),
                McpGetEditorAsync),
            Tool("set_editor",
                "Put SQL into a tab's editor without running it, so the user can review it. Undoable with Ctrl+Z.",
                Schema(
                    ("sql", "string", "SQL text", true),
                    ("mode", "string", "replace (default) | append | insert (at the caret)", false),
                    TabProperty()),
                McpSetEditorAsync),
            Tool("new_tab",
                "Open a new query tab on the shared session.",
                Schema(
                    ("title", "string", "tab title", false),
                    ("sql", "string", "initial SQL", false)),
                McpNewTabAsync),
            Tool("run_query",
                "Show the SQL in the editor and execute it (script: all statements in order). " +
                "Plain SELECT runs immediately; anything else requires the user's approval. " +
                "By default an approved write stays uncommitted (commit is a separate, approved step). " +
                "Set commit=true ONLY when the user explicitly asked to run AND commit in one go: one approval " +
                "then covers both, and the transaction is rolled back automatically if any statement fails. " +
                "Returns status, affected rows and the first rows of the result grid.",
                Schema(
                    ("sql", "string", "SQL to run", true),
                    ("commit", "boolean", "commit right after a successful approved write (one approval for both)", false),
                    ("maxRows", "integer", $"rows to return (default {DefaultResultRows})", false),
                    TabProperty()),
                McpRunQueryAsync),
            Tool("get_results",
                "Page through the result grid currently shown in a tab.",
                Schema(
                    ("offset", "integer", "first row (0-based)", false),
                    ("limit", "integer", $"rows to return (default {DefaultResultRows})", false),
                    TabProperty()),
                McpGetResultsAsync),
            Tool("commit",
                "Commit the open transaction of a tab. The user must approve.",
                Schema(TabProperty()),
                McpCommitAsync),
            Tool("rollback",
                "Roll back the open transaction of a tab. The user must approve (it may discard the user's own work).",
                Schema(TabProperty()),
                McpRollbackAsync),
        ]);

    /// <summary>
    /// UI 스레드에서 한 번에 하나씩 돌리고, 무엇을 하는지 상태바에 보인다.
    /// 앞 호출이 <see cref="McpGateWait"/> 안에 안 끝나면 줄 세우지 않고 busy 로 돌려보낸다.
    /// </summary>
    private McpTool Tool(
        string name,
        string description,
        JsonObject schema,
        Func<JsonElement, CancellationToken, Task<McpToolResult>> handler) =>
        new(name, description, schema, async (args, ct) =>
        {
            if (!await _mcpGate.WaitAsync(McpGateWait, ct))
            {
                return McpToolResult.Error(
                    $"Aurum is busy with a previous '{_mcpRunningTool}' call (a running query or a pending " +
                    "approval dialog). Wait and check aurum_status — do not resend writes.");
            }
            _mcpRunningTool = name;
            try
            {
                return await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // 사용자가 [중지] 했으면 상태 조회만 허용 (Claude 가 왜 막혔는지 알 수 있게)
                    if (IsClaudePaused)
                    {
                        return name == "aurum_status"
                            ? await handler(args, ct)
                            : McpToolResult.Error(
                                "The user stopped Claude's control of Aurum (pressed Stop). " +
                                "Ask the user to click 'Resume' in Aurum before continuing.");
                    }
                    if (BeginClaudeCall(name, ct) is not { } call)
                        return McpToolResult.Error("The user stopped Claude's control of Aurum.");
                    try
                    {
                        StatusLabel.Text = $"Claude ▸ {name}";
                        var result = await handler(args, call.Token);
                        return call.IsCancellationRequested && !ct.IsCancellationRequested
                            ? McpToolResult.Error("Stopped by the user (Stop button / Esc). Anything already executed may need a rollback — check aurum_status.")
                            : result;
                    }
                    catch (OperationCanceledException) when (call.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        return McpToolResult.Error("Stopped by the user (Stop button / Esc).");
                    }
                    finally
                    {
                        EndClaudeCall(call);
                    }
                });
            }
            finally
            {
                _mcpRunningTool = null;
                _mcpGate.Release();
            }
        });

    private Task<McpToolResult> McpStatus(JsonElement args, CancellationToken ct) => Task.FromResult(Ok(StatusJson()));

    private static (string, string, string, bool) TabProperty() =>
        ("tab", "integer", "tab number from aurum_status (default: the dedicated 'Claude' tab)", false);

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in props)
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired)
                required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    // ---------- 읽기 도구 ----------

    private JsonObject StatusJson()
    {
        var tabs = new JsonArray();
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Content is not QueryTabView view)
                continue;
            tabs.Add(new JsonObject
            {
                ["tab"] = i + 1,
                ["title"] = _tabs[i].Header as string ?? "",
                ["active"] = ReferenceEquals(QueryTabs.SelectedItem, _tabs[i]),
                ["claudeTab"] = ReferenceEquals(_tabs[i].Tag, ClaudeTabTag),
                ["connected"] = view.IsConnected,
                ["session"] = view.SessionDisplayName,
                ["dbKind"] = view.SessionProfile?.Kind.ToString(),
                ["privateSession"] = view.IsPrivateSession,
                ["executing"] = view.IsExecuting,
                ["autoCommit"] = view.AutoCommit,
                ["inTransaction"] = view.InTransaction,
                ["status"] = view.InfoMessage,
                ["rowsLoaded"] = view.LoadedRowCount,
                ["error"] = view.LastError,
            });
        }
        return new JsonObject
        {
            ["connection"] = _profile is null
                ? null
                : new JsonObject { ["name"] = _profile.DisplayName, ["dbKind"] = _profile.Kind.ToString() },
            ["tabs"] = tabs,
            ["maxRowsPerCall"] = _mcpSettings.MaxRows,
            ["stoppedByUser"] = IsClaudePaused,
        };
    }

    private async Task<McpToolResult> McpListTablesAsync(JsonElement args, CancellationToken ct)
    {
        if (CacheFor(args) is not { } cache)
            return NotConnected();
        var schema = Str(args, "schema");
        var filter = Str(args, "filter");
        var limit = Math.Clamp(Int(args, "limit") ?? DefaultTableListLimit, 1, MaxTableListLimit);

        var matched = (await cache.GetTablesAsync(ct))
            .Where(t => string.IsNullOrEmpty(schema) || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrEmpty(filter) || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var names = matched
            .Take(limit)
            .Select(t => (JsonNode?)JsonValue.Create(t.IsView ? $"{t.Schema}.{t.Name} (view)" : $"{t.Schema}.{t.Name}"))
            .ToArray();
        return Ok(new JsonObject
        {
            ["total"] = matched.Count,
            ["returned"] = names.Length,
            ["tables"] = new JsonArray(names),
        });
    }

    private async Task<McpToolResult> McpDescribeTableAsync(JsonElement args, CancellationToken ct)
    {
        if (CacheFor(args) is not { } cache)
            return NotConnected();
        if (Str(args, "table") is not { Length: > 0 } requested)
            return McpToolResult.Error("`table` is required.");

        var dot = requested.LastIndexOf('.');
        var schema = dot > 0 ? Unquote(requested[..dot]) : null;
        var name = Unquote(dot > 0 ? requested[(dot + 1)..] : requested);
        var matches = (await cache.GetTablesAsync(ct))
            .Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && (schema is null || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0)
            return McpToolResult.Error($"Table '{requested}' not found in the schema cache. Try list_tables.");
        if (matches.Count > 1)
            return McpToolResult.Error("Ambiguous — qualify with a schema: " +
                                       string.Join(", ", matches.Select(t => $"{t.Schema}.{t.Name}")));

        var table = matches[0];
        var columns = new JsonArray();
        foreach (var c in await cache.GetColumnsAsync(table, ct))
        {
            columns.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["type"] = c.Type,
                ["nullable"] = c.Nullable,
                ["pk"] = c.Pk,
                ["fk"] = c.Fk,
            });
        }
        return Ok(new JsonObject
        {
            ["table"] = $"{table.Schema}.{table.Name}",
            ["view"] = table.IsView,
            ["columns"] = columns,
        });
    }

    private async Task<McpToolResult> McpValidateSqlAsync(JsonElement args, CancellationToken ct)
    {
        if (CacheFor(args) is not { } cache)
            return NotConnected();
        if (Str(args, "sql") is not { Length: > 0 } sql)
            return McpToolResult.Error("`sql` is required.");

        var snapshot = await cache.GetAsync(ct);
        var issues = SqlValidator.Validate(sql, snapshot);
        if (issues.Count == 0)
            return McpToolResult.Ok(
                $"No unknown tables/columns (checked against {snapshot.Tables.Count} cached tables). Syntax is not checked.");

        var list = new JsonArray();
        foreach (var issue in issues)
        {
            var (line, column) = LineColumn(sql, issue.Start);
            list.Add(new JsonObject
            {
                ["line"] = line,
                ["column"] = column,
                ["text"] = sql.Substring(issue.Start, Math.Min(issue.Length, sql.Length - issue.Start)),
                ["message"] = issue.Message,
            });
        }
        return Ok(new JsonObject { ["issues"] = list });
    }

    private async Task<McpToolResult> McpGetEditorAsync(JsonElement args, CancellationToken ct)
    {
        if (await ResolveTabAsync(args, create: false) is not { } target)
            return NoTab(args);
        return Ok(new JsonObject
        {
            ["tab"] = TabNo(target),
            ["sql"] = target.View.GetSql(),
            ["selection"] = target.View.SelectedSql,
        });
    }

    private async Task<McpToolResult> McpGetResultsAsync(JsonElement args, CancellationToken ct)
    {
        if (await ResolveTabAsync(args, create: false) is not { } target)
            return NoTab(args);
        var tabNo = TabNo(target);
        var (columns, rows) = target.View.Snapshot();
        if (columns.Count == 0)
            return McpToolResult.Ok($"Tab {tabNo} has no result grid.");

        var offset = Math.Max(0, Int(args, "offset") ?? 0);
        var limit = Math.Clamp(Int(args, "limit") ?? DefaultResultRows, 0, _mcpSettings.MaxRows);
        var page = JsonNode.Parse(McpResultText.Page(columns, rows, offset, limit))!.AsObject();
        page["tab"] = tabNo;
        page["sql"] = target.View.LastGridSql;
        Audit("get_results", tabNo, target.View.LastGridSql ?? "", "read", page["returned"]?.GetValue<int>());
        return Ok(page);
    }

    // ---------- 편집 도구 ----------

    private async Task<McpToolResult> McpSetEditorAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "sql") is not { } sql)
            return McpToolResult.Error("`sql` is required.");
        var mode = Str(args, "mode") ?? "replace";
        if (mode is not ("replace" or "append" or "insert"))
            return McpToolResult.Error("`mode` must be replace, append or insert.");
        if (await ResolveTabAsync(args, create: true) is not { } target)
            return NoTab(args);

        QueryTabs.SelectedItem = target.Item;
        switch (mode)
        {
            case "append": target.View.AppendSql(sql); break;
            case "insert": target.View.InsertAtCaret(sql); break;
            default: target.View.ReplaceSqlUndoable(sql); break;
        }
        target.View.FocusEditor();
        return McpToolResult.Ok($"Tab {TabNo(target)} editor updated ({mode}). The user can undo with Ctrl+Z.");
    }

    private async Task<McpToolResult> McpNewTabAsync(JsonElement args, CancellationToken ct)
    {
        await NewTabAsync(null, Str(args, "title"), Str(args, "sql"));
        return Ok(new JsonObject
        {
            ["tab"] = _tabs.Count,
            ["title"] = _tabs[^1].Header as string ?? "",
        });
    }

    // ---------- 실행 도구 ----------

    private async Task<McpToolResult> McpRunQueryAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "sql")?.Trim() is not { Length: > 0 } sql)
            return McpToolResult.Error("`sql` is required.");
        if (await ResolveTabAsync(args, create: true) is not { } target)
            return NoTab(args);
        var view = target.View;
        var tabNo = TabNo(target);

        if (CannotRun(target) is { } notReady)
            return notReady;
        if (BindVariables.Find(sql).Count > 0)
            return McpToolResult.Error("The SQL contains :bind variables — inline the literal values instead.");

        QueryTabs.SelectedItem = target.Item;
        var decision = "auto";
        // 한 번 승인으로 실행+COMMIT — 승인을 받은 쓰기에만 적용한다. 승인 없는 SELECT 에 commit 을
        // 붙여도 무시한다 (사용자가 하던 다른 미확정 작업까지 몰래 확정되면 안 된다).
        var commitAfter = false;
        if (McpSqlPolicy.Classify(sql) == McpSqlKind.Write)
        {
            // 승인 창에 다 보이지 않는 SQL 은 받지 않는다 — 보이지 않는 꼬리가 실행되면 안 된다
            if (sql.Length > McpConfirmDialog.MaxPreviewChars)
                return McpToolResult.Error(
                    $"SQL needing approval must be at most {McpConfirmDialog.MaxPreviewChars} characters so the user can read all of it. Split it.");

            commitAfter = Bool(args, "commit") == true && !view.AutoCommit;
            var approved = await RequestApprovalAsync(
                commitAfter
                    ? $"Claude 가 탭 {tabNo} 에서 다음 SQL 을 실행하고 COMMIT 까지 하려고 합니다."
                    : $"Claude 가 탭 {tabNo} 에서 다음 SQL 실행을 요청했습니다.",
                sql, view.AutoCommit, commitAfter, ct);
            Audit("run_query", tabNo, sql, approved ? (commitAfter ? "approved+commit" : "approved") : "declined", null);
            if (!approved)
                return McpToolResult.Error("The user declined (or did not answer) — the SQL was not run.");
            // 승인 창이 떠 있는 동안 탭이 닫히거나 끊겼을 수 있다
            if (CannotRun(target) is { } changed)
                return changed;
            decision = "approved";
        }

        using (ct.Register(() => Dispatcher.UIThread.Post(view.Cancel)))
            await view.RunSqlFromMcpAsync(sql);

        // Busy 를 먼저 본다 — 실행이 거절되면 오류 창이 지난 실행의 것이라 LastError 를 믿을 수 없다
        if (view.InfoMessage.StartsWith("Busy", StringComparison.Ordinal))
            return McpToolResult.Error(view.InfoMessage);
        if (view.LastError is { } error)
        {
            var failure = new JsonObject { ["tab"] = tabNo, ["error"] = error };
            // 실행+COMMIT 을 한 번에 승인받은 경우, 일부만 반영된 채로 두지 않는다
            if (commitAfter && view.InTransaction)
            {
                await view.RollbackAsync();
                UpdateTxState();
                failure["rolledBack"] = !view.InTransaction;
                Audit("rollback", tabNo, "ROLLBACK (auto, after failed run+commit)", "auto", null);
            }
            Audit("run_query", tabNo, sql, $"{decision}:error", null);
            return McpToolResult.Error(failure.ToJsonString(McpJson.Options));
        }
        if (ct.IsCancellationRequested)
            return McpToolResult.Error("Stopped by the user — nothing was committed.");

        if (commitAfter && view.InTransaction)
        {
            await view.CommitAsync();
            UpdateTxState();
            Audit("commit", tabNo, "COMMIT (run+commit)", view.InTransaction ? "error" : "approved", null);
            if (view.InTransaction)
                return McpToolResult.Error($"The SQL ran but COMMIT failed: {view.InfoMessage}. Changes are still uncommitted.");
        }

        var summary = new JsonObject
        {
            ["tab"] = tabNo,
            ["status"] = view.InfoMessage,
            ["rows"] = view.InfoRows,
            ["elapsed"] = view.InfoTime,
            ["inTransaction"] = view.InTransaction,
            ["committed"] = commitAfter,
        };
        if (view.InTransaction)
            summary["note"] = "Uncommitted changes are pending. Call commit or rollback (both need user approval).";

        var (columns, rows) = view.Snapshot();
        var returned = 0;
        if (columns.Count > 0)
        {
            var limit = Math.Clamp(Int(args, "maxRows") ?? DefaultResultRows, 0, _mcpSettings.MaxRows);
            var page = JsonNode.Parse(McpResultText.Page(columns, rows, 0, limit))!;
            returned = page["returned"]?.GetValue<int>() ?? 0;
            summary["result"] = page;
        }
        Audit("run_query", tabNo, sql, decision, returned);
        UpdateTxState();
        return Ok(summary);
    }

    private Task<McpToolResult> McpCommitAsync(JsonElement args, CancellationToken ct) =>
        EndTransactionAsync(args, commit: true, ct);

    private Task<McpToolResult> McpRollbackAsync(JsonElement args, CancellationToken ct) =>
        EndTransactionAsync(args, commit: false, ct);

    /// <summary>COMMIT·ROLLBACK 공통 — 둘 다 승인을 받는다 (ROLLBACK 도 사용자가 하던 작업을 날릴 수 있다).</summary>
    private async Task<McpToolResult> EndTransactionAsync(JsonElement args, bool commit, CancellationToken ct)
    {
        var verb = commit ? "COMMIT" : "ROLLBACK";
        var tool = commit ? "commit" : "rollback";
        if (await ResolveTabAsync(args, create: false) is not { } target)
            return NoTab(args);
        var tabNo = TabNo(target);
        if (!target.View.InTransaction)
            return McpToolResult.Ok($"Tab {tabNo} has no open transaction — nothing to {tool}.");

        QueryTabs.SelectedItem = target.Item;
        var approved = await RequestApprovalAsync(
            $"Claude 가 탭 {tabNo} 의 {verb} 를 요청했습니다. " +
            (commit ? "변경을 확정할까요?" : "확정하지 않은 변경을 모두 버릴까요?"),
            null, autoCommit: false, willCommit: false, ct);
        Audit(tool, tabNo, verb, approved ? "approved" : "declined", null);
        if (!approved)
            return McpToolResult.Error($"The user declined (or did not answer) — no {verb}.");
        if (!_tabs.Contains(target.Item))
            return McpToolResult.Error($"Tab {tabNo} was closed while waiting for approval.");

        if (commit)
            await target.View.CommitAsync();
        else
            await target.View.RollbackAsync();
        UpdateTxState();
        return target.View.InTransaction
            ? McpToolResult.Error($"{verb} did not complete: {target.View.InfoMessage}")
            : McpToolResult.Ok($"Tab {tabNo}: {verb} done.");
    }

    private async Task<bool> RequestApprovalAsync(
        string message, string? sql, bool autoCommit, bool willCommit, CancellationToken ct)
    {
        Activate();
        return await McpConfirmDialog.ShowAsync(this, message, sql, autoCommit, willCommit, ct);
    }

    // ---------- 보조 ----------

    private sealed record McpTarget(TabItem Item, QueryTabView View);

    private int TabNo(McpTarget target) => _tabs.IndexOf(target.Item) + 1;

    /// <summary>실행할 수 없는 상태면 그 이유. (승인 대기 전·후 두 번 본다)</summary>
    private McpToolResult? CannotRun(McpTarget target)
    {
        if (!_tabs.Contains(target.Item))
            return McpToolResult.Error("That tab was closed.");
        var tabNo = TabNo(target);
        if (!target.View.IsConnected)
            return McpToolResult.Error($"Tab {tabNo} is not connected. Ask the user to log on (Ctrl+L).");
        if (target.View.IsExecuting)
            return McpToolResult.Error($"Tab {tabNo} is still executing. Wait or ask the user to cancel.");
        return null;
    }

    /// <summary>
    /// <c>tab</c> 번호가 있으면 그 탭, 없으면 Claude 전용 탭. <paramref name="create"/> 면
    /// 전용 탭이 없을 때 새로 연다 (공유 세션에 붙는다).
    /// </summary>
    private async Task<McpTarget?> ResolveTabAsync(JsonElement args, bool create)
    {
        if (Int(args, "tab") is { } n)
        {
            return n >= 1 && n <= _tabs.Count && _tabs[n - 1].Content is QueryTabView picked
                ? new McpTarget(_tabs[n - 1], picked)
                : null;
        }
        var existing = _tabs.FirstOrDefault(t => ReferenceEquals(t.Tag, ClaudeTabTag));
        if (existing?.Content is QueryTabView claudeView)
            return new McpTarget(existing, claudeView);
        if (!create)
            return null;

        var view = await NewTabAsync(null, ClaudeTabTitle);
        var item = _tabs.First(t => ReferenceEquals(t.Content, view));
        item.Tag = ClaudeTabTag;
        return new McpTarget(item, view);
    }

    /// <summary>탭을 지정했으면 그 탭 세션의 캐시(전용 접속일 수 있다), 아니면 메인 접속 캐시.</summary>
    private SchemaCache? CacheFor(JsonElement args) =>
        Int(args, "tab") is { } n && n >= 1 && n <= _tabs.Count && _tabs[n - 1].Content is QueryTabView view
            ? view.SchemaCache ?? _schemaCache
            : _schemaCache;

    /// <summary>
    /// 감사 로그 한 줄. 기록에 실패하면 상태바로 알린다 — 도구 결과까지 막지는 않지만 조용히 넘기지도 않는다.
    /// </summary>
    private void Audit(string tool, int tab, string sql, string decision, int? rowsReturned)
    {
        try
        {
            _mcpAudit.Append(new McpAuditEntry(DateTimeOffset.Now, tool, tab, sql, decision, rowsReturned));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusLabel.Text = $"MCP 감사 로그를 쓰지 못했습니다: {ex.Message}";
        }
    }

    private static McpToolResult NotConnected() =>
        McpToolResult.Error("Aurum is not connected to a database. Ask the user to log on (Ctrl+L).");

    private static McpToolResult NoTab(JsonElement args) =>
        McpToolResult.Error(Int(args, "tab") is { } n
            ? $"There is no tab {n}. Call aurum_status for the tab list."
            : "The 'Claude' tab does not exist yet — pass `tab` (see aurum_status) or call run_query/set_editor first.");

    private static McpToolResult Ok(JsonNode node) => McpToolResult.Ok(node.ToJsonString(McpJson.Options));

    private static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static int? Int(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private static string Unquote(string identifier) => identifier.Trim().Trim('"', '`', '[', ']');

    private static (int Line, int Column) LineColumn(string text, int offset)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            line++;
            lineStart = i + 1;
        }
        return (line, offset - lineStart + 1);
    }
}
