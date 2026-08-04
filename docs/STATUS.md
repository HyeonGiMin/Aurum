# 진행 상황 · 다음 작업 (인수인계용)

마지막 갱신: 2026-08-04 · 브랜치 `feat/erd-and-result-views`

> **repo 분리 (2026-08-03)**: 이 repo(aurum)는 GUI(Aurum)+Core 전용이다.
> DB 초기 설치·패치 CLI(`iapdb`)와 mini-psql 실행기는 **iap-database repo 의
> `tools/`** 로 이관 — 배포 키트(iapdb + sql/ + patches/)는 그쪽에서 관리한다.

## 목표 대비 현황

| # | 목표 | 상태 |
|---|---|---|
| 1 | **Golden 대체 쿼리 툴** (PostgreSQL) | **완료** — 기능·실접속 검증 모두 끝 (아래 §1) |
| 2 | DB 초기 설치 CLI (`iapdb install`) | **완료** — iap-database repo 로 이관 |
| 3 | 설치된 DB 패치 CLI (`iapdb patch`) | 미착수 — iap-database repo 담당 |

## 0. 이름 (2026-08-03 확정)

- **GUI = Aurum** (금 Au — "Golden 의 후계자"라는 서사. 어셈블리명·창 타이틀·.app/.exe·
  아이콘까지 반영. 아이콘은 주기율표 타일: 다크 배경 + 금 그라데이션 Au + 원자번호 79,
  `IAPDM_RENDER_ICON` 으로 재생성)
- **CLI = iapdb**, 배포 키트(iapdb + sql/ + patches/) = **PrismOne DB Kit**
- 네임스페이스·프로젝트 파일명은 PrismOne.Studio 유지 (외부 노출 없음),
  사용자 데이터 디렉터리도 `~/.prismone-studio/` 유지 (기존 설정 호환)

## 1. Golden 파리티 현황

구현 완료 (근거: `GOLDEN_BEHAVIOR.md` 의 바이너리 추출 명세):

- 시작 시 메인 창(Query1 탭) 위로 **로그온 창 자동 표시**(Golden 동작) · Ctrl+L 재로그온
  (`host[:port]/db`, Login List). 취소하면 미접속 상태로 남는다.
  **Login List 에 Edit(Name/Category/Comment 편집)·Filter ▾(Username/Database/Category
  필터)** — Golden 의 로그인 항목 Category 대응
- F9 문장 / F5·Shift+Enter 스크립트(커서부터 끝까지) / Explain / Explain Analyze / Cancel
- **점진 fetch**(기본 100행, 스크롤 시 이어서, Ctrl+End 전체), Fetch 상한 옵션
- **공유 세션 모델** + New Private Tab(Ctrl+Shift+T)
- Commit / Rollback, 상태바 `[TX]`, **툴바 `Tx: Manual ▾` 드롭다운 하나에
  Transaction Mode(Auto·Manual) + Tx Isolation(Database Default·Read Uncommitted·
  Read Committed·Repeatable Read·Serializable)** — 구성·표기는 DataGrip 2024.2 의
  `DatabaseBundle`(`action.tx.text`, `transaction.mode.*`) 기준.
  드롭다운 팝업은 네이티브 팝업이라 스크린샷 하니스에 잡히지 않는다(툴바 버튼만 캡처됨)
- 자동완성(Ctrl+Space·⌥Space·`.`·FROM 뒤 자동, 별칭→컬럼, 종류 배지·현재 스키마 우선)
- 히스토리 ◀▶(Ctrl+↑↓, 디스크 보존), Find(Ctrl+F), 바인드 변수 `:var`
- Object Browser(F8) + describe, **Ctrl+D describe**, 이름 붙여넣기
- 그리드: 행번호, Transpose(Ctrl+Shift+X), Size Columns to Fit, Filter Like Cell,
  Cell Detail(더블클릭, jsonb pretty-print)
- 내보내기: CSV(COPY 전체 행) / TSV / INSERT 문 / **xlsx**(외부 라이브러리 없이
  OOXML 직접 생성 — 헤더 굵게, 숫자 셀, 10만 행 ≈ 0.2초, Excel 행 상한 시 잘라내고 안내)
