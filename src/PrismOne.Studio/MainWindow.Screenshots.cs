using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Studio;

/// <summary>
/// 자가 검증용 스크린샷 하니스 (IAPDM_SHOT_DIR / IAPDM_SHOT_CONN / IAPDM_RENDER_ICON).
///
/// 제품 코드가 아니라 <b>화면 회귀를 사람 눈 대신 잡는 도구</b>다 (README "자가 검증").
/// MainWindow 본체에 섞여 있으면 메뉴·실행 로직을 읽을 때마다 걸리적거려 분리했다.
/// </summary>
public partial class MainWindow
{
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
                    bmp.Save(System.IO.Path.Combine(dir, "live_completion.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
                else
                {
                    SaveShot(this, System.IO.Path.Combine(dir, "live_completion.png"));
                }

                var shotSql = Environment.GetEnvironmentVariable("IAPDM_SHOT_SQL")
                    ?? "select table_schema, table_name from information_schema.tables order by 1, 2;";
                view.SetSql(shotSql);
                await view.ExecuteAtCaretAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_query.png"));

                // 즐겨찾기 실행 경로 점검 — 메뉴에서 고른 것과 같은 경로(SELECT 게이트 + LoadAndRun)
                await RunFavoriteSqlAsync("select current_database(), now();", "shot favorite");
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_favorite.png"));

                // 즐겨찾기가 에디터를 덮어썼으므로 스크롤 캡처용 쿼리를 다시 돌린다
                view.SetSql(shotSql);
                await view.ExecuteAtCaretAsync();
                await Task.Delay(400);

                await view.ScrollToBottomAsync();
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_scrolled.png"));

                view.SetSql("select t.table_name, c.column_name from information_schema.tables t join information_schema.columns c on c.table_name = t.table_name where t.table_schema = 'prismone' order by 1, 2;");
                await view.ExecuteExplainAsync(analyze: true);
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "live_explain.png"));

                // Run and Edit 전체 경로 검증 — DB 에 쓰기가 발생하므로 옵트인(IAPDM_SHOT_RAE=1).
                // 검증용 임시 테이블을 만들어 그 위에서만 수정·추가·삭제한다.
                if (Environment.GetEnvironmentVariable("IAPDM_SHOT_RAE") == "1")
                    await VerifyRunAndEditAsync(dir, view);
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

