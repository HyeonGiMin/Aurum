# Aurum 사용법

Oracle 시절 Golden 을 대체하는 PRISMONE(PostgreSQL)용 쿼리 툴입니다.
Golden 을 쓰던 손버릇 그대로 쓰이도록 만들었습니다.

## 1. 실행

| 방법 | 용도 |
|---|---|
| `tools/dist/Aurum.app` | macOS 정식 실행 (독에 이름/아이콘 표시). `sh tools/packaging/macos/make-app.sh` 로 생성 |
| `tools/dist/Aurum/Aurum.exe` | Windows 정식 실행 (.NET 설치 불필요한 단일 exe). `powershell -ExecutionPolicy Bypass -File tools/packaging/windows/make-app.ps1` 로 생성 |
| `cd tools && dotnet run --project src/PrismOne.Studio` | 개발 실행 |

앱을 켜면 빈 Query 1 탭이 있는 메인 창이 뜨고 그 위로 **로그온 창이 바로 열립니다**(Golden 과 동일).
취소하면 미접속 상태로 남습니다 — 쿼리를 미리 써둔 뒤 Ctrl+L 로 접속해도 됩니다.

## 2. 로그온 (Ctrl+L)

- 맨 위 **Type** — 접속할 DB 종류를 고릅니다. **PostgreSQL / Oracle / SQLite**.
  종류에 따라 아래 입력이 달라집니다.

  | 종류 | Database 칸 | 비고 |
  |---|---|---|
  | PostgreSQL | `host[:5432]/database` | 포트 생략 시 5432 |
  | Oracle | `host[:1521]/service` | **서비스 이름**입니다 |
  | SQLite | `C:\path\to\file.db` | 파일 경로. Username/Password 사용 안 함 |

- **Database 는 `host[:port]/database` 한 칸**입니다. 예: `<dev-host>/prismone`,
  포트를 생략하면 종류별 기본 포트, `prismone` 만 쓰면 localhost.
- 아래 **Login List** 에서 클릭=필드 채움, **더블클릭=바로 로그인**. New/Delete 로 관리.
  - 항목을 고르면 **Type 도 그 종류로 복원**됩니다.
  - **Type 은 색 배지**로 표시됩니다 (PostgreSQL 파랑 · Oracle 빨강 · SQLite 남색).
  - **컬럼 헤더를 누르면 정렬**됩니다. 한 번 더 누르면 역순, 세 번째면 해제(저장 순서).
  - 선택은 **행 단위**입니다 (셀 선택 없음).
- 접속에 실패하면 **팝업으로 이유**를 보여줍니다 (드라이버 예외의 내부 원인까지).
  비밀번호와 접속 문자열은 표시하지 않습니다.
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
| **Ctrl+F7** | **Run Selected — 선택 영역만** 실행. 선택이 없으면 실행하지 않습니다(그건 F9/F7 의 몫) |
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

## 4. 결과 그리드 — 전체 fetch + 점진 fetch

- **기본은 전부 가져오기**입니다(최대 5만 행). 로드된 행 수가 곧 전체 건수라 스크롤바가 정확합니다.
- Options 에서 전체 fetch 를 끄거나 5만 행 상한에 걸리면 **점진 fetch(무한 스크롤)** 로 넘어갑니다 —
  **500행씩** 이어서 가져옵니다 (상태바 `Fetched 1500 records (more)`). DataGrip 과 같은 단위입니다.
  큰 테이블도 LIMIT 없이 바로 조회하세요.
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
| **Filter Records Like Selected Cell** | 선택 셀과 **같은 값의 행만 그리드에 남김** (Golden 동작). 편집 모드·Transpose 중에는 막힙니다 |
| **Clear Filter** | 필터를 풀고 원래 행 전체로 되돌림 |
| **Append Filter Clause to Editor** | 선택 셀 값으로 `WHERE` 절을 만들어 에디터 끝에 주석으로 덧붙임 |
| **Clear Results** | 결과 영역만 비움 (에디터·로그는 유지) |
| **컬럼 정렬** | 컬럼 헤더 클릭. 숫자로 읽히는 값은 숫자 크기순(문자열순이 아님), NULL 은 맨 앞. **이미 fetch 된 행만** 정렬됩니다(점진 fetch). Transpose·편집 모드에서는 꺼집니다 |
| **맨 왼쪽 `#`** | 화면 순서 순번 — 정렬해도 항상 1, 2, 3… 입니다(정렬 대상 아님) |
| **실행 즉시 전체 fetch** | **기본 켜짐.** 결과를 끝까지 가져와 **로드된 행 수 = 전체**가 되고 **스크롤바가 정확**해집니다(Golden 의 원래 동작). 무제한으로 두어도 **5만 행에서 자동으로 멈춥니다** — 운영 DB 사고 방지용 안전 상한이고, 걸리면 상태바에 `(limit reached)` 가 뜹니다. 그 뒤는 스크롤이나 `Ctrl+End` 로 이어 가져옵니다. 끄면 500행씩 점진 fetch(무한 스크롤) |
| **전체 레코드 수** | Options 의 *전체 레코드 수 조회* — 점진 fetch 로 둘 때만 씁니다. SELECT 실행 후 `COUNT(*)` 를 따로 돌려 상태바에 `Fetched 2,000 of 12,345 records` 로 보여줍니다. **기본은 꺼짐**. 세는 동안 결과 표시는 막히지 않고(별도 접속), 실패하면 조용히 건너뜁니다 |
| **Goto Record Number…** (Ctrl+G) | 행 번호로 이동·선택 |
| **Cell Details…** (Ctrl+F11) | 선택 셀을 별도 창으로 (더블클릭과 동일, jsonb pretty-print) |