- 워크스페이스 저장·복원(`.iapws`), 옵션 다이얼로그, Session Monitor
- **Favorites**(Ctrl+Shift+F 추가 · 메뉴에서 바로 실행 · 관리 창 필터/수정/삭제 ·
  SELECT 이외 차단 옵션) — `~/.prismone-studio/favorites.json`
- PG 강화: Messages(RAISE NOTICE), EXPLAIN 트리, COPY export, pg_stat_activity
- 배포: macOS `.app`(`packaging/macos/make-app.sh`) · **Windows 단일 exe**
  (`packaging/windows/make-app.ps1`, 아이콘은 `make-icon.ps1` → `Assets/icon.ico`)

- **Run and Edit**(F11) — 단일 테이블 SELECT 를 ctid 붙여 재실행 → 셀 수정·행 추가·삭제 →
  Submit(Ctrl+Shift+S)에서 한 트랜잭션으로 반영, 영향 행 ≠ 1 이면 전체 롤백.
  **실접속 검증 완료** (2026-08-03, 전 항목 PASS — 아래 개발 메모 참조).
  검증 과정에서 셀 편집이 아예 시작되지 않는 버그를 찾아 고쳤다:
  DataGrid 는 바인딩 경로의 속성을 리플렉션으로 검사해 편집 가능 여부를 판정하는데,
  CLR 배열엔 인덱서 PropertyInfo 가 없어 `Cells[i]` 경로가 읽기 전용으로 판정됐다
  (BeginEdit 거부 → 더블클릭·F2 무반응). RowItem 에 진짜 인덱서 `this[int]` 를 두고
  편집 모드 바인딩을 `[i]` 경로로 바꿔 해결

- **Print / Print Preview** — SQL(Ctrl+P)·그리드 모두. Avalonia 에 인쇄 API 가 없어
  `%TEMP%/iap-dbm-print/*.html` 로 뽑아 OS 기본 브라우저에 넘긴다(용지·프린터는 브라우저 대화상자)

- **SQL Builder**(Tools) — 테이블·컬럼 선택 + WHERE(연산자 화이트리스트)·Order by·Limit,
  실시간 미리보기 → 에디터에 삽입(실행은 사용자가)

- **EditMode 붙여넣기 다중 insert** — Script > Paste Rows. 클립보드의 탭 구분 표(엑셀·우리
  TSV 내보내기)를 새 행으로 넣는다. 첫 줄이 컬럼명과 같으면 헤더로 보고 건너뛴다

**남은 Golden 기능**: 없음 — Golden 파리티 완료.

- **SQL 검증 (2026-08-04, DATAGRIP_GAP §2)** — 타자 후 0.6초 쉬면 introspection
  캐시와 대조해 없는 테이블/컬럼에 빨간 물결 밑줄 + 호버 툴팁.
  `Core/SqlValidator`(순수 로직, 테스트 22개) + `Studio/SqlErrorRenderer`.
  원칙은 "확신할 때만 표시" — 해석 안 되는 것(CTE·서브쿼리 별칭·모르는 스키마·
  따옴표 식별자·함수)은 침묵한다. 오프라인 스크린샷에 `shot_validation.png` 추가.
- **Explain Plan 시각화 (2026-08-04, DATAGRIP_GAP §4)** — 플랜 트리 노드마다
  self 비중 막대 + %(50%↑ 빨강·20%↑ 주황·초록), 행수 예측 10배↑ 오차엔
  `rows ×N` 배지. 누적치가 아니라 자식 몫을 뺀 self 로 강조한다(이전엔 루트가
  항상 빨갰다). `PlanParser` 에 수치 필드·self 계산 추가 (테스트 6개),
  오프라인 스크린샷 `shot_plan.png`.
- **Schema Diff (2026-08-04, DATAGRIP_GAP §3a)** — Tools > Schema Diff, 읽기 전용.
  표준 사이트에서 스냅샷(JSON)을 떠 두고 각 사이트에서 현재 접속과 비교하는 흐름.
  `Core/SchemaDiff` + `SchemaSnapshotFile`(테스트 10개) + `SchemaDiffWindow`.
  FK 는 제약 이름이 아니라 연결로 비교, 대소문자 무시(Oracle↔PG). DDL 은 만들지
  않는다 — iapdb 원칙 유지. 오프라인 스크린샷 `shot_diff.png`.
