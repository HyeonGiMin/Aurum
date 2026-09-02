# Golden 8 동작 명세 (분석 결과)

출처: 공식 배포판 `golden_846.zip`(benthicsoftware.com, 2026-07 빌드)의 실행 바이너리에서 추출한
UI 문자열·액션 캡션/힌트 + 공식 스크린샷(golden8-popupshowing.png) + 제품 페이지.
아래 인용문("...")은 바이너리에서 추출한 원문 그대로다.

## 1. 시작 흐름

- 메인 창(워크스페이스)이 먼저 뜬다. 로그온은 **Ctrl+L** 로 연다 (시작 시 자동 팝업 아님).
- **워크스페이스** 개념: 열린 탭들의 집합을 파일로 저장/복원.
  "Golden Workspace Files", "Close Workspace", "Clear Recent Workspace List...",
  "Workspace save marks tabs as saved", 워크스페이스 백업 생성.
- 최근 파일 목록: "This file couldn't be loaded. Would you like to remove it from the recent files list?"

## 2. 로그온 (LoginPlus)

- 위쪽 입력 필드 + 아래 **저장된 로그인 항목 리스트** 구조. 항목 관리: "New Login Item",
  "Editing existing Login Item".
- 리스트는 **Username / Database / Category 로 필터** 가능 (로그인 항목에 카테고리 속성 존재).
- **SavePassword 플래그** — 저장 안 한 항목 선택 시 "Enter password for %s:" 프롬프트만 띄운다.
- Oracle 전용: AS SYSDBA 접속 옵션, 비밀번호 변경 유도("Would you like to change your password?").
- "Simple Login" 모드 존재 (간단/상세 전환).

→ PG 매핑: ~~Category 필드 추가~~ ✅ 구현됨 (Edit 버튼으로 Name/Category/Comment 편집,
Filter ▾ 로 Username/Database/Category 필터). 비밀번호 미저장 항목은 빈 비밀번호로
로그인 시 "Enter password for %s:" 안내 후 비밀번호 입력만 받는다.

## 3. 세션 모델 (중요 — 우리 구현과 다름)

- **메인 창 = 접속 1개. 탭들은 그 접속을 공유한다.**
- 예외: "**Open into New Private Tab** | Open file into a new tab with it's own private
  database connection." — 원하는 탭만 전용 접속을 가진다.
- 시사점: 공유 세션이라 한 탭이 실행 중이면 다른 탭도 대기(직렬화)해야 하고,
  트랜잭션(Commit/Rollback)이 모든 공유 탭에 걸린다. private 탭만 독립 트랜잭션.

## 4. 실행

| 동작 | Golden 원문 | 비고 |
|---|---|---|
| Run current statement | "Run just the current statement at the current cursor position." | 커서 문장 1개 |
| Run single statement no caret move | 실행 후 커서 이동 없음 (기본은 다음 문장으로 이동한다는 뜻) | |
| Run script | "Run script from current cursor position. Cursor must be at the beginning of a statement." | **커서부터 끝까지**, 전체가 아님 |
| Run script 단축키 | **F5 + Shift+Enter** ("Check this to disable the Shift-Enter hotkey (F5 will still work to run script.)") | |
| Run and Edit | "Run script and go to edit mode" / 단문 버전도 존재 | 결과 그리드가 **편집 모드**로 |
| Explain | Oracle explain plan 삽입 실행. 오류 시 "Run the statement without explain plan and fix the errors." | PG는 EXPLAIN prefix |
| Cancel | "Execute Aborted", "Canceled after ..." | |
| 상태 표시 | "Done, ran %s of %s statements in %s." / "Done, ran single statement in ..." / "Running statement ...", "Running single statement at cursor." | |
| 핫키 | 사용자 정의 가능: "Enter Hotkey \| Press the hotkey you'd like to use.", "Hotkey must be a function key or include Ctrl or Alt." | 옵션에서 재배치 |

### 4.1 공식 키맵 (Golden 매뉴얼 §4.3 Keyboard shortcuts — ondoc.logand.com/d/368/pdf)

✅ 구현됨 (2026-08-03). 충돌 판정 원칙: Golden 키를 우선하되, 현대 관행 키는 별칭으로 공존.

