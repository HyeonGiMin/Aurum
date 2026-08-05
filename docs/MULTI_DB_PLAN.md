# 멀티 DB 지원 계획 (PostgreSQL / Oracle / SQLite / MongoDB)

작성: 2026-08-04 · 목표: DataGrip 처럼 **접속 대상 DB 를 골라 쓰는** 도구로 넓힌다.

> STATUS.md §1.5 가 "착수 전에 별도 계획 문서로 시작한다"고 한 그 문서다.
>
> **진행 상황 (2026-08-04)**
> - 0단계 **경계 만들기 — 완료.** `Core/Providers/` 에 `DbKind`·`DbCapabilities`·
>   `IDbProvider`·`DbProviders` 레지스트리, `PostgresProvider`, `SqliteProvider`
> - 1단계 **SQLite — 카탈로그까지 완료.** `SqliteErdCatalog` 가 sqlite_master +
>   PRAGMA 로 테이블·컬럼·FK·UNIQUE 를 읽는다. **임시 파일 DB 로 실제 검증**
>   (`SqliteProviderTests` 15개)
> - 2단계 **Oracle — 카탈로그 실접속 검증 완료 (2026-08-04).** `OracleProvider`
>   (ROWID·대문자 인용·RC/Serializable 만) + `OracleErdCatalog`.
>   **Oracle 19.3 에서 PRISMONE 스키마 테이블 517개·관계 293개**를 읽고
>   ErdLayout 까지 통과. 자동 테스트는 서버가 필요해 아직 없다
>   (`OracleProviderTests` 는 서버 없이 도는 항목만 검증)
> - **발견된 문제**: 517개 테이블이면 다이어그램이 2834×11520 이 된다. FK 로 안 이어진
>   단독 테이블이 많아 주제영역이 340개까지 늘어난다. Focus 로 좁히면 되지만
>   전체 보기는 사실상 못 읽는다 — 대형 스키마 배치 개선이 필요하다
> - **로그온 창 DB 종류 선택 — 완료.** Type 드롭다운 + Login List 의 Type 컬럼.
>   접속 검증도 provider 경유(`OpenDbAsync`)
> - **`QuerySession` 드라이버 중립화 — 완료 (2026-08-04).** `DbConnection`/`DbCommand`/
>   `DbDataReader` 로 옮기고 DB 별 차이(BEGIN 문장·세션 격리 구문·서버 NOTICE)를
>   provider 로 뺐다. Oracle 은 BEGIN 을 보내지 않는다(DML 이 암시적으로 열고
>   BEGIN 은 PL/SQL 블록이라 보내면 안 된다). **SQLite 실제 DB 로 실행·커밋 검증**
> - **남은 것**: Object Browser·자동완성 캐시가 아직 PG 카탈로그 전용,
>   COPY 대량 내보내기는 Capabilities 로 껐지만 대체 경로 없음,
>   Oracle AS SYSDBA·Read Only 처리, MongoDB(3단계)

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

#### 2.5단계 — PL/Edit 파리티 (2026-08-04 사용자 방향 제시)

Benthic 은 Golden(쿼리) 외에 **PL/Edit(PL/SQL 에디터)** 를 따로 판다. Oracle 을
제대로 지원하려면 PL/Edit 몫까지 흡수해야 한다. 요구 분해:

| 기능 | 내용 | 우리 기반 |
|---|---|---|
| PL/SQL 블록 실행 | `BEGIN…END;` + `/` 종결 — 문장 분리기가 블록을 통째로 보내야 한다 | StatementSplitter 확장 (핵심 선행) |
| DBMS_OUTPUT 수신 | `DBMS_OUTPUT.ENABLE` 후 `GET_LINES` 폴링 → Messages pane | Capabilities.ServerMessages 이미 true, Messages pane 재사용 |
| 저장 프로시저 편집 | Object Browser 에 Procedure/Function/Package 표시 → 소스 로드(`USER_SOURCE`) → 에디터 | 카탈로그 확장 |
| 컴파일 + 오류 목록 | `CREATE OR REPLACE …` 실행 후 `USER_ERRORS` 조회 → 줄 번호 클릭 이동 | SqlErrorRenderer(밑줄) 재사용 가능 |
| 실행/테스트 | 프로시저 선택 → 파라미터 입력 창 → 호출 + 결과/OUT 값 | BindVariableDialog 확장 |
| 디버거(브레이크포인트) | PL/Edit 의 DBMS_DEBUG 디버거 | **비채택** — 비용 대비 사용 빈도 낮음, 후순위 |

