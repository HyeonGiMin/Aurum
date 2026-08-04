# 멀티 DB 지원 계획 (PostgreSQL / Oracle / SQLite / MongoDB)

작성: 2026-08-04 · 목표: DataGrip 처럼 **접속 대상 DB 를 골라 쓰는** 도구로 넓힌다.

> STATUS.md §1.5 가 "착수 전에 별도 계획 문서로 시작한다"고 한 그 문서다.
> **아직 착수 전이다.** 아래는 현황 측정과 단계 계획이다.

## 1. 현재 결합도 (2026-08-04 측정)

`grep -c "Npgsql\|PostgresException"` 기준. 숫자는 참조 건수다.

| 파일 | 건수 | 성격 |
|---|---|---|
| `Core/QuerySession.cs` | 14 | 접속·트랜잭션·격리·NOTICE — **가장 깊다** |
| `Core/ErdCatalog.cs` | 8 | 이미 `IErdCatalog` 뒤에 있음 ✅ |
| `Core/SchemaCatalog.cs` | 5 | 테이블 목록·describe |
| `Core/ConnectionProfile.cs` | 4 | 접속 문자열 생성 |
| `Core/SchemaVersion.cs` | 4 | PRISMONE 전용 (멀티 DB 무관) |
| `Core/QueryExecutor.cs` | 3 | 결과 읽기 |
| `Core/SessionMonitor.cs` | 3 | pg_stat_activity |
| `Core/BindVariables.cs` | 2 | 파라미터 바인딩 |
| `Studio/QueryTabView.axaml.cs` | 3 | PostgresException 처리 |
| `Studio/MainWindow.axaml.cs` | 2 | 동일 |

PG 전용 SQL/기능이 박힌 곳: `CopyExporter`(COPY TO STDOUT), `GridEditor`(ctid),
`QuerySession`(RAISE NOTICE·격리 구문), `SchemaCatalog`·`ErdCatalog`(pg_catalog),
`SessionMonitor`(pg_stat_activity), `QueryTabView`(EXPLAIN 트리·Messages).

## 2. 드라이버

| DB | 패키지 | 비고 |
|---|---|---|
| PostgreSQL | `Npgsql` (사용 중) | |
| Oracle | `Oracle.ManagedDataAccess.Core` | 100% 매니지드 — Oracle Client 설치 불필요 |
| SQLite | `Microsoft.Data.Sqlite` | 파일 기반 — **테스트에 쓸 수 있는 유일한 DB** |
| MongoDB | `MongoDB.Driver` | ADO.NET 이 아님. 별도 경로 필요 |

**self-contained exe 크기 주의** — 드라이버 4종을 다 넣으면 현재 약 48MB 에서 늘어난다.
필요해지면 선택 배포를 검토하되, 1차는 그냥 참조한다(YAGNI).

## 3. 기능별 대응 가능성

✅ 그대로 / ⚠️ 다른 구문으로 / ❌ 해당 DB 에 없음(비활성화)

| 기능 | PG | Oracle | SQLite | Mongo |
|---|---|---|---|---|
| 문장 실행·그리드 | ✅ | ✅ | ✅ | ⚠️ 파이프라인/쿼리 문서 |
| 문장 분리(`;`) | ✅ | ⚠️ PL/SQL 블록 `/` | ✅ | ❌ SQL 아님 |
| 바인드 변수 | `:var` | ✅ `:var` 네이티브 | ⚠️ `@p`/`$p`/`:p` | ❌ |
| 트랜잭션 Commit/Rollback | ✅ | ✅ | ⚠️ 단순 | ⚠️ replica set 필요 |
| 격리 수준 | ✅ 4단계 | ⚠️ RC/Serializable 만 | ❌ | ❌ |
| 그리드 편집 (행 특정) | `ctid` | ⚠️ `ROWID` | ⚠️ `rowid` | ⚠️ `_id` |
| 스키마 브라우저·describe | ✅ | ⚠️ `all_tables`/`all_tab_columns` | ⚠️ `sqlite_master`/`PRAGMA` | ⚠️ 컬렉션·샘플 기반 추론 |
| **ERD** | ✅ | ⚠️ `all_constraints` | ⚠️ `PRAGMA foreign_key_list` | ❌ FK 없음(참조 추론 필요) |
| EXPLAIN | ✅ 트리 | ⚠️ `DBMS_XPLAN` | ⚠️ `EXPLAIN QUERY PLAN` | ⚠️ `explain()` |
| 서버 메시지 | RAISE NOTICE | ⚠️ `DBMS_OUTPUT` | ❌ | ❌ |
| 세션 모니터 | pg_stat_activity | ⚠️ `V$SESSION` | ❌ | ⚠️ `currentOp` |
| 대량 export | `COPY` | ❌ 행 단위 fallback | ❌ 행 단위 | ❌ 커서 |

