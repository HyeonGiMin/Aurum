# IAP Database Manager 사용법

Oracle 시절 Golden 을 대체하는 PRISMONE(PostgreSQL)용 쿼리 툴입니다.
Golden 을 쓰던 손버릇 그대로 쓰이도록 만들었습니다.

## 1. 실행

| 방법 | 용도 |
|---|---|
| `tools/dist/IAP Database Manager.app` | macOS 정식 실행 (독에 이름/아이콘 표시). `sh tools/packaging/macos/make-app.sh` 로 생성 |
| `tools/dist/IAP Database Manager/IAP Database Manager.exe` | Windows 정식 실행 (.NET 설치 불필요한 단일 exe). `powershell -ExecutionPolicy Bypass -File tools/packaging/windows/make-app.ps1` 로 생성 |
| `cd tools && dotnet run --project src/PrismOne.Studio` | 개발 실행 |

앱을 켜면 빈 Query 1 탭이 있는 메인 창이 뜨고 그 위로 **로그온 창이 바로 열립니다**(Golden 과 동일).
취소하면 미접속 상태로 남습니다 — 쿼리를 미리 써둔 뒤 Ctrl+L 로 접속해도 됩니다.

## 2. 로그온 (Ctrl+L)

- **Database 는 `host[:port]/database` 한 칸**입니다. 예: `<dev-host>/prismone`,
  `stg-ihp5022:5433/prismone`, 포트 생략 시 5432, `prismone` 만 쓰면 localhost.
- 아래 **Login List** 에서 클릭=필드 채움, **더블클릭=바로 로그인**. New/Delete 로 관리.
- **Edit** — 선택한 항목의 표시 이름(Name)·**Category**·Comment 를 고칩니다
  (접속 정보 자체는 필드에서 고쳐 다시 로그인하면 갱신).
- **Filter ▾** — 목록 위에 필터 행이 열리고 **Username / Database / Category** 로
  즉시 걸러집니다(부분 일치, 대소문자 무시). 접속 대상이 많을 때 Category 를
  운영/스테이징 등으로 달아두면 빠르게 찾을 수 있습니다.
- **비밀번호는 항상 AES-256 으로 암호화되어 저장**됩니다 (`~/.prismone-studio/`,
  키는 이 계정 전용). 비밀번호에 한글이 들어오면 두벌식 기준 영문키로 자동 변환됩니다
  (`암호` → `dkagh` — IME 끄는 걸 잊어도 안전).
- **Read Only** 체크: 세션이 읽기 전용(`default_transaction_read_only=on`)으로 열려
  실수로 UPDATE 를 날려도 거부됩니다. 운영 DB 조회용으로 권장.
- 로그인하면 열려 있던 빈 탭들에 세션이 붙습니다.
- 상태바 왼쪽 **접속 pill** 로 상태를 알 수 있습니다 — 미접속이면 빨강 `Disconnected`,
  접속되면 초록 `user@host:port/db`.
- 접속한 DB 가 PRISMONE 설치본이면 그 옆에 파란 **Schema pill** 이 나타납니다 —
  `schema_version` 에 기록된 마지막 적용 패치(예: `Schema: 20260718_01`, 기록이 없으면
  `Schema: baseline`)이고, 툴팁에 적용 시각·건수가 보입니다. **조회 전용**입니다 —
  패치 적용은 `iapdb` CLI 로 합니다 (schema_version 테이블이 없는 DB 에선 숨겨집니다).

## 3. 쿼리 실행

| 키 | 동작 |
|---|---|
| **F9** (또는 Cmd/Ctrl+Enter) | **커서 위치 문장 하나** 실행. 선택 영역이 있으면 선택만 |
| **F5** (또는 Shift+Enter) | **Run Script — 커서 문장부터 끝까지** 순차 실행 |
| ⚡ᴱ | **Explain** — 커서 문장의 실행 계획을 트리로 (실행 안 함) |
| ⚡ᴬ | **Explain Analyze** — 실제 실행 후 노드별 실측 시간 트리. **DML 은 자동 롤백**되어 안전. 전체 시간의 50%↑ 노드는 빨강, 20%↑ 주황. 노드에 마우스를 올리면 Filter/Index Cond 표시 |
| 툴바 ⊘ | 실행 취소 (서버에 cancel 전송) |

- 문장은 세미콜론으로 나뉘며 문자열·주석·**달러쿼팅**(`$$…$$`) 안의 `;` 는 무시됩니다.
- 실행 중엔 이전 결과가 비워지고 상태바에 `Running…` + 경과 시간이 올라갑니다.
  실행 중 재실행 요청은 무시됩니다(취소만 가능).