전부 **Oracle 실서버 검증이 선행 조건** — 현재 개발 머신에는 Oracle 접속이
없다 (STATUS.md §1.7). 서버 확보 후: 쿼리 실행 실검증 → 블록 분리 →
DBMS_OUTPUT → 소스 편집·컴파일 순.

### 3단계 — MongoDB

SQL 이 아니라 **에디터(쿼리 언어)·그리드(중첩 문서)·자동완성(컬렉션/필드)·편집(_id)
UX 를 전부 새로 설계**해야 한다 (Studio3T 가 참고 대상). 앞 단계와 성격이 완전히
달라 별도 계획으로 분리한다.

**진행 상황 (2026-08-05) — Core + ADO 셰임 + Explorer 까지 연결됨**

들어온 것 (전부 테스트 있음, Mongo 관련 342개 스위트 안에 포함):

| 부분 | 파일 | 검증 |
|---|---|---|
| 셸 구문 파서 (`find`/`aggregate`/`use` 등) | `Mongo/MongoCommand.cs` | 서버 없이 다수 |
| 문서 → 표 평탄화 | `Mongo/MongoDocuments.cs` | 서버 없이 다수 |
| 접속·실행·필드 추론·DB 전환 | `Mongo/MongoSession.cs` | 실서버 다수 |
| **ADO.NET 셰임** (DataGrip 의 mongo-jdbc-driver 대응) | `Mongo/MongoAdo.cs` | 실서버 다수 |
| provider 등록·ERD 카탈로그(DB→컬렉션) | `Providers/MongoProvider.cs` | 실서버 다수 |

- 지원 구문: `find`(filter·projection·`.limit/.skip/.sort`) · `findOne` · `aggregate` ·
  `countDocuments` · `distinct` · `show collections` · **`use <db>`**(실 mongosh 문법 —
  이후 문장이 조회할 DB 를 바꾼다). **읽기 전용** — `drop`/`insert` 같은 쓰기는 파서가
  받지 않는다 (Studio 는 조회 전용, STATUS §2·3).
- 필터·파이프라인은 문자열이 아니라 **BSON 문서로 드라이버에 전달**한다 (주입 여지 없음).
- 중첩 문서는 `address.city` 점 경로로 펴고(기본 3단계), 배열은 JSON 한 칸으로 둔다.
- 컬렉션 필드는 카탈로그가 없으므로 **샘플 50건에서 추론**한다.
- **DB 미지정 접속 지원**: `host[:port]` 만 쳐도 되고, 그러면 Explorer 가 서버의 모든 DB 를
  보여준다(`host[:port]/db` 로 쓰면 그 DB 만). **DB 를 한 번도 안 정한 채 조회하면
  예전처럼 임의 DB(test)로 조용히 넘어가지 않고 에러로 알린다** — 엉뚱한 빈 DB 를 조회해
  "0건"으로 착각하는 걸 막기 위해서다. Explorer 더블클릭·`use` 명령으로 DB 를 정한다.
- **ADO.NET 셰임 완료**: `MongoDbConnection`/`MongoDbCommand`/`MongoDbDataReader` 가
  `DbConnection` 계약을 채워 `QuerySession` 이하(그리드·정렬·내보내기·Text 뷰)를 그대로
  재사용한다 — DataGrip 이 JDBC 드라이버로 하는 것과 같은 수. `ChangeDatabase` 가
  실제로 DB 를 바꾼다(재접속 없이).
- **Database Explorer(왼쪽, Alt+1)**: DataGrip 식 DB→컬렉션 트리, 아이콘(원통/표/뷰),
  더블클릭 시 자동 DB 전환 + 조회 문장 삽입. Golden 에 없던 패널.
