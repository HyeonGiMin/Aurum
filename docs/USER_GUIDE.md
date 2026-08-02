# IAP Database Manager 사용법

Oracle 시절 Golden 을 대체하는 PRISMONE(PostgreSQL)용 쿼리 툴입니다.
Golden 을 쓰던 손버릇 그대로 쓰이도록 만들었습니다.

## 1. 실행

| 방법 | 용도 |
|---|---|
| `tools/dist/IAP Database Manager.app` | macOS 정식 실행 (독에 이름/아이콘 표시). `sh tools/packaging/macos/make-app.sh` 로 생성 |
| `cd tools && dotnet run --project src/PrismOne.Studio` | 개발 실행 |

앱을 켜면 미접속 상태로 빈 Query 1 탭이 열립니다. 쿼리를 미리 써두어도 됩니다.

## 2. 로그온 (Ctrl+L)

- **Database 는 `host[:port]/database` 한 칸**입니다. 예: `<dev-host>/prismone`,
  `stg-ihp5022:5433/prismone`, 포트 생략 시 5432, `prismone` 만 쓰면 localhost.
- 아래 **Login List** 에서 클릭=필드 채움, **더블클릭=바로 로그인**. New/Delete 로 관리.
- **비밀번호는 항상 AES-256 으로 암호화되어 저장**됩니다 (`~/.prismone-studio/`,
  키는 이 계정 전용). 비밀번호에 한글이 들어오면 두벌식 기준 영문키로 자동 변환됩니다
  (`암호` → `dkagh` — IME 끄는 걸 잊어도 안전).
- **Read Only** 체크: 세션이 읽기 전용(`default_transaction_read_only=on`)으로 열려
  실수로 UPDATE 를 날려도 거부됩니다. 운영 DB 조회용으로 권장.
- 로그인하면 열려 있던 빈 탭들에 세션이 붙습니다.

## 3. 쿼리 실행

| 키 | 동작 |
|---|---|
| **F9** (또는 Cmd/Ctrl+Enter) | **커서 위치 문장 하나** 실행. 선택 영역이 있으면 선택만 |
| **F5** (또는 Shift+Enter) | **Run Script — 커서 문장부터 끝까지** 순차 실행 |
| ⚡ᴱ | **Explain** — 커서 문장의 실행 계획을 트리로 (실행 안 함) |
| ⚡ᴬ | **Explain Analyze** — 실제 실행 후 노드별 실측 시간 트리. **DML 은 자동 롤백**되어 안전. 전체 시간의 50%↑ 노드는 빨강, 20%↑ 주황. 노드에 마우스를 올리면 Filter/Index Cond 표시 |
| 툴바 ⊘ | 실행 취소 (서버에 cancel 전송) |

- 문장은 세미콜론으로 나뉘며 문자열·주석·**달러쿼팅**(`$$…$$`) 안의 `;` 는 무시됩니다.
- 실행 중엔 이전 결과가 비워지고 상태바에 `Running…` + 경과 시간이 올라갑니다.
  실행 중 재실행 요청은 무시됩니다(취소만 가능).

### 바인드 변수 (`:name`)

문장에 `:key` 같은 변수가 있으면 실행할 때 **값 입력 창**이 뜹니다 (Golden 방식).
값은 탭 안에서 기억되므로 다음 실행 땐 이전 값이 채워져 있습니다.
툴바 노란 타원 버튼으로 미리 입력해 둘 수도 있습니다. 비워 두면 NULL 로 전달됩니다.

```sql
select * from prismone.study where study_key = :key and modality = :mod;
```

문자열·주석·달러쿼팅 안의 `:`, 타입 캐스트(`::date`)는 변수로 보지 않습니다.
값은 SQL 에 문자열로 끼워 넣지 않고 **파라미터로 전달**되므로 인젝션 걱정이 없습니다.

## 4. 결과 그리드 — 점진 fetch

