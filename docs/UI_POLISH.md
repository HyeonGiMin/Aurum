# UI 다듬기 — 실제품 수준까지의 로드맵

작성: 2026-08-04. 방향: **기능은 갖춰졌다(DATAGRIP_GAP 1~6 완료). 이제 "쓸 수 있다"에서
"팔 수 있다"로.** 우선순위는 ①매일 보이는 것 → ②첫인상 → ③접근성·관례 순이고,
안정성 원칙(핵심 그리드 경로를 건드리는 변경은 마지막에, 스크린샷 회귀 필수)을 지킨다.

## 완료 (2026-08-04, 1차)

- **Esc 로 보조 창 닫기** — 모든 다이얼로그·보조 창(`IsCancel` / KeyDown).
  로그온·옵션·바인드 변수·History·Import·Schema Diff·Pin 창 전부.
- **Enter 기본 동작** — History 에서 Enter = Insert to Editor (`IsDefault`).
- **보조 창 최소 크기** — 줄여도 레이아웃이 무너지지 않게 MinWidth/MinHeight.

## P1 — 매일 체감 (다음 사이클)

1. ~~**다크 모드**~~ ✅ 구현됨 (2026-08-04)
   - `GoldenTheme.axaml` 을 ThemeDictionaries(Light/Dark)로 재구성, 테마 종속
     브러시 28종 전부 DynamicResource. View > Dark Mode 토글 + Options 의
     Light/Dark/System 콤보 (`AppOptions.Theme`, 기본 Light — Golden 정체성).
   - 코드 색: 에디터 구문 배색(`SqlHighlighting.For(dark)`, 전환 시 전 탭 재적용),
     Explain 막대·Schema Diff 는 diff 팔레트 공유(`ThemeBrushes.Get`).
   - 남긴 것(의도): ERD 캔버스·인쇄물은 라이트 고정("종이"), 자동완성 배지·
     상태 pill 은 자체 배경이 있는 칩이라 그대로. 코드가 만든 트리는 전환 시
     재실행 때 새 색을 입는다.
   - 스크린샷 하니스 다크 변형: `IAPDM_SHOT_THEME=dark`.
2. ~~**긴 작업 로딩 피드백**~~ ✅ 구현됨 (2026-08-04) — 로그온(버튼 "Connecting…" +
   진행 바), ERD 스키마 목록/카탈로그 로드(상태바 진행 바), Schema Diff
   Compare/Save Snapshot(진행 바), Import(행수 기반 **확정 진행 바**).
   남긴 것: 탭의 쿼리 실행은 Golden 방식(상태바 경과 시간 + Cancel)이 이미 있다.
3. ~~**창 크기·위치 기억**~~ ✅ 구현됨 (2026-08-04) — `WindowPlacementTracker`,
   메인·ERD·Diff 창. options.json 의 WindowPlacements, 저장 시 파일을 새로 읽어
   placement 만 갱신(다른 옵션 필드 보존), 화면 밖이면 무시, Maximized 는
   normal 크기와 함께 기억. 스크린샷 하니스에서는 비활성.

## P2 — 첫인상·완성도

4. **토스트 알림** — xlsx 저장 완료·Import 완료·커밋 완료처럼 "한 줄 알림"이
   필요한 곳. 상태바는 놓치기 쉽다. Avalonia `WindowNotificationManager` 사용.
5. **빈 상태(empty state) 정리** — No records·미접속 탭·History 0건·ERD 미선택에
   아이콘 + 다음 행동 안내 한 줄 (예: "Ctrl+L 로 접속하세요").
6. **타이포 스케일 통일** — FontSize 11/11.5/12/12.5/13 혼재. 3단(본문 13 ·
   보조 12 · 캡션 11)으로 정리하고 Styles 에 클래스로.
7. **툴바 아이콘 정리** — 스타일 혼재 여부 점검, hover/pressed/disabled 상태 통일,
   구분선 간격.

## P3 — 접근성·플랫폼 관례

8. **키보드 접근성** — 탭 순서 점검, 포커스 링 가시성, 그리드 셀 복사(Ctrl+C)
   일관성.
9. **스크린리더** — 주요 컨트롤에 `AutomationProperties.Name`.
10. **플랫폼 관례** — macOS: 단축키 ⌘ 표기 통일·About 메뉴 위치.
    Windows: 작업표시줄 진행 표시(import 등), 점프리스트는 후순위.

## 원칙

- 색·간격을 바꾸는 모든 커밋은 **오프라인 스크린샷 12종 diff 확인** 후 머지.
- 그리드(결과 영역) 내부 구조 변경은 이 로드맵 범위 밖 — 성능·안정성 트랙에서.
- 새 의존성(아이콘 팩 등)은 단일 exe 배포 크기를 확인하고 들인다.