### 바인드 변수 (`:name`)

문장에 `:key` 같은 변수가 있으면 실행할 때 **값 입력 창**이 뜹니다 (Golden 방식).
값은 탭 안에서 기억되므로 다음 실행 땐 이전 값이 채워져 있습니다.
툴바 노란 타원 버튼으로 미리 입력해 둘 수도 있습니다. 비워 두면 NULL 로 전달됩니다.

```sql
select * from prismone.study where study_key = :key and modality = :mod;
```

문자열·주석·달러쿼팅 안의 `:`, 타입 캐스트(`::date`)는 변수로 보지 않습니다.
값은 SQL 에 문자열로 끼워 넣지 않고 **파라미터로 전달**되므로 인젝션 걱정이 없습니다.

## 4. 결과 그리드 — 점진 fetch

- 첫 **100행**이 즉시 표시되고, **스크롤을 내리면 100행씩 이어서** 가져옵니다
  (상태바 `Fetched 300 records (more)`). 큰 테이블도 LIMIT 없이 바로 조회하세요.
- **Ctrl+End** = 끝까지 전부 fetch.
- 셀 값이 아주 크면(예: dcmdataset 의 JSONB) 표시는 500자에서 잘리고
  `… (+N chars)` 로 표기됩니다.
- **셀 더블클릭 → Cell Detail 창**: 잘림 없는 전문을 보여주고, **JSON(jsonb)이면
  자동으로 pretty-print** 됩니다. DICOM Data Set 열람에 사용하세요.
- 셀 복사(헤더 포함), 컬럼 리사이즈 지원. 빈 결과는 `▸ 1 No Records`.

### 그리드 기능 (Results 메뉴)

| 기능 | 설명 |
|---|---|
| **Transpose** (Ctrl+Shift+X) | 행/열 전치 — 컬럼이 많은 한 행을 세로로 읽을 때. 다시 누르면 원래대로 |
| **Size All Columns to Fit** | 모든 컬럼 폭을 내용에 맞춤 |
| **Filter Like Selected Cell** | 선택 셀 값으로 `WHERE` 절을 만들어 에디터 끝에 주석으로 덧붙임 |

## 4.5 Run and Edit — 그리드에서 직접 고치기 (Golden EditMode)

**F11**(Script > Run and Edit) 을 누르면 커서 위치 문장을 편집 모드로 다시 실행합니다.

- **단일 테이블 SELECT 만** 가능합니다. 조인·`DISTINCT`·`GROUP BY`·집합연산·서브쿼리가 있으면
  거부하고 이유를 상태바에 표시합니다 (행을 특정할 수 없기 때문).
- 행 식별은 Golden 이 Oracle ROWID 를 쓰던 것과 같은 방식으로 **PG 의 `ctid`** 를 씁니다.
  편집 모드 SELECT 에 `ctid` 컬럼이 자동으로 붙지만 그리드에는 보이지 않습니다.
- 셀을 직접 고치고, **Add Row** 로 새 행을 추가하고, 행을 선택해
  **Delete Selected Records…**(`Delete N selected records?` 확인) 로 삭제 표시합니다.
- **Paste Rows** — 클립보드의 **탭 구분 표**(엑셀에서 복사한 범위, 우리 TSV 내보내기)를
  새 행으로 한꺼번에 넣습니다. 첫 줄이 컬럼명과 같으면 헤더로 보고 건너뜁니다.
  붙여넣은 행도 Submit 해야 INSERT 됩니다.
- **Submit Edits (Ctrl+Shift+S)** 를 눌러야 DB 로 나갑니다. 그전까지는 아무것도 반영되지 않습니다.
  - 변경분은 **한 트랜잭션**에서 UPDATE → DELETE → INSERT 순으로 실행됩니다.
  - 어느 한 문장이라도 **영향 행이 1 이 아니면 전부 롤백**합니다. 다른 사람이 먼저 고쳤거나
    `ctid` 가 바뀐 경우로, 다시 조회한 뒤 작업하세요.
  - Tx Mode 가 Manual(기본)이면 커밋되지 않은 채 `[TX]` 로 남습니다 — 툴바 ✓ 로 확정하세요.
- **Revert Edits** 는 원래 쿼리를 다시 실행해 편집 전으로 되돌립니다.
- **빈 칸은 NULL 로 저장**됩니다. 빈 문자열이 필요하면 Run and Edit 대신 UPDATE 문을 쓰세요.
- 편집 모드에서는 옵션의 NULL 표시 문자열을 적용하지 않습니다(그 값이 그대로 저장되면 곤란하므로).
- 다른 문장을 실행하면 편집 모드가 자동으로 풀립니다.