- 첫 **100행**이 즉시 표시되고, **스크롤을 내리면 100행씩 이어서** 가져옵니다
  (상태바 `Fetched 300 records (more)`). 큰 테이블도 LIMIT 없이 바로 조회하세요.
- **Ctrl+End** = 끝까지 전부 fetch.
- 셀 값이 아주 크면(예: dcmdataset 의 JSONB) 표시는 500자에서 잘리고
  `… (+N chars)` 로 표기됩니다.
- **셀 더블클릭 → Cell Detail 창**: 잘림 없는 전문을 보여주고, **JSON(jsonb)이면
  자동으로 pretty-print** 됩니다. DICOM Data Set 열람에 사용하세요.
- 셀 복사(헤더 포함), 컬럼 리사이즈 지원. 빈 결과는 `▸ 1 No Records`.

### 그리드 기능 (Results 메뉴)

| 기능 | 설명 |
|---|---|
| **Transpose** (Ctrl+Shift+X) | 행/열 전치 — 컬럼이 많은 한 행을 세로로 읽을 때. 다시 누르면 원래대로 |
| **Size All Columns to Fit** | 모든 컬럼 폭을 내용에 맞춤 |
| **Filter Like Selected Cell** | 선택 셀 값으로 `WHERE` 절을 만들어 에디터 끝에 주석으로 덧붙임 |

## 5. 트랜잭션 (Golden 방식)

- **AutoCommit 은 기본 꺼짐**(툴바 `Auto` 체크박스). INSERT/UPDATE/DDL 을 실행하면
  자동으로 트랜잭션이 열리고 상태바 앞에 **`[TX]`** 가 붙습니다.
- 툴바 **✓ Commit / ⤺ Rollback** 으로 확정/되돌리기. PG 는 **DDL 도 롤백**됩니다.
- PG 특성 반영: 수동 커밋 모드여도 **SELECT/EXPLAIN/SHOW 는 트랜잭션을 열지 않습니다**
  (idle-in-transaction 이 서버 VACUUM 을 방해하는 문제 회피).
- 직접 `BEGIN`/`COMMIT`/`ROLLBACK` 을 실행해도 상태를 따라갑니다.
- fetch 가 진행 중이면 커밋이 거부됩니다 — Fetch All 또는 Cancel 후 커밋하세요.

## 6. 자동완성

- **Ctrl+Space** (macOS 는 **Option+Space** 도) — 수동 호출.
- **`.` 입력 시 자동**: `prismone.` → 테이블 목록, `study.` / `s.`(별칭) → **컬럼 목록**
  (FROM/JOIN 의 별칭을 해석합니다. `from prismone.study s` 후 `s.` → study 컬럼).
- **FROM/JOIN 뒤에서는 스페이스/글자 입력만으로 테이블 목록이 자동으로** 뜹니다.
- 팝업이 뜬 뒤 계속 타이핑하면 필터링, Enter/Tab 으로 삽입.

## 7. 히스토리 · 검색

- **Ctrl+↑ / Ctrl+↓** (툴바 ◀ ▶) — 실행했던 문장 순환. 끝까지 가면 작성 중이던
  초안으로 복귀. 히스토리는 재시작 후에도 유지(최근 500개).
- **Ctrl+F** (툴바 돋보기) — 에디터 검색 패널.

## 8. Object Browser (F8)