- **Edit Document(Studio3T 대응, Ctrl+Shift+D)**: 그리드 행 하나를 JSON 으로 통째로 고쳐
  `_id` 기준 `replaceOne` 으로 저장한다. `find`(projection 없음) 결과에만 붙는다 —
  `aggregate`·projection 있는 find 는 문서가 원본과 달라질 수 있어 제외(SQL 의
  "단일 테이블 SELECT 만 편집"과 같은 이유). `_id` 변경은 DB 왕복 전에 막는다.
  구현: `MongoRowContext`(어느 문서였는지)가 `FetchedRow`(Core, provider 중립 `object?`
  자리)를 거쳐 `RowItem.MongoContext` 까지 그대로 실려간다 — 그리드·정렬·QuerySession
  은 손대지 않았다.
- **Add/Delete Document**: Edit Document 와 같은 급의 즉시 저장. Add 는 이미 조회된
  행 아무거나로 대상 컬렉션을 짐작한다(0건 조회 상태에선 거부). Delete 는 SQL 의
  단계적 커밋과 달리 즉시 지워지므로 먼저 확인창을 띄우고, 성공한 행은 재조회 없이
  그리드에서도 바로 뺀다.
- **Explorer 자동 열림**: Mongo 로 접속하면 왼쪽 Database Explorer 를 자동으로 연다 —
  스키마가 없어 오른쪽 Object Browser 보다 DB→컬렉션 트리가 실질적인 시작점이다.
- **JSON Import/Export**: `Tools > Import JSON…` 은 JSON 배열·JSON Lines(mongoexport
  기본 산출물) 둘 다 받는다. 컬럼 매핑 없이 그대로 InsertMany — **원자성이 없어**
  중간에 실패하면 그 앞까지는 이미 들어간 채로 멈추고, 몇 개가 들어갔는지 예외
  메시지로 알린다. `Results > Save Grid As JSON…` 은 Mongo 뿐 아니라 **모든 DB
  종류에서** 되는 범용 내보내기로 넣었다(GridExporter 에 Json 포맷 추가).
- 검증 인프라: `docker run -d --name aurum-mongo-test -p 127.0.0.1:27017:27017 mongo:7`
  후 `AURUM_MONGO_TEST_HOST=localhost`. 환경변수가 없으면 실서버 테스트는 그냥 통과한다.
  포트를 일부러 틀리면 실서버 테스트만 실패하는 것으로 "정말 서버를 친다"를 확인했다.

- **explain() — 완료 (2026-08-05)**: 툴바 ⚡ᴱ=queryPlanner · ⚡ᴬ=executionStats 를
  `explain` 커맨드로 돌려 **PG 와 같은 플랜 트리(self 시간 막대)** 로 매핑한다
  (`MongoExplain` — winningPlan/executionStages/aggregate stages 세 모양 + 미지 형태
  폴백). `.explain()` 체인도 파서가 받는다(원문 JSON 한 칸). 실서버 검증 3건.
- **currentOp 세션 모니터 — 완료 (2026-08-05)**: `SessionMonitor` 가 Kind 로
  분기해 Mongo 는 `currentOp` → 같은 ActivityRow/창 재사용, Cancel/Terminate 는
  둘 다 `killOp`. SQLite 처럼 지원 없는 DB 는 메뉴에서 안내로 막는다.

- **중첩 문서 트리 뷰 — 완료 (2026-08-05)**: Results > View Documents as Tree.
  `Core/MongoTree`(순수 변환, 테스트 6개) + `MongoTreeWindow`(자식은 펼칠 때
  생성 — 큰 문서에서 안 굳는다, Copy Value 컨텍스트 메뉴). Edit Document 와
  같은 조건(순수 find)의 원본 문서를 쓴다. 스크린샷 `shot_mongotree.png`.

**남은 것**: 없음 — 3단계 계획 범위 완료. Studio3T 대비 더 가져올 만한 동작은
실사용 후 검토.

**범위 확인 (2026-08-04 사용자)**: DataGrip 처럼 접속 대상 DB 로 직접 지원하되,
Studio3T 의 실무 기능(컬렉션 브라우저, find/aggregate 실행, 문서 그리드(중첩 펼침),
_id 기준 편집, JSON import/export)을 목표로 한다. 착수 시 검증 인프라부터:
로컬 mongod (현재 개발 머신 미설치 — `brew install mongodb-community`) 또는
Testcontainers 로 **테스트 가능한 상태를 먼저** 만든다 (SQLite 때와 같은 원칙).

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
