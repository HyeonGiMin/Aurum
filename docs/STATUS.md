# 진행 상황 · 다음 작업 (인수인계용)

마지막 갱신: 2026-08-03 · 브랜치 `feat/tools-favorites-windows`

## 세 가지 목표 대비 현황

| # | 목표 | 상태 |
|---|---|---|
| 1 | **Golden 대체 쿼리 툴** (PostgreSQL) | **기능 완료** — 아래 §1 (실접속 검증만 미완) |
| 2 | DB 초기 설치 (IAP 제품용) | **미착수** — CLI 스캐폴드만 존재 |
| 3 | 설치된 DB 패치/업그레이드 | **미착수** |

## 1. Golden 파리티 현황

구현 완료 (근거: `GOLDEN_BEHAVIOR.md` 의 바이너리 추출 명세):

- 시작 시 메인 창(Query1 탭) 위로 **로그온 창 자동 표시**(Golden 동작) · Ctrl+L 재로그온
  (`host[:port]/db`, Login List). 취소하면 미접속 상태로 남는다
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
- 내보내기: CSV(COPY 전체 행) / TSV / INSERT 문
- 워크스페이스 저장·복원(`.iapws`), 옵션 다이얼로그, Session Monitor
- **Favorites**(Ctrl+Shift+F 추가 · 메뉴에서 바로 실행 · 관리 창 필터/수정/삭제 ·
  SELECT 이외 차단 옵션) — `~/.prismone-studio/favorites.json`
- PG 강화: Messages(RAISE NOTICE), EXPLAIN 트리, COPY export, pg_stat_activity
- 배포: macOS `.app`(`packaging/macos/make-app.sh`) · **Windows 단일 exe**
  (`packaging/windows/make-app.ps1`, 아이콘은 `make-icon.ps1` → `Assets/icon.ico`)

- **Run and Edit**(F11) — 단일 테이블 SELECT 를 ctid 붙여 재실행 → 셀 수정·행 추가·삭제 →
  Submit(Ctrl+Shift+S)에서 한 트랜잭션으로 반영, 영향 행 ≠ 1 이면 전체 롤백.
  **실접속 검증 미완** (아래 참조)

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

## 2·3. CLI (다음 큰 작업)

`src/PrismOne.Db.Cli` 는 아직 빈 스캐폴드다. 설계 방향은 `../README.md` 와
`STUDIO_PLAN.md` 에 적힌 대로:

- `iapdb install` — 리포 루트의 `manifest.txt` 순서대로 `sql/*.sql` 실행
  (psql 없이 Npgsql 로. **주의: SQL 파일이 psql 메타커맨드를 쓴다** — `\set`, `\gexec`,
  `\if :{?var}`, `:'var'` 치환. mini-psql 프리프로세서를 Core 에 구현하는 A안 권장)
- `iapdb patch apply|status|--dry-run|--baseline` — `patches/` 델타 + `PRISMONE.schema_version`
  (`patches/apply.sh` 의 시맨틱을 그대로 포팅)
- macOS bash 3.2 에서 `run_all.sh` 가 안 도는 것(`mapfile` 없음)도 CLI 필요성의 근거

## 개발 메모 (다른 환경에서 이어받을 때)

- **빌드**: `cd tools && dotnet build PrismOne.Tools.sln` (.NET 10 SDK 필요)
- **테스트**: `dotnet test tests/PrismOne.Db.Core.Tests` (현재 130개)
- **실접속 검증 현황** (2026-08-03, 개발 DB `<dev-host>` 접속 가능한 환경에서 확인):
  1. ✅ **Tx Isolation** — `ApplyIsolationAsync` 후 `show transaction_isolation` 으로 확인.
     Serializable / Repeatable Read / Read Committed 모두 세션에 반영되고,
     Database Default 는 서버 기본값(read committed)으로 되돌아온다
  2. ✅ **즐겨찾기 실행 경로** — `live_favorite.png` 로 확인 (메뉴에서 실행 → 결과 표시)
  3. ✅ 로그온·describe·자동완성·쿼리·스크롤·EXPLAIN — 스크린샷 모드 7종 전부 정상
  4. ⏳ **Run and Edit 전체 경로(셀 편집 → Submit → 커밋)는 여전히 미검증**.
     공유 staging DB 에 데이터를 쓰게 되므로 확인하지 않았다.
     **검증용 임시 테이블을 따로 만들어 그 위에서** 확인할 것 (DataGrid 셀의
     `Cells[i]` TwoWay 바인딩이 실제로 값을 되쓰는지가 핵심)
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
  생성 후 종료. 접속 없이 샘플 데이터만 볼 땐 `IAPDM_SHOT_CONN` 을 빼면
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