| Golden | 키 | 우리 구현 |
|---|---|---|
| Run Script | F5 / Shift+Enter | ✅ 동일 |
| Run Script From Cursor | F6 | ✅ F6 (우리 F5 도 커서부터 — Golden 8 시맨틱) |
| Run One Statement At Cursor | F7 / Ctrl+Enter | ✅ + F9 (Golden 8 기준) |
| **Run Script And Go To Edit Mode** | **Ctrl+E** | ✅ + F11 별칭 |
| **Run Selected** | **Ctrl+F7** | ✅ 동일 — 선택 영역만 실행 (선택이 없으면 실행 안 함) |
| Commit / Rollback | Ctrl+F5 / Ctrl+F6 | ✅ 동일 |
| Login | Ctrl+L / Ctrl+J | ✅ 동일 |
| New Tab / Private | Ctrl+N / Shift+Ctrl+Alt+N | ✅ Ctrl+N·T / +Shift |
| Close Tab | Ctrl+F4 | ✅ + Ctrl+W(관행) |
| Goto Next/Prior Tab | Ctrl+Tab / Shift+Ctrl+Tab | ✅ 동일 |
| Save Workspace | Shift+Ctrl+W | ✅ 동일 (Open Workspace 의 Ctrl+W 는 탭 닫기 관행과 충돌 → 메뉴로) |
| Find / Find Next / Replace | Ctrl+F / F3 / Ctrl+H | ✅ 동일 (F3 는 에디터 내장) |
| Comment / Uncomment | Ctrl+- / Shift+Ctrl+- | ✅ 동일 |
| Toggle Edit↔Results | Ctrl+R / F8 | ✅ Ctrl+R (F8 은 우리 Object Browser) |
| Show Execution Plan | Ctrl+P | ❌ 우리 Ctrl+P 는 Print(관행) — Explain 은 툴바/메뉴 |
| Block Indent/Unindent | Tab / Shift+Tab (선택 시) | ✅ 에디터 내장 |

## 5. Fetch (점진 로딩)

- **초기 100행**: "This is the initial record count for a query. Default is 100."
  "Fetches first 100 records into grid. Scroll down to fetch more."
- **배치 100행**: "The number of records a query fetches at once. Default is 100." (ArraySize)
- 탭별 상한: "Enter the recordset size limit for this tab (-1 for unlimited)"
- Fetch All / "Fetch Next Block" 액션, 상태 "Fetched: N".

## 6. 트랜잭션

- "Commit | Commit the current Transaction." / "Rollback | Rollback the current transaction."
- **Autocommit** 토글 존재 (기본 off — Oracle 관례).
- 실행 중 커밋 불가 방어: "cannot commit transaction - SQL statements in progress".

## 7. 그리드

- **Transpose Columns/Records** (행/열 전치)
- 필터: "Filter records like selected cell."
- "Size Column to Fit" / "Size All Columns to Fit"
- 셀 상세 창(멀티라인/이미지), 정렬, 커스텀 포맷
- **편집 모드(EditMode)**: 하단 insert row, 붙여넣기로 다중 insert/update
  ("EditMode: Paste inserted %d records.", "Delete %d selected records?")
- Export: **xlsx / xls / txt / INSERT 문** ("Only applies to INSERT statements.",
  "Add blank line between statements."), 인쇄 + 미리보기

## 8. 에디터

- 키워드 하이라이팅(적갈색 굵게), 자동완성 팝업(테이블/컬럼 목록 — 스크린샷 확인)
- Find / Replace, Block indent/unindent, 멀티 캐럿(v8, VSCode 스타일)
- SQL 인쇄/미리보기

## 9. 기타 창

- **Output 창**: dbms_output 수신("Get output | Get dbms_output buffer from the database..."),
  Clear/Copy All/Save → PG 매핑: RAISE NOTICE/WARNING 메시지 수신
- **Favorites**: 즐겨찾기 쿼리 메뉴(필터 지원). 기본은 SELECT만 실행 허용
  ("Allow non-Select statements to run from the Favorites Menu." 옵션)
- **History**: 쿼리 히스토리 (서버 저장 옵션 UseServerHistory 존재)
- ~~SQLBuilder(비주얼 쿼리 빌더)~~ ✅ 구현됨 (Tools > SQL Builder — 테이블·컬럼·WHERE·정렬·Limit,
  미리보기 후 에디터 삽입), Describe 창(스키마/타입별)

## 10. 우리 구현과의 갭 (우선순위순)

1. ~~**세션 모델**: 탭=세션 → 탭들이 메인 세션 공유 + private 탭~~ ✅ 구현됨 (Ctrl+Shift+T)
2. ~~**Run script 시맨틱**: F5 = 커서부터 끝까지~~ ✅ 구현됨 (F5 / Shift+Enter)
3. **fetch 기본값**: ✅ 초기/배치 100. 옵션화·탭별 상한(-1)은 미구현
4. Commit/Rollback/Autocommit (P4 예정 — 공유 세션 위에서)
5. ~~자동완성 팝업, Find~~ ✅ 구현됨 (Replace 는 검색 패널 내장)
6. ~~히스토리~~ ✅ 구현됨 (Ctrl+↑↓). ~~Favorites~~ ✅ 구현됨 (Ctrl+Shift+F · Favorites 메뉴 ·
   Manage 창 · "SELECT 이외 실행 허용" 옵션)
7. ~~그리드: transpose, 셀 상세, 컬럼 fit, xlsx/INSERT export~~ ✅ 구현됨
   (xlsx 는 외부 라이브러리 없이 OOXML 직접 생성 — xls(구형 바이너리)는 xlsx 로 대체)