    /// <summary>
    /// Run and Edit 전체 경로(셀 편집 → Submit → 커밋 → 재조회) 실접속 검증.
    /// 핵심은 DataGrid 셀의 Cells[i] TwoWay 바인딩이 실제로 값을 되쓰는지 —
    /// EditCellForShotAsync 가 진짜 편집 경로(BeginEdit → TextBox → CommitEdit)를 탄다.
    /// 결과는 live_editmode_result.txt (PASS/FAIL 항목별) 로 남긴다.
    /// </summary>
    private async Task VerifyRunAndEditAsync(string dir, QueryTabView view)
    {
        const string table = "__iapdm_rae_verify";
        var log = new System.Text.StringBuilder();
        var pass = true;
        void Check(string name, bool ok)
        {
            pass &= ok;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        }

        var prevAuto = view.AutoCommit;
        view.AutoCommit = true;
        try
        {
            view.SetSql(
                $"drop table if exists {table};\n" +
                $"create table {table}(id int primary key, name text, note text);\n" +
                $"insert into {table} values (1, 'alpha', 'one'), (2, 'beta', 'two'), (3, 'gamma', 'three');");
            await view.RunScriptAsync();
            await Task.Delay(300);

            view.SetSql($"select * from {table} order by id");
            Check("Run and Edit 진입", await view.RunAndEditAsync());
            await Task.Delay(600);

            // 1) 셀 수정 — 실제 DataGrid 편집 경로 (컬럼: [0]=ctid, [1]=id, [2]=name, [3]=note)
            var editResult = await view.EditCellForShotAsync(0, 2, "alpha-edited");
            Check($"DataGrid 셀 편집 — Cells[i] TwoWay 바인딩 ({editResult})", editResult == "ok");
            SaveShot(this, System.IO.Path.Combine(dir, "live_editmode.png"));

            // 2) 행 추가 (id=4)
            view.AddInsertRow();
            view.SetCellForShot(3, 1, "4");
            view.SetCellForShot(3, 2, "delta");
            view.SetCellForShot(3, 3, "four");

            // 3) 행 삭제 (id=3)
            view.SelectRowForShot(2);
            Check("행 삭제 표시", view.MarkSelectedRowsDeleted() == 1);

            await view.SubmitEditsAsync();
            await Task.Delay(600);
            SaveShot(this, System.IO.Path.Combine(dir, "live_editmode_after.png"));

            // 편집 모드 밖의 새 SELECT 로 DB 최종 상태 확인
            view.SetSql($"select id, name, note from {table} order by id");
            await view.ExecuteAtCaretAsync();
            await Task.Delay(400);
            var (_, rows) = view.Snapshot();
            Check("행 수 3 (1 update + 1 insert + 1 delete 반영)", rows.Count == 3);
            Check("UPDATE 반영 — id=1 name=alpha-edited", rows.Count > 0 && rows[0][1] == "alpha-edited");
            Check("DELETE 반영 — id=3 없음", rows.All(r => r[0] != "3"));
            Check("INSERT 반영 — id=4 delta", rows.Any(r => r[0] == "4" && r[1] == "delta"));
        }
        catch (Exception ex)
        {
            pass = false;
            log.AppendLine("EXCEPTION  " + ex);
        }
        finally
        {
            try
            {
                view.SetSql($"drop table if exists {table};");
                await view.RunScriptAsync();
                await Task.Delay(300);
            }
            catch { /* 뒷정리 실패는 결과 파일로만 남긴다 */ }
            view.AutoCommit = prevAuto;
            log.Insert(0, (pass ? "PASS" : "FAIL") + " — Run and Edit live verification\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "live_editmode_result.txt"), log.ToString());
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
            // SQL 검증 밑줄도 오프라인으로 확인한다 — 샘플 카탈로그로 캐시를 채워둔다
            var sampleColumns = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal)
            {
                ["prismone.study"] =
                [
                    new(1, "study_key", "bigint", "no", "P1", ""),
                    new(2, "study_id", "varchar(64)", "no", "", ""),
                    new(3, "patient_key", "bigint", "no", "", "F1"),
                    new(4, "patient_id", "varchar(64)", "no", "", ""),
                    new(5, "study_dttm", "timestamp", "yes", "", ""),
                    new(6, "modality", "varchar(16)", "yes", "", ""),
                ],
                // SQL Builder 조인 화면을 오프라인으로 확인하려면 상대 테이블도 컬럼이 있어야 한다
                ["prismone.patient"] =
                [
                    new(1, "patient_key", "bigint", "no", "P1", ""),
                    new(2, "patient_id", "varchar(64)", "no", "", ""),
                    new(3, "patient_name", "varchar(128)", "yes", "", ""),
                    new(4, "birth_date", "date", "yes", "", ""),
                ],
            };
            var sampleCache = new SchemaCache(
                _ => Task.FromResult(new SchemaSnapshot(_allTables, sampleColumns)));
            await sampleCache.GetAsync();
            view.SchemaCache = sampleCache;

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

            // 왼쪽 Database Explorer — 트리 템플릿이 실제로 그려지는지 확인용
            OnMenuToggleExplorer(this, new RoutedEventArgs());
            await Task.Delay(600);
            SaveShot(this, System.IO.Path.Combine(dir, "shot_explorer.png"));
            OnMenuToggleExplorer(this, new RoutedEventArgs());

            // Golden 의 결과 보기 전환 (Show Text) — 접속 없이도 렌더를 확인한다
            if (ActiveView is { } textView)
            {
                SetResultView(textView, ResultViewMode.Text);
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "shot_text.png"));
                // F12 경로로 한 번 더 돌려 Log 보기까지 확인 (Text → Log)
                OnMenuCycleResultView(this, new RoutedEventArgs());
                await Task.Delay(400);
                SaveShot(this, System.IO.Path.Combine(dir, "shot_log.png"));
                SetResultView(textView, ResultViewMode.Grid);
            }

            // SQL 검증 — 없는 컬럼(study_dtm)·없는 테이블(stduy)에 물결 밑줄
            view.SetSql("""
                select s.study_key, s.study_dtm
                  from prismone.study s;

                select * from prismone.stduy;
                """);
            await Task.Delay(1200);   // 검증 타이머(0.6s) 경과 대기
            SaveShot(this, System.IO.Path.Combine(dir, "shot_validation.png"));