- **CSV/TSV Import (2026-08-04, DATAGRIP_GAP §5)** — Tools > Import CSV/TSV.
  RFC 4180 파서(구분자 자동 감지) + 헤더 이름 매핑(무시 헤더·빠진 NOT NULL 미리
  경고) + **전량 성공 아니면 전량 롤백**(실패 시 행 번호·DB 오류). 값은 문자열로
  보내 서버가 캐스팅. 전용 접속에서 실행. `IDbProvider.ParameterPlaceholder`
  추가 — Microsoft.Data.Sqlite 가 이름 없는 파라미터를 거부해 SQLite 는 `@pN`.
  테스트 19개, 오프라인 스크린샷 `shot_import.png`.
- **성능·안정성 (2026-08-04, 사용자 방향: "성능과 안정성이 최우선")**
  - Import 대량 insert 를 **준비된 문장 재사용**(`QuerySession.ExecuteBatchAsync`,
    Prepare + 파라미터 재사용)으로 — 행마다 명령 생성 제거. 5천 행 테스트 포함
    전체 스위트가 0.2초 안에 돈다. 실패 행 번호는 `BatchRowException` 으로 유지
  - SQL 검증에 **200K 자 상한** — 대형 덤프에서 UI 스레드 지연 방지 (밑줄보다 반응성)
- **Query History 조회 창 (2026-08-04, 사용자 요청)** — Tools > Query History.
  실행 시각 포함 최근 순, 부분일치 필터, 더블클릭/Insert 로 에디터 삽입.
  `HistoryStore` 가 시각을 메모리에도 보존하도록 확장 (jsonl 형식은 그대로).
  쿼리 앱 내 저장 = 기존 Favorites, 파일 열기/저장 = 기존 Ctrl+O/S 로 이미 충족.
- **Pin Results (2026-08-04, DATAGRIP_GAP §6)** — Results > Pin Results to New
  Window. 결과 영역 다중 탭 대신 **스냅샷을 별도 창에 고정** — 그리드 핵심 경로를
  건드리지 않는 저위험 설계. 오프라인 스크린샷 `shot_history.png` · `shot_pin.png`.
- **다크 모드 (2026-08-04, UI_POLISH P1-1)** — View > Dark Mode 토글 +
  Options 의 Light/Dark/System. GoldenTheme 을 ThemeDictionaries 로 재구성
  (브러시 28종 DynamicResource), 에디터 구문 배색·플랜 막대·diff 색까지 테마
  대응. 기본은 Light(Golden 정체성). ERD·인쇄물은 라이트 고정.
  스크린샷 하니스 `IAPDM_SHOT_THEME=dark` — 라이트/다크 12종씩 회귀 확인.
- **fetch 회귀 테스트 (2026-08-04)** — "전체 fetch 기본 + 5만 행 상한" 경로를
  SQLite 로 못박음 (`ActiveQueryFetchTests` 6개): lookahead 무손실·배치 경계
  Completed 판정·완료 후 빈 배치(무한 루프 방지 전제)·상한 도달 후 Abort→재실행·
  취소 후 세션 복구.

## 1.6 ERD 뷰어 (2026-08-04, MVP)

Golden 에는 없던 기능. SQL Developer 의 relational model 대응으로 **Tools > Diagram (ERD)**
창을 추가했다. 읽기 전용이며 DDL 을 만들지 않는다.

구조 — PG 종속을 더 굳히지 않으려고 카탈로그만 먼저 provider 로 분리했다(§1.5 방향):

| 파일 | 역할 |
|---|---|
| `Core/ErdModel.cs` | DB 중립 모델(`ErdTable`/`ErdRelation`/`ErdGraph`) + `Focus(N홉)`·`Filter` |
| `Core/ErdCatalog.cs` | `IErdCatalog` 경계 + `PgErdCatalog`(pg_class/pg_constraint 읽기 전용) |
| `Core/ErdLayout.cs` | 순수 레이아웃 — 연결요소 → 참조깊이 레이어 → 바리센터 정렬 → 패킹 → 직교 라우팅 |
| `Studio/ErdCanvas.cs` | `DrawingContext` 직접 렌더 (줌은 좌표 배율 — 글자가 뭉개지지 않게) |
| `Studio/ErdWindow.axaml(.cs)` | 창 — Schema/Filter/Focus·Depth/Columns, 줌·팬·Fit, Save PNG |