## 5. 트랜잭션 (Golden 방식 + DataGrip 툴바)

- 툴바의 **`Tx: Manual ▾` 버튼**을 누르면 드롭다운이 열리고, 그 안에
  **Transaction Mode**(Auto·Manual)와 **Tx Isolation**(5개)이 함께 있습니다.
  현재 값 앞에 ✓ 가 붙고, 항목에 마우스를 올리면 설명이 뜹니다.
  DataGrip 2024.2 의 Tx 드롭다운과 같은 구성·표기입니다.
- **Tx Mode 는 기본 Manual**. INSERT/UPDATE/DDL 을 실행하면
  자동으로 트랜잭션이 열리고 상태바 앞에 **`[TX]`** 가 붙습니다.
  Auto 로 바꾸면 문장마다 자동 커밋됩니다. 설정은 **탭(세션) 단위**로 적용됩니다.
- **Tx Isolation** — DataGrip 과 동일한 5개:

  | 항목 | 의미 |
  |---|---|
  | **Database Default** (기본) | 서버/DB 설정값을 그대로 사용 (`RESET default_transaction_isolation`) |
  | Read Uncommitted | PG 는 받아들이지만 실제 동작은 Read Committed 와 같습니다 |
  | Read Committed | 커밋된 변경만 탐지 (PG 의 실질 기본값) |
  | Repeatable Read | 동시에 발생한 변경을 탐지하지 않음 |
  | Serializable | 동시 실행이 직렬 실행과 같은 결과 |

  고른 값은 `SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL …` 로 세션에 걸리며,
  **열린 트랜잭션이 있으면 그 트랜잭션이 끝난 뒤부터** 적용됩니다(PG 규약 — 상태바에 안내가 뜹니다).
  마지막에 고른 값은 옵션에 저장돼 다음 접속·새 세션에도 걸리고, 세션이 끊겨 재접속해도 유지됩니다.
- 툴바 **✓ Commit / ⤺ Rollback** 으로 확정/되돌리기. PG 는 **DDL 도 롤백**됩니다.
- PG 특성 반영: 수동 커밋 모드여도 **SELECT/EXPLAIN/SHOW 는 트랜잭션을 열지 않습니다**
  (idle-in-transaction 이 서버 VACUUM 을 방해하는 문제 회피).
- 직접 `BEGIN`/`COMMIT`/`ROLLBACK` 을 실행해도 상태를 따라갑니다.
- fetch 가 진행 중이면 커밋이 거부됩니다 — Fetch All 또는 Cancel 후 커밋하세요.

## 6. 자동완성

- **Ctrl+Space** (macOS 는 **Option+Space** 도) — 수동 호출.
- **`.` 입력 시 자동**: `prismone.` → 테이블 목록, `study.` / `s.`(별칭) → **컬럼 목록**
  (FROM/JOIN 의 별칭을 해석합니다. `from prismone.study s` 후 `s.` → study 컬럼).
- **FROM/JOIN 뒤에서는 스페이스/글자 입력만으로 테이블 목록이 자동으로** 뜹니다.
- 팝업이 뜬 뒤 계속 타이핑하면 필터링, Enter/Tab 으로 삽입.

## 7. 히스토리 · 검색

- **Ctrl+↑ / Ctrl+↓** (툴바 ◀ ▶) — 실행했던 문장 순환. 끝까지 가면 작성 중이던
  초안으로 복귀. 히스토리는 재시작 후에도 유지(최근 500개).
- **Ctrl+F** (툴바 돋보기) — 에디터 검색 패널.

## 7.2 Favorites (즐겨찾기)

자주 쓰는 쿼리를 이름 붙여 저장해 두고 메뉴에서 바로 실행합니다 (Golden 의 Favorites).

- **Ctrl+Shift+F** — 커서 위치 문장(선택 영역이 있으면 선택분)을 즐겨찾기에 추가.
  이름이 SQL 앞부분으로 채워져 뜨니 알아볼 이름으로 고쳐 **Save**.
- **Favorites 메뉴** — 저장된 항목이 이름순으로 나열됩니다. 항목을 고르면 **그 SQL 이
  현재 탭에 올라가고 바로 실행**됩니다(마우스를 올리면 전문이 툴팁으로 보입니다).
- **Favorites > Manage Favorites…** — 필터(이름·SQL 부분일치), 이름/SQL 수정(Save),
  삭제(Delete), 실행(Run 또는 목록 더블클릭), **Insert into Editor**(실행 없이 커서 위치에 삽입).