            // Explain Plan 시각화 — self 비중 막대 + 행수 예측 오차 배지
            var samplePlan = PlanParser.Parse("""
                [{
                  "Plan": {
                    "Node Type": "Nested Loop", "Join Type": "Inner",
                    "Startup Cost": 0.4, "Total Cost": 1650.2, "Plan Rows": 120,
                    "Actual Total Time": 48.2, "Actual Rows": 118, "Actual Loops": 1,
                    "Plans": [
                      { "Node Type": "Seq Scan", "Relation Name": "study", "Alias": "s",
                        "Startup Cost": 0, "Total Cost": 1520.0, "Plan Rows": 40,
                        "Actual Total Time": 41.3, "Actual Rows": 4183, "Actual Loops": 1,
                        "Filter": "(study_dttm >= '2026-07-01'::timestamp)",
                        "Rows Removed by Filter": 95817 },
                      { "Node Type": "Index Scan", "Relation Name": "examlist", "Alias": "e",
                        "Index Name": "pk_examlist",
                        "Startup Cost": 0.4, "Total Cost": 3.2, "Plan Rows": 1,
                        "Actual Total Time": 0.001, "Actual Rows": 1, "Actual Loops": 4183,
                        "Index Cond": "(study_key = s.study_key)" }
                    ]
                  },
                  "Planning Time": 0.42,
                  "Execution Time": 48.9
                }]
                """);
            view.BindPlanTree(samplePlan!, analyze: true);
            await Task.Delay(500);
            SaveShot(this, System.IO.Path.Combine(dir, "shot_plan.png"));

            // Schema Diff — 합성 기준/대상 그래프로 렌더 확인 (접속 없이)
            var diffBaseline = new ErdGraph(
                [
                    new ErdTable("prismone", "study", false,
                    [
                        new ErdColumn("study_key", "bigint", true, true, false),
                        new ErdColumn("study_dttm", "timestamp", false, false, false),
                        new ErdColumn("audit_yn", "char(1)", true, false, false),
                    ]),
                    new ErdTable("prismone", "study_note", false,
                        [new ErdColumn("note_key", "bigint", true, true, false)]),
                ],
                [new ErdRelation("fk_note_study", "prismone.study_note", ["study_key"],
                    "prismone.study", ["study_key"], false, false)]);
            var diffTarget = new ErdGraph(
                [
                    new ErdTable("prismone", "study", false,
                    [
                        new ErdColumn("study_key", "bigint", true, true, false),
                        new ErdColumn("study_dttm", "timestamptz", true, false, false),
                    ]),
                    new ErdTable("prismone", "scratch_tmp", false,
                        [new ErdColumn("id", "integer", false, false, false)]),
                ],
                []);
            var diffWin = new SchemaDiffWindow();
            diffWin.Show(this);
            diffWin.BindResult(SchemaDiff.Compare(diffBaseline, diffTarget));
            await Task.Delay(500);
            SaveShot(diffWin, System.IO.Path.Combine(dir, "shot_diff.png"));
            diffWin.Close();

            // CSV Import — 샘플 파일로 매핑·미리보기 렌더 확인 (접속 없이)
            var importWin = new CsvImportDialog(ConnectionProfile.Default, sampleCache);
            importWin.Show(this);
            await Task.Delay(400);   // 테이블 목록 적재 대기
            importWin.LoadText("study_batch.csv",
                "study_key,study_id,study_dttm,modality,exam_note\n" +
                "2001,ST20260804-0001,2026-08-04 09:10:00,CT,follow-up\n" +
                "2002,ST20260804-0002,2026-08-04 09:25:00,MR,\n" +
                "2003,\"ST20260804,0003\",2026-08-04 10:02:00,US,\"quoted, note\"\n");
            importWin.TableCombo.SelectedIndex = 0;
            await Task.Delay(500);
            SaveShot(importWin, System.IO.Path.Combine(dir, "shot_import.png"));
            importWin.Close();