→ **기능 플래그가 필요하다.** provider 가 `Capabilities` 를 노출하고 UI 는 없는 기능을
비활성화한다. 지금처럼 "버튼은 있는데 안 되는" 상태를 만들면 안 된다.

## 4. 단계

### 0단계 — 경계 만들기 (선행, 동작 변화 없음)

`Core/Providers/` 신설:

- `DbKind` — PostgreSql / Oracle / Sqlite / MongoDb
- `IDbProvider` — 접속 열기, 문장 분리, 식별자 인용, 행 특정 키(ctid/ROWID/rowid/_id)
- `IDbCatalog` — 테이블·컬럼·FK (기존 `IErdCatalog` 를 여기로 흡수)
- `DbCapabilities` — §3 표의 플래그

PG 구현은 기존 코드를 **그대로 옮기기만** 한다. `QuerySession`/`QueryExecutor` 가
`DbConnection`·`DbDataReader`(ADO.NET 공통 기반 타입)을 쓰도록 바꾸면 Npgsql 은
ADO.NET 구현이라 대부분 그대로 컴파일된다.

### 1단계 — SQLite (가장 싸고, 나머지를 안전하게 만든다)

파일 기반이라 **단위 테스트에서 진짜 DB 를 띄울 수 있다.** 지금은 카탈로그·편집·
EXPLAIN 이 전부 사람 눈 검증에 묶여 있는데(STATUS.md 개발 메모의 스크린샷 하니스),
SQLite 를 넣으면 이 경로들이 CI 에서 자동 검증된다. Oracle/Mongo 를 시작하기 전에
이걸 먼저 하는 이유가 이것이다.

### 2단계 — Oracle

`Oracle.ManagedDataAccess.Core`. Golden 이 원래 Oracle 툴이라 파리티 문서의 Oracle
고유 항목(AS SYSDBA, AltSchema, DBMS_OUTPUT, ROWID 편집)이 여기서 살아난다.
검증에 Oracle 인스턴스가 필요하다 — 사내 서버 사용 협의 필요.

### 3단계 — MongoDB

SQL 이 아니라 **에디터(쿼리 언어)·그리드(중첩 문서)·자동완성(컬렉션/필드)·편집(_id)
UX 를 전부 새로 설계**해야 한다 (Studio3T 가 참고 대상). 앞 단계와 성격이 완전히
달라 별도 계획으로 분리한다.

## 5. 권장 순서와 이유

**0 → 1(SQLite) → 2(Oracle) → 3(MongoDB)**

SQLite 를 Oracle 보다 먼저 두는 건 기능 요구 때문이 아니라 **검증 인프라** 때문이다.
지금 상태로 Oracle 을 얹으면 회귀를 잡을 방법이 없다.

## 6. 착수 전 결정할 것

- 접속 프로필(`~/.prismone-studio/connections.json`)에 `DbKind` 추가 시
  **기존 파일 호환** 유지 — 필드가 없으면 PostgreSQL 로 간주한다
- 로그온 창에 DB 종류 선택 추가 — Golden 의 Login List 구조를 유지할지
- exe 크기: 드라이버 4종 동시 참조 vs 선택 배포
- Oracle 검증 환경 확보
- Studio 의 "빈 비밀번호 접속 차단"(운영 DB 전제)은 SQLite 파일 접속에 맞지 않는다 —
  provider 별로 규칙을 달리해야 한다