- **기본은 SELECT 계열만 실행**됩니다. `WITH … (INSERT … RETURNING)` 처럼 쓰기가 섞인
  문장은 막히고 상태바에 이유가 표시됩니다. 풀려면
  **Tools > Options > "Favorites 메뉴에서 SELECT 이외의 문장도 실행 허용"** 을 켜세요
  (Golden 의 "Allow non-Select statements to run from the Favorites Menu.").
- 목록은 `~/.prismone-studio/favorites.json` 에 저장됩니다.

## 7.5 Describe (Ctrl+D)

에디터에서 **테이블 이름 위에 커서를 두고 Ctrl+D** 를 누르면 Object Browser 가 열리며
그 테이블이 선택되고 컬럼 목록(describe)이 표시됩니다. `prismone.study` 처럼 스키마를
붙여도 되고 `study` 만 써도 됩니다.

## 8. Object Browser (F8)

- 툴바 ≡ 버튼 또는 **F8** 로 오른쪽 패널 토글 (기본 숨김 — Golden 6 레이아웃).
- Schema/Show(Tables·Views·All) 필터, TableSearch 로 이름 검색.
- 테이블 **클릭 → 아래 describe** (# / Name / Type / N? / PK / FK).
- 테이블 **더블클릭 → 쿼리에 이름 붙여넣기** (`Use Schema` 체크 시 스키마 접두어 포함).
- `select / * / from / where` 미니 그리드: 클릭하면 해당 단어 삽입.

## 9. Messages (RAISE NOTICE)

PG 함수가 `RAISE NOTICE/WARNING` 을 내면 결과 아래 **Messages pane** 이 자동으로
열립니다. 새 실행 때 초기화됩니다.
테스트: `DO $$ BEGIN RAISE NOTICE 'hello %', now(); END $$;`

## 9.4 SQL Builder (Tools 메뉴)

쿼리를 손으로 쓰지 않고 골라서 만드는 창입니다 (Golden 의 SQLBuilder).

- 왼쪽에서 **테이블**을 고르면 컬럼 목록이 뜹니다. 체크한 컬럼만 SELECT 되고,
  아무것도 체크하지 않으면 `*` 입니다. `별칭 s` 를 켜면 `from ... s` 와 `s.컬럼` 형태가 됩니다.
- **+ 조건** 으로 WHERE 를 추가합니다. 연산자는 `=`, `<>`, `>`, `>=`, `<`, `<=`,
  `LIKE`, `ILIKE`, `IN`, `IS NULL`, `IS NOT NULL`. 조건들은 `and` 로 묶입니다.
- 값은 자동으로 처리됩니다 — 숫자와 바인드 변수(`:key`)는 그대로, 나머지는 작은따옴표로
  인용(`it's` → `'it''s'`)됩니다. `IN` 은 콤마로 끊어 각각 인용합니다.
- Order by · Limit 를 지정할 수 있고, 아래 **미리보기**에 완성된 SQL 이 실시간으로 보입니다.
- **Insert into Editor** 를 누르면 에디터 커서 위치에 삽입됩니다. **실행은 하지 않습니다** —
  확인한 뒤 F9 로 직접 실행하세요.

## 9.5 Session Monitor (Tools 메뉴)

**Tools > Session Monitor** — 현재 DB 의 접속 세션(pg_stat_activity)을 보여줍니다.
PID·사용자·클라이언트·상태·경과시간·대기 이벤트·쿼리. Auto(5s) 자동 새로고침,
**Cancel Query**(쿼리만 취소) / **Terminate**(세션 종료 — 권한 필요).
공유 staging 에서 누가 물고 있는지 확인할 때 사용.

## 10. 파일 · 내보내기 · 탭

- **Ctrl+O** 스크립트 열기(새 탭으로) / **Ctrl+S** 저장.
- Results 메뉴의 내보내기 4종:
  - **Export All Rows As CSV… (COPY)** — 마지막 실행 쿼리를 서버에서 `COPY … TO STDOUT`
    으로 다시 실행해 **전체 행을 잘림 없이 고속으로**. COPY 불가 문장이면 로드된 행 폴백.
  - **Save Grid As TSV…** — 로드된 행을 탭 구분 텍스트로 (엑셀 붙여넣기용).
  - **Save Grid As INSERT…** — 로드된 행을 `INSERT INTO …` 문으로. 대상 테이블명은
    쿼리의 FROM 절에서 추정합니다. 숫자/boolean 은 그대로, NULL 은 NULL 로.
  - **Save Grid As xlsx…** — 로드된 행을 Excel 통합문서로. 헤더는 굵게, 숫자는 숫자
    셀로(단 `007` 처럼 표기가 바뀌는 값은 문자열 유지), NULL 은 빈 셀.
    시트 이름은 대상 테이블명. 외부 라이브러리 없이 직접 생성하므로 가볍고 빠릅니다
    (10만 행 × 10컬럼 ≈ 0.2초). Excel 행 상한(약 105만)을 넘으면 잘라내고 알립니다.
- **Ctrl+T** 새 탭 / **Ctrl+W** 탭 닫기 / 탭줄 오른쪽 **▾** 탭 목록.

### 인쇄 (Golden 의 Print / Print Preview)

- **Script > Print SQL… (Ctrl+P)** — 에디터 내용 인쇄. **Results > Print Grid…**(툴바 프린터 버튼)
  — 그리드에 **로드된 행**을 인쇄합니다(전체가 필요하면 Ctrl+End 로 먼저 다 가져오세요).
- 각각 **Print Preview** 항목이 따로 있습니다(인쇄 대화상자를 자동으로 띄우지 않음).
- 동작 방식: Avalonia 에 인쇄 API 가 없어 인쇄용 HTML 을 `%TEMP%\iap-dbm-print\` 에 만들고
  **OS 기본 브라우저로 엽니다**. 용지·여백·프린터 선택은 브라우저 인쇄 대화상자에서 하세요.
  머리말에 탭 이름·접속·생성 시각이 들어가고, 표는 페이지가 넘어가도 헤더가 반복됩니다.

### 세션 모델 (Golden 과 동일)

- **탭들은 메인 접속 하나를 공유합니다.** 따라서 Commit/Rollback 은 공유 탭 전체에 걸리고,
  한 탭이 실행 중이면 다른 탭은 `Busy — another tab is running on this session.` 로 거부됩니다.
  접속 하나에 결과셋 하나이므로, 다른 탭에서 새 문장을 실행하면 이전 탭의 남은 fetch 는
  중단됩니다(`Fetch stopped — another tab used this session.`).
- **Ctrl+Shift+T = New Private Tab** — 그 탭만의 **전용 접속**을 엽니다. 독립 트랜잭션이
  필요하거나 긴 쿼리를 돌리면서 다른 탭에서 작업하려면 이걸 쓰세요.

## 11. 단축키 요약

| 키 | 동작 | | 키 | 동작 |
|---|---|---|---|---|
| Ctrl+L | 로그온 | | F9 | 문장 실행 |
| F5 / Shift+Enter | 스크립트 실행 | | Ctrl+End | 전체 fetch |
| Ctrl+Space (⌥Space) | 자동완성 | | Ctrl+F | 찾기 |
| Ctrl+↑ / ↓ | 히스토리 | | F8 | Object Browser |
| Ctrl+T / ⇧T / W | 탭 / 전용탭 / 닫기 |
| Ctrl+D | Describe | | Ctrl+Shift+X | Transpose | | Ctrl+Z / Y | Undo / Redo |
| Ctrl+O / S | 열기 / 저장 | | Ctrl+Shift+F | 즐겨찾기에 추가 |
| F11 | Run and Edit | | Ctrl+Shift+S | Submit Edits |
| Ctrl+P | Print SQL | | | |

(macOS 에선 Ctrl 대신 Cmd 도 동작)

## 11.5 워크스페이스 · 옵션

- **File > Save/Open Workspace…** — 열린 탭들의 제목·SQL·전용접속 여부를 `.iapws` 파일로
  저장하고 그대로 복원합니다 (Golden 의 Workspace).
- **Tools > Options…** (툴바 렌치) — fetch 배치 크기, 탭별 최대 행수(-1 무제한),
  NULL 표시 문자열, `statement_timeout`(ms), AutoCommit(Tx mode) 기본값,
  Favorites 에서 SELECT 이외 문장 실행 허용 여부.
  Tx isolation 기본값은 툴바에서 고른 값이 그대로 저장됩니다.
  설정은 `~/.prismone-studio/options.json` 에 저장되고 새 실행부터 적용됩니다.

## 12. 데이터 파일 · 문제 해결

- `~/.prismone-studio/` — `connections.json`(접속 목록, 비밀번호 암호문),
  `key.bin`(암호화 키, 0600), `history.jsonl`(쿼리 히스토리), `options.json`(옵션),
  `favorites.json`(즐겨찾기 — 이름과 SQL만).
- 키 파일을 지우면 저장된 비밀번호만 무효가 됩니다(접속 목록은 유지) —
  다음 로그인 때 비밀번호만 다시 입력하면 됩니다.
- 세션이 끊기면 다음 실행 때 같은 프로파일로 자동 재접속합니다(열려 있던 트랜잭션은 소멸).
