# PrismOne Studio — Golden 파리티 계획

목표: **Benthic Software Golden**(Oracle용 경량 쿼리 툴)의 UI·동작을 PostgreSQL 위에 그대로 재현한다.
"동일"의 기준은 Golden 6/7의 일상 사용 흐름이다 — 즉시 뜨는 로그인, 문장 단위 실행, 점진 fetch 그리드,
쿼리 히스토리, 커밋 제어. Oracle 전용 개념(TNS 등)만 PG 등가물로 치환한다.

## UI 구조 (Golden 대응)

```
┌──────────────────────────────────────────────────────────────┐
│ 메뉴바  File · Edit · Session · Grid · Window · Help          │
│ 툴바   [접속▾] [새 쿼리탭] | ▶F9 ▶F5(script) ■ | ◀히스토리▶  │
│        | Commit Rollback [x]AutoCommit | Export▾              │
├───────────────┬──────────────────────────────────────────────┤
│ Object        │  쿼리 탭1 │ 쿼리 탭2 │ +                      │
│ Browser       │ ┌──────────────────────────────────────────┐ │
│  (Tables/     │ │ SQL 에디터 (하이라이팅·줄번호)             │ │
│   Views/      │ ├──────────────────────────────────────────┤ │
│   Sequences/  │ │ 결과 그리드 (점진 fetch)                   │ │
│   Functions,  │ ├──────────────────────────────────────────┤ │
│   필터박스)    │ │ 탭 상태줄: Fetched 500 rows (more) · 12ms │ │
│               │ └──────────────────────────────────────────┘ │
├───────────────┴──────────────────────────────────────────────┤
│ 상태바  세션: postgres@host:5432/prismone · autocommit off     │
└──────────────────────────────────────────────────────────────┘
```

- Golden은 MDI 자식창, 우리는 **탭** (Golden 7도 탭 스타일). 쿼리 탭마다 **독립 세션(접속)** — Golden의 창=세션 모델과 동일.
- 메인 툴바가 활성 탭에 작용한다 (Golden과 동일).

## 동작 파리티 표

| # | 기능 | Golden 동작 | 구현 방침 |
|---|---|---|---|
| 1 | 로그인 | 저장된 접속 드롭다운, 즉시 연결 | `~/.prismone-studio/connections.json` 저장 목록 + 최근 접속 기본 선택. 비밀번호 저장은 옵트인 |
| 2 | 문장 실행 | **F9 = 커서 위치 문장 하나** 실행 → 그리드 | 세미콜론 분리(문자열·주석·**달러쿼팅** 인식) 후 커서 문장 실행. Ctrl/Cmd+Enter 동일 |
| 3 | 스크립트 실행 | **F5 = 전체(또는 선택) 스크립트** → 텍스트 출력창 | 문장 순차 실행, SQL*Plus 식 텍스트 출력 pane (행수·오류·소요) |
| 4 | 점진 fetch | 첫 배치 즉시 표시, 스크롤 시 이어 fetch, `Ctrl+End` 전체 fetch | reader 를 열어둔 채 배치(기본 500행) fetch. 상태줄 "Fetched N rows (more)". 탭마다 전용 세션이라 reader 유지 가능 |
| 5 | 취소 | 실행/fetch 중 Cancel | PG cancel request 전송, 세션 유지 |
| 6 | 히스토리 | 툴바 ◀ ▶ 로 과거 문장 순환 (창별) | 탭별 링 히스토리 + 전체 히스토리 창(검색), 디스크 보존 |
| 7 | 바인드 변수 | `:var` 만나면 값 입력 다이얼로그, 이전 값 기억 | 동일 (타입: text/number/date/null). PG 캐스트는 텍스트+`::` 권장 안내 |
| 8 | 커밋 제어 | **auto-commit off 기본**, Commit/Rollback 버튼, 미커밋 표시 | 세션별 트랜잭션 유지로 동일 구현. 기본값 off (Golden 습관), 옵션으로 변경 |
| 9 | Describe | 테이블 지정 → 컬럼/타입/NULL/기본값, 인덱스·제약 탭 | Ctrl+D (커서 단어) + 브라우저 컨텍스트 메뉴. 소스: pg_catalog |
| 10 | Object Browser | Tables/Views/Procedures 트리, 필터, 더블클릭 Describe, 드래그로 이름 삽입 | 동일 + Sequences/Functions. 더블클릭=Describe, 컨텍스트 메뉴 "Select Data" |
| 11 | Export | Save Grid As: CSV/Tab/XLS/HTML/INSERT문, 헤더 포함 복사 | CSV/TSV/XLSX/HTML/INSERT. 클립보드 헤더 포함 복사(이미 있음) |
| 12 | 옵션 | fetch 크기, NULL 표시 문자열, 날짜 포맷, 폰트 | 옵션 다이얼로그 + json 저장 |
| 13 | 에디터 | 키워드 하이라이팅, 줄번호 | AvaloniaEdit + SQL TextMate 문법. 자동완성은 후순위(테이블명 정도만) |

## PG라서 Golden과 다르게 두는 것

- 접속: TNS name 대신 host/port/database — 저장된 접속 목록이 그 역할을 대신한다.
- DDL도 트랜잭션에 들어간다(PG 특성) — auto-commit off 에서 DDL 후 Rollback 이 실제로 되돌린다 (Oracle과 다름, 오히려 장점).
- `:var` 바인드는 PG 프로토콜 파라미터로 전달 (`$1` 변환).

## 단계

| Phase | 내용 | 파리티 항목 |
|---|---|---|
| **P1** | 탭=세션 아키텍처, 점진 fetch 그리드, 문장 분리기(달러쿼팅), F9 커서 문장 실행, 취소 | 2·4·5 |
| **P2** | AvaloniaEdit(하이라이팅·줄번호), 스크립트 모드(F5 텍스트 출력), 히스토리, 바인드 변수 | 3·6·7·13 |
| **P3** | 접속 저장/로그인 UX, Object Browser 확장, Describe | 1·9·10 |
| **P4** | 커밋 제어(트랜잭션 세션), Export 확장, 옵션 | 8·11·12 |
| **P5** | 마감: 단축키 전수 점검, 아이콘/툴바, 10만행 성능, 메뉴바 구성 | 전체 |

문장 분리기·fetch 엔진은 Core 에 두고 단위 테스트를 붙인다 (추후 CLI 스크립트 실행에도 재사용).