### 결과 보기 전환 — `Show: DataGrid ▾` (툴바)

Golden 과 같은 드롭다운입니다. **탭마다 따로** 기억하며, 탭을 옮기면 버튼 라벨이 그 탭 상태로 바뀝니다.
**F12** 로 `DataGrid → Text → Log` 순환합니다 (Golden 6 View 메뉴의 *Toggle DataGrid/Text View/Log View*).

| 보기 | 내용 |
|---|---|
| **Show DataGrid** | 기본 — 편집·정렬·내보내기가 되는 그리드 |
| **Show Text** | 로드된 행을 **고정폭으로 정렬한 텍스트**(SQL\*Plus 식). 그대로 복사해 메일·이슈에 붙이기 좋습니다. NULL 은 `(null)`, 너무 긴 값은 `…` 로 잘립니다 |
| **Show Log** | 이 탭에서 실행한 문장 기록 — `[14:23:05] select * from prismone.study — 8 row(s), 0.062s`. 오류·취소도 남습니다 |

Text/Log 는 그리드 위에 덮어 표시되며, **오류 메시지는 어느 보기에서든 그대로 우선 표시**됩니다.
로그는 메모리에만 쌓이고 파일로 저장하지 않습니다(탭을 닫으면 사라집니다).

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
- **WHERE / AND / OR / ON / HAVING / SELECT 뒤, 콤마 뒤**에서는 `.` 없이도
  **FROM 에 적힌 테이블들의 컬럼**이 맨 위에 뜹니다. 테이블이 여럿이면 어느 테이블
  컬럼인지 함께 보여주고, PK/FK 도 표시합니다.
- 팝업이 뜬 뒤 계속 타이핑하면 필터링, Enter/Tab 으로 삽입.
- **PostgreSQL · Oracle · SQLite 모두 동작**합니다 — 카탈로그를 DB 종류별로 읽어
  한 번만 캐시하기 때문입니다(접속 직후 한 번 적재, 대형 스키마는 몇 초 걸릴 수 있음).

## 6.5 SQL 검증 — 없는 테이블·컬럼 밑줄

타자를 멈추면(0.6초) 에디터의 SQL 을 스키마 카탈로그와 대조해서
**없는 테이블과 없는 컬럼에 빨간 물결 밑줄**을 긋습니다. 실행 전에 오타를 잡는
DataGrip 의 unresolved reference 표시 대응입니다.

- 검사 대상: `FROM`/`JOIN`/`INSERT INTO`/`UPDATE` 뒤 테이블, `별칭.컬럼` 참조
  (옛날식 콤마 조인 `from a x, b y` 도 모두 봅니다).
- 밑줄에 마우스를 올리면 **무엇이 없는지 툴팁**으로 보여줍니다.
- 원칙은 "확신할 때만 표시" — CTE·서브쿼리 별칭, 모르는 스키마(pg_catalog 등),
  `dual`/`pg_*`/`sqlite_*` 내장 이름, 따옴표 식별자, 함수는 판단하지 않고 넘어갑니다.
  주석·문자열 안은 검사하지 않습니다.
- 자동완성과 같은 introspection 캐시를 쓰므로 **타자 중에 접속을 열지 않습니다.**
  접속 직후 카탈로그가 적재되기 전에는 조용히 꺼져 있습니다.

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

## 9.5 Diagram / ERD (Tools 메뉴)

**Tools > Diagram (ERD)…** — 스키마의 FK 관계를 그림으로 봅니다 (SQL Developer 의
relational model 대응). **읽기 전용**이라 DDL 을 만들거나 바꾸지 않습니다.

