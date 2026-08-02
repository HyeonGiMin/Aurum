# PostgreSQL 고유 기능 · pgAdmin 기능 분석 → IAP Database Manager 적용 계획

Golden 파리티(GOLDEN_BEHAVIOR.md)가 UI/UX 의 기준이라면, 이 문서는 **PostgreSQL 이라서
가능한/필요한 것**과 **pgAdmin(사실상 PG 표준 GUI)이 제공하는 기능** 중 우리 툴에
필요한 것을 골라 우선순위를 매긴다.

## 1. 이미 적용된 PG 특성

| 항목 | 내용 |
|---|---|
| 트랜잭션 DDL | PG 는 DDL 도 트랜잭션에 들어간다 → Rollback 이 CREATE/ALTER 까지 되돌림 (Oracle 과 다른 장점, 수동 커밋 모드에서 그대로 활용) |
| 읽기 문장 autocommit | 수동 커밋 모드여도 SELECT/EXPLAIN/SHOW 는 트랜잭션을 열지 않음 — idle-in-transaction 세션이 스냅샷을 붙들어 **VACUUM 방해**하는 PG 특유 문제 회피 |
| Read Only 세션 | 로그인 다이얼로그 체크박스 → `default_transaction_read_only=on` (운영 조회용 안전장치) |
| EXPLAIN | ⚡ᴱ 버튼 (Oracle explain plan 의 PG 등가) |
| 점진 fetch | Npgsql 행 단위 스트리밍 위에 구현 (커서 없이 reader 유지) |
| 취소 | PG cancel request (pg_cancel_backend 와 같은 프로토콜) |

## 2. pgAdmin 기능 인벤토리 → 채택 판단

| pgAdmin 기능 | 판단 | 근거 / 반영 위치 |
|---|---|---|
| Query Tool (편집기+그리드) | ✅ 이미 있음 | Golden 스타일이 우리가 더 가벼움 |
| **EXPLAIN ANALYZE (그래픽 플랜)** | 🔜 **P4** | 텍스트 플랜은 지금도 됨. 트리 시각화는 성능 튜닝에 실사용 가치 큼. 1차는 들여쓰기 트리 뷰로 |
| **Messages 탭 (RAISE NOTICE/WARNING)** | 🔜 **P4 (높음)** | PG 함수/프로시저 디버깅 필수. Npgsql `Notice` 이벤트 → Golden 의 dbms_output 창 위치에 출력 pane |
| **pg_stat_activity (세션 모니터) + 쿼리 킬** | 🔜 P5 | 공유 staging 에서 누가 물고 있는지 확인 + `pg_cancel/terminate_backend`. 관리 기능 1순위 |
| 테이블 DDL 보기 (CREATE 문 재구성) | 🔜 P3 | 브라우저 컨텍스트 메뉴 "Show DDL" — describe 확장 |
| **COPY 기반 대량 export/import** | 🔜 P4 | CSV export 를 `COPY TO STDOUT` 으로 바꾸면 대량에서 수십 배 빠름. import 는 CLI(설치/패치)와 묶어서 |
| jsonb 뷰어 (셀 상세 + pretty print) | 🔜 P4 | dcmdataset 같은 JSONB 열람 — cell detail 창에 JSON 트리/포맷 |
| LISTEN/NOTIFY 모니터 | ⏸ 보류 | pgmq/알림 디버깅용. 수요 생기면 |
| 백업/리스토어 (pg_dump 래퍼) | ❌ 제외 | 설치/패치는 우리 CLI 영역. dump 는 표준 도구 사용 |
| 서버 설정 편집(postgresql.conf) | ❌ 제외 | 범위 밖 |
| ERD | ❌ 제외 | 리포에 이미 ERD 뷰어 파이프라인 존재 (scripts/gen_erd.py) |
| 사용자/권한 GUI | ⏸ 보류 | prismone 롤 체계는 sql/80_grants.sql 이 원천 |
| Autovacuum/통계 대시보드 | ⏸ 보류 | pg_stat_activity 뷰가 먼저 |

## 3. PG 고유 기능 중 추가 채택 후보

| 기능 | 가치 | 계획 |
|---|---|---|
| `search_path` 표시/설정 | Object Browser 의 Use Schema 와 연동, 세션 pill 에 표시 | P3 |
| 파티션 인지 describe | STUDY 등 파티션 테이블의 자식/키 표시 (46_partitions.sql 체계) | P3 |
| `statement_timeout` 옵션 | 폭주 쿼리 예방 (옵션 다이얼로그) | P4 |
| 배열/복합 타입 표시 | ValueFormatter 가 배열은 처리 — 복합타입/범위타입 보강 | 수요 시 |
| `pg_size_pretty` 테이블 크기 | 브라우저 목록에 크기 컬럼(옵션) | P5 |

## 4. 반영 현황

✅ 구현 완료: Messages(NOTICE) pane · EXPLAIN (ANALYZE) 플랜 트리 · COPY 기반 export ·
jsonb Cell Detail · pg_stat_activity Session Monitor · statement_timeout(옵션) ·
읽기 문장 autocommit 절충 · Read Only 세션

🔜 남은 후보: 테이블 DDL 보기(Show DDL) · 파티션 인지 describe · search_path 표시 ·
테이블 크기(pg_size_pretty) · COPY 기반 import(CLI 와 함께)

> 최신 진행 상황은 [STATUS.md](STATUS.md).