- 카디널리티는 조회가 아니라 **추론**이다: 자식 FK 컬럼 집합이 자식 쪽 PK/UNIQUE 로 덮이면
  1:1, 아니면 1:N. 자식 컬럼에 nullable 이 있으면 0..N(빈 원)
- 전체 스키마는 한 화면에 못 읽으므로 **Focus(선택 + N홉)가 기본 시야**. 테이블 수가 많으면
  상태바가 좁히라고 알린다
- 레이아웃이 UI 와 분리돼 있어 단위 테스트가 붙는다 (`ErdLayoutTests` 18개 — 겹침 없음·
  부모가 위·결정성·순환/자기참조 종료·Focus/Filter 경계)
- 접속 없이 렌더를 확인할 수 있게 오프라인 스크린샷 하니스에 `shot_erd.png` 를 추가했다
  (`MainWindow.SampleErdGraph()` 의 합성 스키마)

**남은 것 (미착수)**: 배치 수동 드래그·저장, 테이블 더블클릭 → describe/SELECT 연동,
SVG 내보내기·인쇄, 선택 영역 DDL 생성.
**PG 실접속 전제 확인 완료 (2026-08-04)** — 개발 DB(`pg_constraint contype='f'`)에
FK 제약 **prismone 185건**(+pgmq 2건) 존재. ERD 관계선이 그려지는 조건 충족.

## 1.7 멀티 DB 현황 (2026-08-04)

계획은 `MULTI_DB_PLAN.md`, 판단 근거는 `TOOL_COMPARISON.md` / `DATAGRIP_GAP.md`.

| 기능 | PostgreSQL | Oracle | SQLite |
|---|---|---|---|
| 접속 (로그온 창 Type 선택) | ✅ | ✅ | ✅ |
| 쿼리 실행·트랜잭션 | ✅ | ⚠️ 미검증 | ✅ (테스트) |
| Object Browser·자동완성 | ✅ | ✅ | ✅ |
| ERD (Tools > Diagram) | ✅ | ✅ 실접속 확인 | ✅ (테스트) |
| 세션 격리 수준 | 4단계 | RC/Serializable | ❌ 없음 |
| 그리드 편집 행 특정 | ctid | ROWID | rowid |
| COPY 대량 내보내기 | ✅ | ❌ | ❌ |
| 스키마 버전 pill | ✅ | ❌ (PG 전용) | ❌ |

**실측 메모**

- Oracle 카탈로그: 552테이블 적재 **2.2초**. 처음엔 FK 관계까지 읽어 74초가 걸려
  `IErdCatalog.LoadTablesAsync`(관계 제외)를 따로 뒀다 — 자동완성엔 FK 가 필요 없다
- Oracle ERD: 517테이블·293관계. 단독 테이블을 한 영역으로 묶어 주제영역
  340개 → 8개, 높이 11520 → 8838. 그래도 전체 보기는 못 읽으니 Focus 가 기본

**남은 것**: Oracle 쿼리 실행 실검증 — **이 개발 머신에는 Oracle 접속이 저장돼 있지
않다** (2026-08-04 확인, 저장된 접속은 PG 1건뿐). Oracle 서버가 보이는 환경에서
로그온 한 번 저장한 뒤 검증할 것. 그 외: AS SYSDBA·Read Only(Oracle 은 접속 수준
읽기전용이 없다), MongoDB(3단계), Oracle 카탈로그 자동 테스트(서버 필요)

## 1.5 후순위 방향 — Studio3T 기능 흡수 + DataGrip 급 동작

Golden 파리티가 끝나면 다음 단계는 **DataGrip 처럼 쓰이는 DB 툴**로 넓히는 것.
Studio3T(몽고 툴)에서 가져올 만한 것: 비주얼 쿼리 빌더, 결과 비교(diff), 스키마 탐색기,
데이터 마이그레이션/내보내기 파이프라인, 쿼리 성능 뷰. **아직 후순위** — 착수 전에
어떤 기능을 어느 순서로 가져올지 별도 계획을 세운다.

**MongoDB 지원** (2026-08-03 사용자 방향 제시): DataGrip 처럼 **MongoDB 를 접속 대상
DB 로 직접 지원**해야 한다. 착수 시점에 검토할 것:

- 현재 Core 는 Npgsql 전제(QuerySession·StatementSplitter·EXPLAIN 트리·ctid 편집이
  전부 PG 종속). 멀티 DB 로 가려면 DataGrip 의 dialect/driver 층처럼
  **세션·문장·결과·카탈로그를 provider 인터페이스로 추상화**한 뒤 PG/Mongo 구현을
  나눠야 한다 — PG 코드가 더 굳기 전에 추상화 경계만 미리 잡아두는 게 싸게 먹힌다
- Mongo 는 SQL 이 아니라 문서·파이프라인 모델이라 에디터(쿼리 언어)·그리드(중첩 문서
  표시)·자동완성(컬렉션/필드)·편집(_id 기준) 모두 별도 UX 가 필요 — Studio3T 가
  참고 대상. 드라이버는 공식 MongoDB.Driver(.NET)
- **계획 문서 작성됨 (2026-08-04): `MULTI_DB_PLAN.md`** — Oracle·SQLite 까지 범위를
  넓혔고, 결합도 측정(Core 8개 파일이 Npgsql 직접 참조)·드라이버 선정·기능별 대응
  가능성 표·단계(0 경계 → 1 SQLite → 2 Oracle → 3 Mongo)를 담았다.
  SQLite 를 Oracle 보다 먼저 두는 이유는 **파일 DB 라 단위 테스트로 검증 가능**해서다.
  구현은 아직 미착수

## 2·3. CLI — iap-database repo 로 이관

`iapdb`(install 완료·patch 미착수)와 mini-psql 실행기(`PsqlScript`)는 repo 분리와 함께
**iap-database 의 `tools/`** 로 옮겼다. 진행 상황·검증 기록은 그쪽 `tools/README.md` 참조.

**설계 원칙 (2026-08-03 확정)** — 초기화·패치와 관리 도구의 역할 분리:

- **초기 설치·패치 적용 = `iapdb` CLI** (배포 키트: `iapdb + sql/ + patches/` 를 한 덩어리로).
  버전 종속성은 배포 키트에 내재하는 게 정상 — 키트 버전 = 스키마 목표 버전.
  자동화·감사·재현성(사일런트 설치, ssh 원격 실행) 요건도 CLI 가 맞다.
  TUI 가 필요해지면 CLI 위에 대화형 마법사로 얹는다 (코어 공유)
- **Studio(GUI) = 조회·관리 전용, 스키마 버전에 비종속**. 패치 Apply 버튼 없음.
  빈 비밀번호 접속 차단(운영 DB 전제)도 유지 — trust 서버는 "아직 초기화 안 된 서버"
- Studio 상태바에 **스키마 버전 pill** 구현됨 — 접속 시 `PRISMONE.schema_version` 을
  읽어 마지막 적용 패치(예: `Schema: 20260718_01`, 기록 없으면 `Schema: baseline`)를
  표시, 툴팁에 적용 시각·건수. 테이블이 없으면(비 PRISMONE DB) 자동 숨김.
  개발 서버(<dev-host>)에는 schema_version 이 없어 안 보이는 게 정상

**이 repo 의 남은 것**:

- ~~Golden 핫키 파리티~~ ✅ 구현됨 (2026-08-03) — Golden 매뉴얼 §4.3 공식 키맵 기준
  (Ctrl+E=Run and Edit · Ctrl+F5/F6=Commit/Rollback · Ctrl+H=Replace ·
  Ctrl+-=주석 토글 · Ctrl+R=에디터↔결과 · Ctrl+Tab=탭 이동 등,
  GOLDEN_BEHAVIOR.md §4.1 대조표). 핫키 사용자 재배치 옵션은 후순위
- §1.5 방향: Studio3T 기능 흡수 + DataGrip 급, MongoDB provider 추상화

## 개발 메모 (다른 환경에서 이어받을 때)

