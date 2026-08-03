# 진행 상황 · 다음 작업 (인수인계용)

마지막 갱신: 2026-08-03 · 브랜치 `main`

## 세 가지 목표 대비 현황

| # | 목표 | 상태 |
|---|---|---|
| 1 | **Golden 대체 쿼리 툴** (PostgreSQL) | **완료** — 기능·실접속 검증 모두 끝 (아래 §1) |
| 2 | DB 초기 설치 (IAP 제품용) | **미착수** — CLI 스캐폴드만 존재 |
| 3 | 설치된 DB 패치/업그레이드 | **미착수** |

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
- 아직 미착수 — §2·3 CLI 이후, 별도 계획 문서로 시작한다

## 2·3. CLI

**`iapdb install` 구현·검증 완료** (2026-08-03):

- Core `PsqlScript`(mini-psql, A안) — `\set` · `\if :{?var}`/`\else`/`\endif` · `\gexec`(줄 끝) ·
  `:'v'`/`:"v"`/`:v` 치환(따옴표·달러쿼팅·주석 안은 psql 처럼 제외, `::` 캐스트 무시).
  지원 밖 메타커맨드는 조용히 넘기지 않고 예외. `PsqlScriptRunner` 가 Npgsql 로 실행
  (gexec 는 결과 셀을 SQL 로 재실행). 실제 sql/ 13파일 전부 파싱 테스트 포함 (테스트 158개)
- `iapdb install` — manifest.txt 순서 실행. run_all.sh 와 같은 env(PGHOST·PG_SUPER·DB_NAME…)
  + `--host/--port/--super/--db-*/--ts-*/--dry-run/--verbose`.
  **초기 비밀번호 부트스트랩**: 비밀번호 없이 접속되는 초기 상태(trust)를 감지하면
  `ALTER ROLE … PASSWORD` 로 초기 비밀번호 설정부터 진행(`--set-superpass` 또는 프롬프트).
  인증 실패 시 대화형이면 3회 재입력, 비대화형이면 명확한 오류
- **검증 2종** (모두 vendored pgmq 를 extension 디렉터리에 복사):
  1. 로컬 brew postgresql@16 스크래치 클러스터 — trust → 비밀번호 설정 → 13단계
     5,256문장 전체 성공(테이블 441·pgcrypto/pgmq·테이블스페이스 2,
     prismone/***REMOVED*** 접속·시드 확인) · scram 전환 후 오답 실패(exit 2)/정답 성공
  2. **Docker**(colima) `postgres:16` 컨테이너, `POSTGRES_HOST_AUTH_METHOD=trust` —
     무비밀번호 감지 → 초기 비밀번호 설정 → 전체 설치 성공(동일 지표 + SCRAM 확인).
     테이블스페이스 디렉터리는 컨테이너 안에 미리 생성(`/data/pg_ts/*`,
     compose 의 db-init 서비스와 같은 방식)
  **주의: 40_schema 부터는 신규 설치 전용**(CREATE TABLE 에 IF NOT EXISTS 없음 —
  psql 로 돌려도 동일). 재실행 업그레이드는 patch 명령의 몫

**남은 것**:

- `iapdb patch apply|status|--dry-run|--baseline` — `patches/` 델타 + `PRISMONE.schema_version`
  (`patches/apply.sh` 의 시맨틱을 그대로 포팅)
- 배포 패키징(단일 실행 파일) 및 Windows 검증

## 개발 메모 (다른 환경에서 이어받을 때)

- **빌드**: `cd tools && dotnet build PrismOne.Tools.sln` (.NET 10 SDK 필요)
- **테스트**: `dotnet test tests/PrismOne.Db.Core.Tests` (현재 130개)
- **실접속 검증 현황** (2026-08-03, 개발 DB `<dev-host>` 접속 가능한 환경에서 확인):
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
  배포 확인은 macOS `sh packaging/macos/make-app.sh` → `dist/IAP Database Manager.app`,
  Windows `powershell -ExecutionPolicy Bypass -File packaging/windows/make-app.ps1`
  → `dist/IAP Database Manager/IAP Database Manager.exe` (self-contained 단일 exe, 약 48MB)
- **Windows 주의**: PowerShell 5.1 은 BOM 없는 UTF-8 .ps1 을 CP949 로 읽어 한글 주석이
  깨지면서 파싱이 어긋난다. `packaging/windows/*.ps1` 은 **UTF-8 BOM 으로 저장**할 것
- **자가 검증 (중요)**: 화면 회귀는 스크린샷 모드로 확인한다.
  ```bash
  IAPDM_SHOT_DIR=/tmp/shots \
  IAPDM_SHOT_CONN="<dev-host>/prismone|prismone|***REMOVED***" \
  dotnet run --project src/PrismOne.Studio --no-build
  ```
  → `live_after_login.png` / `live_describe.png` / `live_completion.png` /
  `live_query.png` / `live_favorite.png` / `live_scrolled.png` / `live_explain.png`
  생성 후 종료. `IAPDM_SHOT_RAE=1` 을 함께 주면 Run and Edit 실접속 검증까지 수행한다
  (임시 테이블을 만들어 편집·Submit 후 drop — 위 실접속 검증 4번 참조). 접속 없이 샘플 데이터만 볼 땐 `IAPDM_SHOT_CONN` 을 빼면
  `shot_main.png` / `shot_login.png` / `shot_favorites.png` 가 나온다.
  아이콘 재생성은 `IAPDM_RENDER_ICON=<경로>`.
- **개발용 DB**: `<dev-host>/prismone` (계정 `prismone`/`***REMOVED***`). 공유 서버이므로
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
| `STATUS.md` | 이 문서 — 진행 상황과 다음 작업 |
