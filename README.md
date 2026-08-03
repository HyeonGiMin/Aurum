# tools — Aurum

PRISMONE(PostgreSQL) 데이터베이스를 다루는 데스크톱/CLI 도구 모음입니다. .NET 10 (C#) 단일 솔루션.
데스크톱 앱(제품명 **Aurum**)은 Oracle 시절 쓰던 Benthic Golden 의 UI·동작을 재현합니다.

- **사용법: [docs/USER_GUIDE.md](docs/USER_GUIDE.md)** · **진행 상황·다음 작업: [docs/STATUS.md](docs/STATUS.md)**
- Golden 동작 명세: [docs/GOLDEN_BEHAVIOR.md](docs/GOLDEN_BEHAVIOR.md) · 파리티 계획: [docs/STUDIO_PLAN.md](docs/STUDIO_PLAN.md)
- PostgreSQL·pgAdmin 기능 분석: [docs/PG_FEATURES.md](docs/PG_FEATURES.md)

주요 키: **Ctrl+L** 로그온 · **F9** 문장 실행 · **F5/Shift+Enter** 스크립트 실행(커서부터 끝까지) ·
**Ctrl+End** 전체 fetch · **F8** Object Browser · **Ctrl+T/W** 탭 열기/닫기 ·
**Ctrl+Shift+F** 즐겨찾기 추가.
저장된 접속의 비밀번호는 AES-256-GCM 으로 암호화되어 `~/.prismone-studio/` 에 보관됩니다.

## 목표

1. **Golden 대체** — Oracle에서 쓰던 Golden처럼 가벼운 PostgreSQL 쿼리 툴 (SQL 편집기 + 결과 그리드 + export)
2. **초기 설치** — DB 설치 후 IAP 제품용 초기 데이터베이스 구성 (`manifest.txt` + `sql/` 실행, `run_all.sh`의 크로스플랫폼 대체)
3. **패치/업그레이드** — 초기화된 DB에 스키마 델타 적용 (`patches/` + `PRISMONE.schema_version` 체계 계승)

2·3번은 GUI 없이 **CLI 단독으로 완전히 동작**해야 한다 (서버·CI·에어갭 현장).

## 구조

```
PrismOne.Tools.sln
src/
  PrismOne.Db.Core/    # 공용 로직: 접속 관리 · manifest 러너 · 패치 엔진 · psql 호환 실행기
  PrismOne.Db.Cli/     # CLI: install / patch apply|status|--dry-run|--baseline / verify
  PrismOne.Studio/     # GUI (Avalonia): 쿼리 툴 + 설치/패치 프론트엔드 — Core 재사용
```

- **Core** — sql/·patches/ 의 SQL을 Npgsql로 실행한다. 기존 SQL이 psql 메타커맨드(`\set`, `\gexec`, `\if :{?var}`, `:'var'` 치환)를 쓰므로, 실제 사용되는 문법만 처리하는 mini-psql 프리프로세서를 포함한다 (SQL 파일 무수정, bash 경로와 공존).
- **Cli** — 제품 설치·패치의 1차 인터페이스. `patches/apply.sh` 의 apply / dry-run / baseline 시맨틱을 그대로 포팅.
- **Studio** — Windows·macOS·Linux 지원 (Avalonia 12). WPF가 아닌 이유: 크로스플랫폼 + macOS 개발 환경.

## 빌드 · 배포

```bash
# .NET 10 SDK 필요
dotnet build PrismOne.Tools.sln
dotnet test tests/PrismOne.Db.Core.Tests
```

배포 패키지:

```bash
sh packaging/macos/make-app.sh          # → dist/Aurum.app
```

```powershell
powershell -ExecutionPolicy Bypass -File packaging/windows/make-app.ps1
# → dist/Aurum/Aurum.exe (self-contained 단일 exe)
#   + dist/IAPDatabaseManager-win-x64-<버전>.zip
```

> `packaging/windows/*.ps1` 은 **UTF-8 BOM** 으로 저장한다. PowerShell 5.1 은 BOM 없는
> UTF-8 을 CP949 로 읽어 한글 주석이 깨지면서 스크립트 파싱이 어긋난다.