            // 자동완성 팝업 — 샘플 카탈로그로 배지·색이 테마에 맞는지 확인 (접속 없이)
            if (ActiveView is { } completionView)
            {
                var keepSql = completionView.GetSql();
                completionView.CompletionTables = _allTables;   // 접속 경로가 아니라 여기선 직접 채운다
                completionView.SetSql("select * from ");
                completionView.FocusEditor();
                await Task.Delay(200);
                await completionView.ShowCompletionForShotAsync();
                await Task.Delay(700);
                if (completionView.CompletionWindowForShot is { } popup && popup.Bounds.Width > 1)
                {
                    var size = new Avalonia.PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height);
                    using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
                    bmp.Render(popup);
                    bmp.Save(System.IO.Path.Combine(dir, "shot_completion.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
                completionView.CloseCompletionForShot();
                completionView.SetSql(keepSql);
            }

            // 결과 그리드 찾기 창 — 접속 없이 렌더만 확인
            var find = new FindInResultsDialog(() => ActiveView);
            find.Show(this);
            await Task.Delay(400);
            SaveShot(find, System.IO.Path.Combine(dir, "shot_findresults.png"));
            find.Close();

            // 업데이트 알림 — 가짜 버전으로 창 모양만 확인 (네트워크·설치 상태와 무관)
            var updateWin = AppUpdater.PreviewWindow();
            updateWin.Show(this);
            await Task.Delay(400);
            SaveShot(updateWin, System.IO.Path.Combine(dir, "shot_update.png"));
            updateWin.Close();

            // Query History — 가짜 항목으로 렌더 확인 (실제 히스토리 파일을 읽지 않는다)
            var historyWin = new HistoryDialog(
            [
                new HistoryEntry("select * from prismone.study where study_dttm >= '2026-07-01' order by study_dttm desc",
                    new DateTime(2026, 8, 3, 14, 22, 5)),
                new HistoryEntry("update prismone.examlist set status = 'DONE' where exam_key = 1234",
                    new DateTime(2026, 8, 3, 15, 2, 41)),
                new HistoryEntry("select e.exam_key, e.status from prismone.examlist e join prismone.study s on s.study_key = e.study_key",
                    new DateTime(2026, 8, 4, 9, 12, 0)),
            ]);
            historyWin.Show(this);
            await Task.Delay(400);
            SaveShot(historyWin, System.IO.Path.Combine(dir, "shot_history.png"));
            historyWin.Close();

            // Pin Results — 샘플 그리드 스냅샷을 새 창에
            if (ActiveView?.SnapshotResult() is { } pinSnap)
            {
                var pin = new PinnedResultWindow(pinSnap.Sql ?? "study search", pinSnap.Columns, pinSnap.Rows);
                pin.Show(this);
                await Task.Delay(500);
                SaveShot(pin, System.IO.Path.Combine(dir, "shot_pin.png"));
                pin.Close();
            }

            // Mongo Tree View — 합성 문서로 렌더 확인 (접속 없이)
            var treeDocs = new[]
            {
                MongoDB.Bson.BsonDocument.Parse("""
                    { _id: 1001, patient_id: "P000182", name: "kim",
                      address: { city: "Seoul", geo: { lat: 37.5, lon: 127.0 } },
                      studies: [ { study_id: "ST20260801-0007", modality: "CT" },
                                 { study_id: "ST20260731-0140", modality: "MR" } ] }
                    """),
                MongoDB.Bson.BsonDocument.Parse("""
                    { _id: 1002, patient_id: "P004417", name: "lee",
                      address: { city: "Busan" }, tags: ["vip", "follow-up"] }
                    """),
            };
            var treeWin = new MongoTreeWindow("db.patients.find({})",
                treeDocs.Select((d, i) => PrismOne.Db.Core.Mongo.MongoTree.FromDocument(d, i)).ToList());
            treeWin.Show(this);
            await Task.Delay(400);
            // 첫 문서를 펼쳐 중첩 구조가 보이게
            if (treeWin.Content is TreeView tv && tv.Items.Count > 0 && tv.Items[0] is TreeViewItem first)
            {
                first.IsExpanded = true;
                await Task.Delay(300);
                foreach (var child in first.Items.OfType<TreeViewItem>().Where(c => c.Items.Count > 0))
                    child.IsExpanded = true;
            }
            await Task.Delay(400);
            SaveShot(treeWin, System.IO.Path.Combine(dir, "shot_mongotree.png"));
            treeWin.Close();

            var dialog = new ConnectDialog();
            dialog.Show(this);
            await Task.Delay(500);
            SaveShot(dialog, System.IO.Path.Combine(dir, "shot_login.png"));
            dialog.ShowFilterForShot();
            await Task.Delay(300);
            SaveShot(dialog, System.IO.Path.Combine(dir, "shot_login_filter.png"));
            dialog.Close();

            // SQL Builder — 조인·집계까지 보이게 두 테이블을 넣은 상태로 찍는다
            var builder = new SqlBuilderDialog(_allTables, null) { SchemaCache = sampleCache };
            builder.Show(this);
            await Task.Delay(300);
            await builder.AddForShotAsync("prismone.study");
            await builder.AddForShotAsync("prismone.patient", "s.patient_key = p.patient_key");
            await Task.Delay(500);
            SaveShot(builder, System.IO.Path.Combine(dir, "shot_sqlbuilder.png"));
            builder.Close();

            var favorites = new FavoritesDialog(_favorites,
                "select * from prismone.study where study_dttm > now() - interval '7 days';");
            favorites.Show(this);
            await Task.Delay(500);
            SaveShot(favorites, System.IO.Path.Combine(dir, "shot_favorites.png"));
            favorites.Close();

            var erd = new ErdWindow(SampleErdGraph());
            erd.Show(this);
            await Task.Delay(700);
            SaveShot(erd, System.IO.Path.Combine(dir, "shot_erd.png"));
            erd.Close();

            // Edit Document (Mongo) — 접속 없이 합성 문서로 렌더만 확인
            var mongoDoc = new MongoDocumentDialog(MongoDB.Bson.BsonDocument.Parse(
                "{ _id: 1, name: 'sample', age: 30, address: { city: 'Seoul' } }"));
            mongoDoc.Show(this);
            await Task.Delay(500);
            SaveShot(mongoDoc, System.IO.Path.Combine(dir, "shot_mongo_edit.png"));
            mongoDoc.Close();

            // Import JSON (Mongo) — 접속 없이 합성 JSON 으로 미리보기 렌더만 확인
            var mongoImport = new MongoImportDialog(
                ConnectionProfile.Default with { Kind = DbKind.MongoDb, Database = "demo" });
            mongoImport.Show(this);
            await Task.Delay(300);
            mongoImport.LoadText("people.json",
                "[{ \"_id\": 1, \"name\": \"kim\" }, { \"_id\": 2, \"name\": \"lee\" }]");
            mongoImport.CollectionBox.Text = "people";
            await Task.Delay(400);
            SaveShot(mongoImport, System.IO.Path.Combine(dir, "shot_mongo_import.png"));
            mongoImport.Close();
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>스크린샷용 합성 스키마 — 접속 없이 ERD 렌더를 눈으로 확인하기 위한 가짜 데이터.</summary>
    private static ErdGraph SampleErdGraph()
    {
        static ErdColumn Pk(string name) => new(name, "bigint", true, IsPk: true, IsFk: false);
        static ErdColumn Fk(string name) => new(name, "bigint", true, IsPk: false, IsFk: true);
        static ErdColumn Col(string name, string type, bool notNull = true) =>
            new(name, type, notNull, IsPk: false, IsFk: false);
        static ErdRelation Rel(string child, string parent, string column, bool optional = false) =>
            new($"fk_{child}_{parent}", $"public.{child}", [column], $"public.{parent}", [column],
                ChildUnique: false, ChildOptional: optional);

        var tables = new List<ErdTable>
        {
            // Dicom Image 도메인
            new("public", "patient", false, [Pk("patient_key"), Col("patient_id", "varchar(64)")]),
            new("public", "study", false,
                [Pk("study_key"), Fk("patient_key"), Col("study_dttm", "timestamp", notNull: false)]),
            new("public", "series", false, [Pk("series_key"), Fk("study_key"), Col("modality", "varchar(16)")]),
            new("public", "image", false, [Pk("image_key"), Fk("series_key"), Col("sop_uid", "varchar(128)")]),
            new("public", "study_note", false, [Pk("note_key"), Fk("study_key"), Col("body", "text", false)]),
            // Interface 도메인
            new("public", "interface_msg", false,
                [Pk("msg_key"), Col("msg_type", "varchar(16)"), Col("payload", "text", false)]),
            new("public", "interface_log", false, [Pk("log_key"), Fk("msg_key"), Col("result", "varchar(16)")]),
            new("public", "interface_queue", false, [Pk("queue_key"), Fk("msg_key"), Col("retry_cnt", "int")]),
            // Routing 도메인
            new("public", "router", false, [Pk("router_key"), Col("router_name", "varchar(64)")]),
            new("public", "routing_rule", false,
                [Pk("rule_key"), Fk("router_key"), Col("priority", "int"), Col("expr", "text", false)]),
            // Archive 도메인
            new("public", "archive_job", false, [Pk("job_key"), Col("state", "varchar(16)")]),
            new("public", "archive_target", false, [Pk("target_key"), Fk("job_key"), Col("path", "text")]),
            // User Management 도메인
            new("public", "app_user", false, [Pk("user_key"), Col("login_id", "varchar(64)")]),
            new("public", "user_role", false, [Pk("user_key"), Fk("role_key")]),
            new("public", "role_perm", false, [Pk("role_key"), Pk("perm_code"), Col("granted", "boolean")]),
            // 그 밖
            new("public", "folder", false, [Pk("folder_key"), Fk("parent_key"), Col("name", "text")]),
            new("public", "v_study_summary", true, [Col("study_key", "bigint"), Col("series_cnt", "bigint")]),
        };
        var relations = new List<ErdRelation>
        {
            Rel("study", "patient", "patient_key"),
            Rel("series", "study", "study_key"),
            Rel("image", "series", "series_key"),
            Rel("study_note", "study", "study_key", optional: true),
            Rel("interface_log", "interface_msg", "msg_key"),
            Rel("interface_queue", "interface_msg", "msg_key"),
            Rel("routing_rule", "router", "router_key"),
            Rel("archive_target", "archive_job", "job_key"),
            Rel("user_role", "app_user", "user_key"),
            Rel("user_role", "role_perm", "role_key"),
            Rel("folder", "folder", "parent_key", optional: true),
        };
        return new ErdGraph(tables, relations);
    }

    /// <summary>앱 아이콘: Aurum(Au, 금) — 주기율표 타일. 다크 배경 + 금 그라데이션 "Au" + 원자번호 79.</summary>
    private static void RenderAppIcon(string path)
    {
        var background = Avalonia.Media.Color.Parse("#17130C");
        var canvas = new Canvas { Width = 512, Height = 512 };

        // 금 그라데이션 (밝은 금 → 진한 금, 대각선)
        var gold = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#F7DE8B"), 0.0),
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#E4B54A"), 0.55),
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#B9821F"), 1.0),
            },
        };

        // 타일: 다크 배경 + 가는 금 테두리 (주기율표 원소 칸)
        canvas.Children.Add(new Border
        {
            Width = 512, Height = 512,
            CornerRadius = new Avalonia.CornerRadius(112),
            Background = new Avalonia.Media.SolidColorBrush(background),
        });
        canvas.Children.Add(new Border
        {
            Width = 512 - 2 * 26, Height = 512 - 2 * 26,
            [Canvas.LeftProperty] = 26.0,
            [Canvas.TopProperty] = 26.0,
            CornerRadius = new Avalonia.CornerRadius(88),
            BorderBrush = gold,
            BorderThickness = new Avalonia.Thickness(7),
        });

        var inter = new Avalonia.Media.FontFamily("fonts:Inter#Inter");

        // 원자번호 79 — 타일 좌상단
        canvas.Children.Add(new TextBlock
        {
            Text = "79",
            FontFamily = inter,
            FontSize = 76,
            FontWeight = Avalonia.Media.FontWeight.Medium,
            Foreground = gold,
            [Canvas.LeftProperty] = 78.0,
            [Canvas.TopProperty] = 62.0,
        });

        // 원소기호 Au — 중앙보다 살짝 아래 (주기율표 배치)
        canvas.Children.Add(new TextBlock
        {
            Text = "Au",
            FontFamily = inter,
            FontSize = 252,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = gold,
            Width = 512,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            [Canvas.LeftProperty] = 0.0,
            [Canvas.TopProperty] = 148.0,
        });

        canvas.Measure(new Avalonia.Size(512, 512));
        canvas.Arrange(new Avalonia.Rect(0, 0, 512, 512));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                   new Avalonia.PixelSize(512, 512), new Avalonia.Vector(96, 96)))
        {
            bitmap.Render(canvas);
            bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        // .icns 최상위(512@2x)용 1024px — 같은 512 좌표계를 2배 DPI 로 렌더
        using (var bitmap2x = new Avalonia.Media.Imaging.RenderTargetBitmap(
                   new Avalonia.PixelSize(1024, 1024), new Avalonia.Vector(192, 192)))
        {
            bitmap2x.Render(canvas);
            bitmap2x.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "icon_1024.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static void SaveShot(Window window, string path)
    {
        var size = new Avalonia.PixelSize(
            Math.Max(1, (int)window.Bounds.Width),
            Math.Max(1, (int)window.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