8. ~~Output 창 (RAISE NOTICE)~~ ✅ 구현됨 (Messages pane)
9. 워크스페이스 저장/복원 (P5)
10. ~~EditMode(그리드 편집)~~ ✅ 구현됨 — Run and Edit(Ctrl+E/F11). 행 특정은 DB 별 의사
    컬럼(Oracle `ROWID` — Golden 원조 방식, PG `ctid`, SQLite `rowid`)을 provider 가 정한다
    (2026-09-02 Oracle/SQLite 확장). Submit(Ctrl+Shift+S) 시 한 트랜잭션,
    영향 행 ≠ 1 이면 전체 롤백. 붙여넣기 다중 insert(Paste Rows)도 구현됨.
    Oracle 은 날짜 셀을 문자열로 바인딩하므로 세션에 NLS 형식(YYYY-MM-DD HH24:MI:SS)을 건다

**시작 시 로그온 창**: Golden 은 실행하면 메인 창 위로 로그온 창을 곧바로 띄운다
(2026-08-03 실제 동작 확인 — 그전 기록이던 "메인 창만 먼저"는 정정). 우리도 동일하게
바꿨고, 취소하면 미접속 상태로 남는다.

상태바 형식("Done, ran x of y statements."), Ctrl+L 로그온,
점진 fetch 자체, 커서 문장 실행, Explain, 로그온 리스트 구조는 이미 일치.

## 6. Golden 6 실물 확인 (2026-08-04)

`C:\Program Files\Benthic\Golden6.exe` 를 직접 띄워 메뉴를 확인했다. 위 §1~5 는 Golden 8
바이너리에서 추출한 문자열 기반이고, 이 절은 **Golden 6 화면 실물**이 근거다.
(접속 정보는 기록하지 않는다 — 사내 위키 참조)

### View 메뉴 (실물)

| 항목 | 키 | 우리 상태 |
|---|---|---|
| SQL Builder | F9 | ✅ Tools 메뉴 (우리 F9 는 문장 실행이라 키는 다름) |
| DBMS OUTPUT Window | F10 | △ Messages 패널로 대응 (별도 창 아님) |
| Cell Details Window | Ctrl+F11 | ✅ 구현됨 (2026-08-04) |
| Scratch Window of current results | F11 | ❌ 미구현 (우리 F11 은 Run and Edit) |
| **Toggle DataGrid/Text View/Log View** | **F12** | ✅ 구현됨 (2026-08-04, 툴바 `Show: ▾` + F12) |
| Data View ▸ | | ❌ 미구현 |
| Toggle SQL/DataGrid Orientation | | ❌ 미구현 (에디터·그리드 좌우 배치) |
| Next / Previous Tab | Ctrl+Tab / ⇧Ctrl+Tab | ✅ 동일 |
| Change tab order or names… | | ❌ 미구현 |

### Results 메뉴 (실물)

| 항목 | 키 | 우리 상태 |
|---|---|---|
| Find in Results… | | ❌ 미구현 (에디터 Find 만 있음) |
| Goto Record Number… | Ctrl+G | ✅ 구현됨 (2026-08-04) |
| Spreadsheet Autosize ▸ | | △ Size All Columns to Fit 단일 명령 |
| Clear Spreadsheet | | ✅ Clear Results 로 구현됨 (2026-08-04) |
| Bind Variable Cursors ▸ | | ❌ 미구현 |
| Format Column… / Clear Column Format | | ❌ 미구현 |
| Filter records like selected cell. | | ✅ 구현됨 (2026-08-04, 그리드 실제 필터) |
| Clear Filter | | ✅ 구현됨 (2026-08-04) |
| Advanced Export… | | ❌ 미구현 |
| Export results to Excel | Ctrl+Alt+E | ✅ xlsx 저장 (키는 다름) |
| Export results to OpenOffice Calc | | ❌ 미구현 |
| Export results to a CSV file… | | ✅ 구현됨 |
| Export results to an XML File… | | ❌ 미구현 |

### 로그인 창 (실물)

`Login: <id>` 헤더 + Username / Password / Database / Read Only,
버튼 Login · Close · **Help** · **Options…**,
Login List 컬럼 Name / Username / Database / **AltSchema** / …,
버튼 New · Edit · Delete · Filter ▾ · **Import/Export ▾**

우리 미구현: **AltSchema 컬럼**, **로그인 목록 Import/Export**, Help·Options 버튼.

### 남은 갭 (우선순위)

1. Toggle SQL/DataGrid Orientation (와이드 모니터에서 체감 큼)
2. Find in Results (결과 그리드 내 검색)
3. 탭 이름 변경 / 순서 바꾸기
4. 로그인 목록 Import/Export, AltSchema
5. XML export, Advanced Export, Format Column, Scratch Window, Data View