- 툴바 ≡ 버튼 또는 **F8** 로 오른쪽 패널 토글 (기본 숨김 — Golden 6 레이아웃).
- Schema/Show(Tables·Views·All) 필터, TableSearch 로 이름 검색.
- 테이블 **클릭 → 아래 describe** (# / Name / Type / N? / PK / FK).
- 테이블 **더블클릭 → 쿼리에 이름 붙여넣기** (`Use Schema` 체크 시 스키마 접두어 포함).
- `select / * / from / where` 미니 그리드: 클릭하면 해당 단어 삽입.

## 9. Messages (RAISE NOTICE)

PG 함수가 `RAISE NOTICE/WARNING` 을 내면 결과 아래 **Messages pane** 이 자동으로
열립니다. 새 실행 때 초기화됩니다.
테스트: `DO $$ BEGIN RAISE NOTICE 'hello %', now(); END $$;`

## 9.5 Session Monitor (Tools 메뉴)

**Tools > Session Monitor** — 현재 DB 의 접속 세션(pg_stat_activity)을 보여줍니다.
PID·사용자·클라이언트·상태·경과시간·대기 이벤트·쿼리. Auto(5s) 자동 새로고침,
**Cancel Query**(쿼리만 취소) / **Terminate**(세션 종료 — 권한 필요).
공유 staging 에서 누가 물고 있는지 확인할 때 사용.

## 10. 파일 · 내보내기 · 탭

- **Ctrl+O** 스크립트 열기(새 탭으로) / **Ctrl+S** 저장.
- Results 메뉴의 내보내기 3종:
  - **Export All Rows As CSV… (COPY)** — 마지막 실행 쿼리를 서버에서 `COPY … TO STDOUT`
    으로 다시 실행해 **전체 행을 잘림 없이 고속으로**. COPY 불가 문장이면 로드된 행 폴백.
  - **Save Grid As TSV…** — 로드된 행을 탭 구분 텍스트로 (엑셀 붙여넣기용).
  - **Save Grid As INSERT…** — 로드된 행을 `INSERT INTO …` 문으로. 대상 테이블명은
    쿼리의 FROM 절에서 추정합니다. 숫자/boolean 은 그대로, NULL 은 NULL 로.
- **Ctrl+T** 새 탭 / **Ctrl+W** 탭 닫기 / 탭줄 오른쪽 **▾** 탭 목록.

### 세션 모델 (Golden 과 동일)

- **탭들은 메인 접속 하나를 공유합니다.** 따라서 Commit/Rollback 은 공유 탭 전체에 걸리고,
  한 탭이 실행 중이면 다른 탭은 `Busy — another tab is running on this session.` 로 거부됩니다.
  접속 하나에 결과셋 하나이므로, 다른 탭에서 새 문장을 실행하면 이전 탭의 남은 fetch 는
  중단됩니다(`Fetch stopped — another tab used this session.`).
- **Ctrl+Shift+T = New Private Tab** — 그 탭만의 **전용 접속**을 엽니다. 독립 트랜잭션이
  필요하거나 긴 쿼리를 돌리면서 다른 탭에서 작업하려면 이걸 쓰세요.

## 11. 단축키 요약

| 키 | 동작 | | 키 | 동작 |
|---|---|---|---|---|
| Ctrl+L | 로그온 | | F9 | 문장 실행 |
| F5 / Shift+Enter | 스크립트 실행 | | Ctrl+End | 전체 fetch |
| Ctrl+Space (⌥Space) | 자동완성 | | Ctrl+F | 찾기 |
| Ctrl+↑ / ↓ | 히스토리 | | F8 | Object Browser |
| Ctrl+T / ⇧T / W | 탭 / 전용탭 / 닫기 | | Ctrl+Z / Y | Undo / Redo |
| Ctrl+O / S | 열기 / 저장 | | | |

(macOS 에선 Ctrl 대신 Cmd 도 동작)

## 12. 데이터 파일 · 문제 해결

- `~/.prismone-studio/` — `connections.json`(접속 목록, 비밀번호 암호문),
  `key.bin`(암호화 키, 0600), `history.jsonl`(쿼리 히스토리).
- 키 파일을 지우면 저장된 비밀번호만 무효가 됩니다(접속 목록은 유지) —
  다음 로그인 때 비밀번호만 다시 입력하면 됩니다.
- 세션이 끊기면 다음 실행 때 같은 프로파일로 자동 재접속합니다(열려 있던 트랜잭션은 소멸).
