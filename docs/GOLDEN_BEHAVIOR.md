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

→ PG 매핑: Category 필드 추가, 비밀번호 미저장 항목 더블클릭 시 비밀번호만 묻는 프롬프트.

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
7. 그리드: transpose, 셀 상세, 컬럼 fit, xlsx/INSERT export (P4)
8. ~~Output 창 (RAISE NOTICE)~~ ✅ 구현됨 (Messages pane)
9. 워크스페이스 저장/복원 (P5)
10. ~~EditMode(그리드 편집)~~ ✅ 구현됨 — Run and Edit(F11). Golden 이 Oracle ROWID 로 행을
    특정하던 것을 PG 에선 `ctid` 로 대응. Submit(Ctrl+Shift+S) 시 한 트랜잭션,
    영향 행 ≠ 1 이면 전체 롤백. 붙여넣기 다중 insert(Paste Rows)도 구현됨

**시작 시 로그온 창**: Golden 은 실행하면 메인 창 위로 로그온 창을 곧바로 띄운다
(2026-08-03 실제 동작 확인 — 그전 기록이던 "메인 창만 먼저"는 정정). 우리도 동일하게
바꿨고, 취소하면 미접속 상태로 남는다.

상태바 형식("Done, ran x of y statements."), Ctrl+L 로그온,
점진 fetch 자체, 커서 문장 실행, Explain, 로그온 리스트 구조는 이미 일치.
