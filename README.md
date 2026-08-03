# Aurum

**Au(금, 79)** — Oracle 시절 쓰던 Benthic **Golden** 의 후계자. PostgreSQL용 데스크톱
쿼리 툴로, Golden 의 UI·동작(공유 세션·점진 fetch·EditMode·Favorites…)을 재현하고
PG 고유 기능(EXPLAIN 트리·COPY export·RAISE NOTICE 수신)을 얹었다. .NET 10 + Avalonia 12
(Windows·macOS·Linux).

원래 [iap-database](https://github.com/inftai111/iap-database) repo 의 `tools/` 로 시작했고,
2026-08 에 별도 repo 로 분리했다 (히스토리 보존). **DB 초기 설치·패치 CLI(`iapdb`)는
iap-database 쪽에 남아 있다** — 설계 원칙: 초기화·패치는 배포 키트(iapdb + sql/ + patches/)의
몫이고, Aurum 은 스키마 버전에 종속되지 않는 조회·관리 도구다.

- **사용법: [docs/USER_GUIDE.md](docs/USER_GUIDE.md)** · **진행 상황: [docs/STATUS.md](docs/STATUS.md)**
- Golden 동작 명세: [docs/GOLDEN_BEHAVIOR.md](docs/GOLDEN_BEHAVIOR.md) · 초기 파리티 계획: [docs/STUDIO_PLAN.md](docs/STUDIO_PLAN.md)
- PostgreSQL·pgAdmin 기능 분석: [docs/PG_FEATURES.md](docs/PG_FEATURES.md)

주요 키: **Ctrl+L** 로그온 · **F9** 문장 실행 · **F5/Shift+Enter** 스크립트 실행(커서부터 끝까지) ·
**Ctrl+End** 전체 fetch · **F8** Object Browser · **F11** Run and Edit · **Ctrl+T/W** 탭 열기/닫기.
저장된 접속의 비밀번호는 AES-256-GCM 으로 암호화되어 `~/.prismone-studio/` 에 보관됩니다.

## 구조

```
Aurum.sln
src/
  PrismOne.Db.Core/    # 쿼리 세션 · 그리드 편집 · export(xlsx 포함) · 히스토리/즐겨찾기 · 스키마 카탈로그
  PrismOne.Studio/     # GUI (제품명 Aurum) — 네임스페이스는 역사적 이유로 PrismOne.Studio 유지
tests/
  PrismOne.Db.Core.Tests/
packaging/             # macOS .app · Windows 단일 exe
```

## 빌드 · 배포

```bash
# .NET 10 SDK 필요
dotnet build Aurum.sln
dotnet test tests/PrismOne.Db.Core.Tests
dotnet run --project src/PrismOne.Studio
```

배포 패키지:

```bash
sh packaging/macos/make-app.sh          # → dist/Aurum.app
```

```powershell
powershell -ExecutionPolicy Bypass -File packaging/windows/make-app.ps1
# → dist/Aurum/Aurum.exe (self-contained 단일 exe)
```

> `packaging/windows/*.ps1` 은 **UTF-8 BOM** 으로 저장한다. PowerShell 5.1 은 BOM 없는
> UTF-8 을 CP949 로 읽어 한글 주석이 깨지면서 스크립트 파싱이 어긋난다.

## 자가 검증 (스크린샷 하니스)

```bash
IAPDM_SHOT_DIR=/tmp/shots dotnet run --project src/PrismOne.Studio   # 샘플 데이터 UI 캡처
# IAPDM_SHOT_CONN="host[:port]/db|user|pass" 를 주면 실접속 재현 (+IAPDM_SHOT_RAE=1 은 편집 검증)
# 앱 아이콘 재생성: IAPDM_RENDER_ICON=<png경로>
```