- Object Browser 에서 테이블을 고른 상태로 열면 그 테이블이 **Focus** 로 잡힙니다.
- **Focus + Depth** — 선택 테이블에서 몇 홉 안의 이웃까지 볼지 정합니다(기본 1).
  스키마 전체는 한 화면에 읽기 어려우므로 이게 기본 시야입니다.
  체크를 풀면 스키마 전체가 나옵니다.
- **Filter** — 이름 부분 일치로 테이블을 걸러냅니다. 걸린 테이블끼리의 관계만 남습니다.
- **Columns** — `Keys only`(PK/FK 만) / `All`(전체 컬럼). 키가 하나도 없는 테이블은
  앞쪽 컬럼 몇 개를 대신 보여줍니다.
- 관계선 표기: 자식(FK) 쪽 **까마귀발**이 1:N, 눈금 하나면 1:1(FK 컬럼이 PK/UNIQUE 로
  덮이는 경우), 앞의 **빈 원**은 FK 컬럼이 nullable 이라는 뜻(0..N). 부모(PK) 쪽은 항상 눈금.
  뷰는 헤더 색이 다르고, 자기참조는 박스 오른쪽 고리로 그립니다.
- **Group** — 주제영역(Subject Area)을 나누는 기준. `관계`(FK 로 이어진 덩어리 · 기본) /
  `이름 접두어`(`ihp_request_…` 같은 명명 규칙) / `묶지 않음`. 영역마다 색이 붙고 테두리에
  이름과 테이블 수가 표시되며, 테이블 박스도 그 색으로 칠해집니다 —
  SQL Developer Data Modeler 의 subject area 와 같은 방식입니다.
  왼쪽 **범례**에 영역 목록이 나옵니다(툴바의 `범례` 체크로 접기).
- 조작: 테이블 **클릭**하면 강조 + 상태바에 컬럼 수·FK 수, **더블클릭**하면 그 테이블로
  Focus 이동. 빈 곳을 **드래그**하면 이동(팬), **Ctrl+휠** 로 확대/축소(커서 아래 지점 고정),
  Shift+휠 가로 이동, 그냥 휠은 세로 스크롤. **Fit** 으로 창에 맞춤.
  단축키: `+` `−` 줌 · `0` Fit · `1` 원래 크기 · `F5` 새로고침 · `Esc` 선택 해제.
- **Save PNG…** — 화면 줌과 무관하게 100% 크기로 이미지를 저장합니다.
- FK 제약이 걸려 있지 않은 스키마는 관계선이 나오지 않습니다 — 상태바가 알려줍니다.
  (논리적으로만 연결하고 FK 를 안 거는 스키마가 있습니다)
- 현재는 **PostgreSQL 만** 지원합니다. Oracle 은 접속 지원이 들어오면 같은 창에서 동작하도록
  카탈로그 계층(`IErdCatalog`)을 분리해 두었습니다.

## 9.6 Session Monitor (Tools 메뉴)

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

Golden 매뉴얼의 공식 키맵을 따르고, 현대 관행 키를 별칭으로 함께 둡니다.

| 키 | 동작 | | 키 | 동작 |
|---|---|---|---|---|
| Ctrl+L / Ctrl+J | 로그온 | | F9 / F7 / Ctrl+Enter | 문장 실행 |
| F5 / F6 / Shift+Enter | 스크립트 실행 (커서부터) | | Ctrl+End | 전체 fetch |
| **Ctrl+F7** | **Run Selected (선택 영역만)** | | | |
| **Ctrl+E** / F11 | **Run and Edit (편집 모드)** | | Ctrl+Shift+S | Submit Edits |
| **Ctrl+F5** | **Commit** | | **Ctrl+F6** | **Rollback** |
| Ctrl+Space (⌥Space) | 자동완성 | | Ctrl+F / Ctrl+H | 찾기 / 바꾸기 |
| Ctrl+↑ / ↓ | 히스토리 | | F8 | Object Browser |
| Ctrl+T · Ctrl+N / +⇧ | 새 탭 / 전용 탭 | | Ctrl+W · Ctrl+F4 | 탭 닫기 |
| Ctrl+Tab / +⇧ | 다음 / 이전 탭 | | Ctrl+Shift+W | 워크스페이스 저장 |
| Ctrl+D | Describe | | Ctrl+Shift+X | Transpose |
| **F12** | **DataGrid/Text/Log 전환** | | **Ctrl+G** | **Goto Record Number** |
| **Ctrl+F11** | **Cell Details** | | | |
| Ctrl+O / S | 열기 / 저장 | | Ctrl+Shift+F | 즐겨찾기에 추가 |
| Ctrl+- / +⇧ | 주석 처리 / 해제 | | Ctrl+R | 에디터 ↔ 결과 포커스 |
| Ctrl+P | Print SQL | | Ctrl+Z / Y | Undo / Redo |

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