- **빌드**: `cd tools && dotnet build PrismOne.Tools.sln` (.NET 10 SDK 필요)
- **테스트**: `dotnet test tests/PrismOne.Db.Core.Tests` (현재 281개)
- **실접속 검증 현황** (2026-08-03, 사내 개발 DB 접속 가능한 환경에서 확인):
  1. ✅ **Tx Isolation** — `ApplyIsolationAsync` 후 `show transaction_isolation` 으로 확인.
     Serializable / Repeatable Read / Read Committed 모두 세션에 반영되고,
     Database Default 는 서버 기본값(read committed)으로 되돌아온다
  2. ✅ **즐겨찾기 실행 경로** — `live_favorite.png` 로 확인 (메뉴에서 실행 → 결과 표시)
  3. ✅ 로그온·describe·자동완성·쿼리·스크롤·EXPLAIN — 스크린샷 모드 7종 전부 정상
  4. ✅ **Run and Edit 전체 경로(셀 편집 → Submit → 커밋 → 재조회) 검증 완료**.
     스크린샷 모드에 `IAPDM_SHOT_RAE=1` 을 추가하면 검증용 임시 테이블
     `__iapdm_rae_verify` 를 만들어 실제 DataGrid 편집 경로(BeginEdit → TextBox →
     CommitEdit)로 UPDATE 1·INSERT 1·DELETE 1 을 수행하고 새 SELECT 로 DB 반영을
     확인한 뒤 테이블을 drop 한다. 결과는 `live_editmode_result.txt` (항목별
     PASS/FAIL) · `live_editmode.png` · `live_editmode_after.png`.
     의심하던 `Cells[i]` TwoWay 바인딩은 실제로 **안 써지고 있었고**(§1 의
     Run and Edit 항목 참조) 인덱서 우회로 고친 뒤 전 항목 PASS
- **실행**: 개발 중엔 `dotnet run --project src/PrismOne.Studio`,
  배포 확인은 macOS `sh packaging/macos/make-app.sh` → `dist/Aurum.app`,
  Windows `powershell -ExecutionPolicy Bypass -File packaging/windows/make-app.ps1`
  → `dist/Aurum/Aurum.exe` (self-contained 단일 exe, 약 48MB)
- **Windows 주의**: PowerShell 5.1 은 BOM 없는 UTF-8 .ps1 을 CP949 로 읽어 한글 주석이
  깨지면서 파싱이 어긋난다. `packaging/windows/*.ps1` 은 **UTF-8 BOM 으로 저장**할 것
- **자가 검증 (중요)**: 화면 회귀는 스크린샷 모드로 확인한다.
  ```bash
  IAPDM_SHOT_DIR=/tmp/shots \
  IAPDM_SHOT_CONN="<dev-host>/prismone|<user>|<password>" \
  dotnet run --project src/PrismOne.Studio --no-build
  ```
  → `live_after_login.png` / `live_describe.png` / `live_completion.png` /
  `live_query.png` / `live_favorite.png` / `live_scrolled.png` / `live_explain.png`
  생성 후 종료. `IAPDM_SHOT_RAE=1` 을 함께 주면 Run and Edit 실접속 검증까지 수행한다
  (임시 테이블을 만들어 편집·Submit 후 drop — 위 실접속 검증 4번 참조). 접속 없이 샘플 데이터만 볼 땐 `IAPDM_SHOT_CONN` 을 빼면
  `shot_main.png` / `shot_login.png` / `shot_favorites.png` 가 나온다.
  아이콘 재생성은 `IAPDM_RENDER_ICON=<경로>`.
- **개발용 DB**: `<dev-host>/prismone` — 접속 정보는 사내 위키 참조. 공유 서버이므로
  읽기 위주로 쓰고, 스키마 변경은 하지 말 것.
- 사용자 데이터: `~/.prismone-studio/` — connections.json(암호문) · key.bin(0600) ·
  history.jsonl · options.json

## 문서 지도

| 파일 | 내용 |
|---|---|
| `USER_GUIDE.md` | 사용법 (기능 추가 시 **반드시 함께 갱신**) |
| `GOLDEN_BEHAVIOR.md` | Golden 동작 명세 + 갭 목록 (바이너리·매뉴얼 근거) |
| `PG_FEATURES.md` | pgAdmin·PostgreSQL 고유 기능 채택 계획 |
| `STUDIO_PLAN.md` | 초기 파리티 계획 (P1~P5) |
| `TOOL_COMPARISON.md` | **Golden vs DataGrip 장단점** — Aurum 이 무엇을 취하고 무엇을 버릴지 |
| `DATAGRIP_GAP.md` | DataGrip 대비 기능 갭과 우선순위 |
| `MULTI_DB_PLAN.md` | **멀티 DB 계획** — PG/Oracle/SQLite/MongoDB provider 단계 |
| `UI_POLISH.md` | **UI 다듬기 로드맵** — 실제품 수준까지 (다크 모드·로딩 피드백·토스트 등) |
| `STATUS.md` | 이 문서 — 진행 상황과 다음 작업 |
