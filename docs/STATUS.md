# 진행 상황 · 다음 작업 (인수인계용)

마지막 갱신: 2026-08-03 · 브랜치 `feat/tools-golden-parity`

## 세 가지 목표 대비 현황

| # | 목표 | 상태 |
|---|---|---|
| 1 | **Golden 대체 쿼리 툴** (PostgreSQL) | **거의 완료** — 아래 §1 |
| 2 | DB 초기 설치 (IAP 제품용) | **미착수** — CLI 스캐폴드만 존재 |
| 3 | 설치된 DB 패치/업그레이드 | **미착수** |

## 1. Golden 파리티 현황

구현 완료 (근거: `GOLDEN_BEHAVIOR.md` 의 바이너리 추출 명세):

- 시작 화면(미접속 상태에서도 Query1 탭) · Ctrl+L 로그온(`host[:port]/db`, Login List)
- F9 문장 / F5·Shift+Enter 스크립트(커서부터 끝까지) / Explain / Explain Analyze / Cancel
- **점진 fetch**(기본 100행, 스크롤 시 이어서, Ctrl+End 전체), Fetch 상한 옵션
- **공유 세션 모델** + New Private Tab(Ctrl+Shift+T)
- Commit / Rollback / AutoCommit(기본 수동), 상태바 `[TX]`
- 자동완성(Ctrl+Space·⌥Space·`.`·FROM 뒤 자동, 별칭→컬럼, 종류 배지·현재 스키마 우선)
- 히스토리 ◀▶(Ctrl+↑↓, 디스크 보존), Find(Ctrl+F), 바인드 변수 `:var`
- Object Browser(F8) + describe, **Ctrl+D describe**, 이름 붙여넣기
- 그리드: 행번호, Transpose(Ctrl+Shift+X), Size Columns to Fit, Filter Like Cell,
  Cell Detail(더블클릭, jsonb pretty-print)
- 내보내기: CSV(COPY 전체 행) / TSV / INSERT 문
- 워크스페이스 저장·복원(`.iapws`), 옵션 다이얼로그, Session Monitor
- PG 강화: Messages(RAISE NOTICE), EXPLAIN 트리, COPY export, pg_stat_activity

**남은 Golden 기능** (우선순위순):

1. **Favorites** — 즐겨찾기 쿼리 메뉴 (작음, 반나절)
2. **Run and Edit** — 결과 그리드에서 직접 INSERT/UPDATE/DELETE (대형, 별도 설계 필요)
3. Print / 인쇄 미리보기 (툴바에 자리만 있음)
4. SQLBuilder(비주얼 쿼리 빌더) — 우선순위 낮음

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
- **테스트**: `dotnet test tests/PrismOne.Db.Core.Tests` (현재 59개)
- **실행**: 개발 중엔 `dotnet run --project src/PrismOne.Studio`,
  배포/독 확인은 `sh packaging/macos/make-app.sh` 후 `dist/IAP Database Manager.app`
- **자가 검증 (중요)**: 화면 회귀는 스크린샷 모드로 확인한다.
  ```bash
  IAPDM_SHOT_DIR=/tmp/shots \
  IAPDM_SHOT_CONN="<dev-host>/prismone|prismone|***REMOVED***" \
  dotnet run --project src/PrismOne.Studio --no-build
  ```
  → `live_after_login.png` / `live_describe.png` / `live_completion.png` /
  `live_query.png` / `live_scrolled.png` / `live_explain.png` 생성 후 종료.
  접속 없이 샘플 데이터만 볼 땐 `IAPDM_SHOT_CONN` 을 빼면 된다.
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
