# DataGrip 대비 갭 — Aurum 에 녹일 것

작성: 2026-08-04

Golden 파리티는 끝났다(GOLDEN_BEHAVIOR.md). 이 문서는 **Golden 에는 없지만 DataGrip
에는 있어서 실무에서 아쉬운 것**을 추려 우선순위를 매긴 것이다.
멀티 DB 는 별도 계획(MULTI_DB_PLAN.md)이라 여기서 다루지 않는다.

## 우선순위

**가치는 "DataGrip 리뷰에서 자주 언급되는가"가 아니라 "우리 사용자가 얼마나 자주,
얼마나 절실하게 쓰는가"로 매긴다.** 초판에서 스키마 비교를 "매우 높음"으로 매겼다가
아래 §3 의 이유로 내렸다 (2026-08-04 수정).

| # | 기능 | 지금 | 빈도 | 비용 | 판단 |
|---|---|---|---|---|---|
| 1 | **스키마 introspection 캐시** | 매번 조회 | 상시 | 낮음 | ✅ 완료 |
| 2 | **SQL 검증** (없는 테이블/컬럼 표시) | 없음 | **매일** | 중간 | ✅ 완료 (2026-08-04) |
| 3 | Explain Plan 시각화(비용·행수 막대) | 트리만 | 자주 | 낮음 | ✅ 완료 (2026-08-04) |
| 4 | 스키마 **읽기 전용 diff** | 없음 | 가끔 | 낮음 | ✅ 완료 (2026-08-04) |
| 5 | CSV/TSV **import** (파일 → 테이블) | export 만 | 가끔 | 중간 | ✅ 완료 (2026-08-04) |
| 6 | 결과 탭 여러 개 / 고정(pin) | 탭당 1개 | 가끔 | 중간 | ✅ 완료 (2026-08-04, 새 창 고정) |
| 7 | Find usages / Go to declaration | 없음 | 가끔 | 높음 | 파서 필요 |
| 9 | **SSH 터널** (점프 호스트 경유 접속) | 없음 | 상시(운영망) | 중간 | ✅ 완료 (2026-09-02) |
| 10 | ssh-agent · ~/.ssh/config · ProxyJump | 없음 | 자주(운영망) | 중간 | ✅ 완료 (2026-09-02) |
| 8 | 커스텀 추출기(스크립트) | 고정 4종 | 드묾 | 중간 | 보안상 비채택 |
| — | ~~동기화 DDL 생성~~ | 없음 | — | — | **Aurum 제외 → `iapdb`** |

## 1. 스키마 introspection 캐시 — 먼저 하는 이유

**지금 문제 (코드 확인):** `MainWindow.axaml.cs` 의 `OnObjectSelected` 가 테이블을 고를
때마다 `_profile.OpenAsync()` 로 **새 접속을 연다**. describe 한 번에 TCP 연결 + 인증이
붙는다. `LoadBrowserAsync` 도 별도 접속을 연다. 공유 세션 모델을 쓰면서 카탈로그
조회만 접속을 남발하는 셈이다.

**DataGrip 방식:** 데이터 소스를 등록하면 스키마를 한 번 introspection 해서 로컬에
캐시하고, 이후 자동완성·탐색·describe 는 캐시에서 즉시 답한다. 갱신은 명시적으로
(Refresh) 하거나 DDL 을 실행했을 때.

**얻는 것:**
- describe 즉시 응답, 접속 남발 제거
- 자동완성이 컬럼까지 서버 왕복 없이 완성
- #2(SQL 검증)의 전제 — 검증하려면 스키마를 알고 있어야 한다

**설계:**
- `Core/SchemaCache` — `(schema.table → 컬럼 목록)` 을 접속당 1개 메모리에 보관
- 적재는 **테이블 목록 + 전 컬럼을 한 번에** (지금은 테이블당 1쿼리)
- `Refresh()` 명시 갱신 + DDL 실행 시 무효화
- 순수 자료구조로 두고 조회 함수를 주입해 단위 테스트를 붙인다

## 2. SQL 검증 — ✅ 구현됨 (2026-08-04)

캐시가 생기면 에디터에서 `from prismone.stduy` 같은 오타를 실행 전에 잡을 수 있다.
전체 SQL 파싱은 과하니, 우선 `FROM`/`JOIN` 뒤 식별자와 `alias.column` 만 대조한다
(자동완성이 이미 하는 해석을 재사용).

**구현 (2026-08-04):** `Core/SqlValidator` + `Studio/SqlErrorRenderer`(물결 밑줄) +
QueryTabView 의 0.6초 디바운스 타이머·호버 툴팁. FROM/JOIN/INTO/UPDATE 뒤 테이블
(콤마 조인 포함)과 `별칭.컬럼`을 introspection 캐시와 대조한다. 원칙은 **확신할 때만
표시** — CTE·서브쿼리 별칭·모르는 스키마·내장 이름(dual/pg_*/sqlite_*)·따옴표
식별자·함수(`extract(year from …)` 의 from 포함)는 침묵. 주석·문자열·달러 인용은
마스킹. 단위 테스트 22개 (`SqlValidatorTests`).

## 3. 스키마 비교 — 쪼개서 판단 (2026-08-04 수정)

초판에서 "가치 매우 높음"으로 매겼으나 **내렸다.** 이유 셋:

1. **빈도가 낮다.** 우리 사용자의 하루는 쿼리 실행·자동완성이 수십~수백 회고,
   스키마 비교는 패치 후 검증 때 한 번이다. DataGrip 리뷰에서 자주 언급된다는 건
   그쪽 사용자(스키마를 직접 설계·변경하는 개발자) 기준이지 우리 기준이 아니다.
2. **동기화 DDL 생성은 우리 설계 원칙과 충돌한다.** STATUS.md §2·3 이 명시한다 —
   *"Studio(GUI) = 조회·관리 전용, 스키마 버전에 비종속. 패치 Apply 버튼 없음.
   초기 설치·패치 적용 = `iapdb` CLI"*. 운영 DB 에 붙은 GUI 가 ALTER 스크립트를
   뱉으면, 실행을 안 하더라도 복사·붙여넣기로 "패치는 CLI 로, 재현 가능하게"라는
   규칙이 우회된다.
3. **부분적 대체재가 이미 있다.** 상태바의 스키마 버전 pill 이 마지막 적용 패치를
   보여준다.

### 쪼갠 결과

- **3a. 읽기 전용 diff — ✅ 구현됨 (2026-08-04)**
  Tools > Schema Diff. `Core/SchemaDiff`(순수 비교, 테스트 10개) +
  `SchemaSnapshotFile`(JSON 스냅샷) + `Studio/SchemaDiffWindow`.
  기준은 스냅샷 파일 또는 저장된 접속(비밀번호 저장분만), 대상은 현재 접속.
  테이블 빠짐/추가/달라짐(컬럼 단위 type·null·pk), FK 는 **제약 이름이 아니라
  연결(자식→부모 컬럼)로 동일성**을 판정 — 사이트마다 자동 생성 이름이 달라도
  같은 관계면 같다. Oracle↔PG 대소문자 차이는 무시. DDL 은 만들지 않는다.
  "이 사이트 스키마가 표준과 뭐가 다른가"를 즉시 보는 것. 조회 전용이라 원칙과
  충돌하지 않고, 지원팀이 여러 사이트를 볼 때 값이 있다.
  **`IErdCatalog` 가 이미 테이블·컬럼·FK 를 DB 중립 모델로 읽으므로**, 두 스냅샷을
  떠서 비교하는 것만 추가하면 된다 — 비용이 싸다.
  버전 pill 이 못 잡는 것도 여기서 잡힌다: *기록상 패치는 적용됐는데 실제 스키마가
  어긋난* 경우.

- **3b. 동기화 DDL 생성 — Aurum 에서 제외**
  자리는 `iapdb` 다. 배포 키트가 목표 스키마를 알고 있으므로 거기서 생성하는 게
  맞고, 그래야 사일런트 설치·감사·재현성 요건도 지켜진다.

## 4. Explain Plan 시각화 — ✅ 구현됨 (2026-08-04)

이미 EXPLAIN 트리가 있다. DataGrip 처럼 **노드별 비용·예상 행수를 막대로** 표시하고
가장 비싼 노드를 강조하면 싼 값에 체감이 커진다. `PlanParser` 가 비용을 이미
파싱하는지 확인한 뒤 렌더만 얹는다.

**구현 (2026-08-04):** 노드마다 self 비중 막대 + %(50%↑ 빨강 / 20%↑ 주황 / 초록).
핵심은 **누적치가 아니라 self(자식 몫을 뺀 값)** 로 강조하는 것 — 이전 구현은 누적
시간 기준이라 루트가 항상 빨갛게 나왔다. Analyze 는 실측 시간, plan-only 는 추정
비용 기준. 행수 예측 10배↑ 오차엔 `rows ×N` 배지(Plan Rows 와 Actual Rows 는 둘 다
루프당 값이라 loops 를 곱하지 않고 비교). `PlanParserTests` 6개 추가,
오프라인 스크린샷 `shot_plan.png`.

## 5~8

- ~~**import**~~: ✅ 구현됨 (2026-08-04) — Tools > Import CSV/TSV.
  `Core/CsvParser`(RFC 4180: 따옴표·필드 안 줄바꿈·`""`, 구분자 자동 감지) +
  `CsvImporter`(헤더 이름 매핑, 위치 매핑, 구조 사전 검증) + `CsvImportDialog`.
  타입 추론은 하지 않는다 — 값을 문자열로 보내 서버가 캐스팅(그리드 편집과 동일).
  **전량 성공 아니면 전량 롤백**, 실패 시 행 번호와 DB 오류 보고. 전용 접속에서
  실행해 공유 세션을 방해하지 않는다. `IDbProvider.ParameterPlaceholder`
  추가($n/@pn/:pn — Microsoft.Data.Sqlite 는 이름 없는 파라미터를 거부해서 필요했다).
  테스트 19개 (파서 7 + 매핑/빌드 6 + SQLite 실 DB 왕복 3 + 기타).
- ~~**결과 탭 여러 개/pin**~~: ✅ 구현됨 (2026-08-04) — Results > Pin Results to
  New Window. 결과 영역을 다중 탭으로 바꾸는 대신 **스냅샷을 별도 창에 고정**하는
  방식을 택했다 — 가장 많이 쓰는 그리드 경로를 건드리지 않아 안정성 위험이 없고,
  "두 결과를 나란히 비교"라는 실제 용도는 그대로 충족한다.
- **Find usages**: SQL 파서가 있어야 제대로 된다. 비용 대비 후순위
- **커스텀 추출기**: 스크립트 실행 = 보안 검토 필요. 후순위

## 9. SSH 터널 — ✅ 구현됨 (2026-09-02, DataGrip + pgAdmin 기준)

**왜 필요했나:** 운영 DB 는 포트가 밖으로 안 열려 있고 bastion 을 거친다. 그동안은
사용자가 손으로 `ssh -L` 을 띄우고 로그온 창에 `localhost:<임의포트>` 를 적어야 했다 —
포트를 외우고, 터널이 죽으면 왜 안 되는지 알 수 없었다.

**어디에 끼웠나:** 접속 경로가 이미 한 곳으로 모여 있어(`ConnectionProfile` →
`IDbProvider.BuildConnectionString`/`OpenAsync`) 그 경계에서 host/port 만 로컬 포워딩
끝점으로 바꾸는 것으로 끝났다. `SchemaCache`·`ErdCatalog`·`SessionMonitor`·`CopyExporter`
같은 호출부는 한 줄도 고치지 않았다.

- `Ssh/SshOptions` — 점프 호스트 + 인증(비밀번호 / 개인키+passphrase). `ConnectionProfile`
  과 `SavedConnection` 의 **맨 뒤 기본값 필드**라 기존 `connections.json` 이 그대로 읽힌다.
- `Ssh/SshTunnel` — SSH.NET 기반. **127.0.0.1 의 임의 포트로만** listen 한다(`ssh -L` 기본과
  동일 — 남이 우리 터널을 타고 DB 에 붙지 못하게). 포트는 미리 잡아 두고 그 번호로 연다.
- `Ssh/SshTunnelPool` — **(SSH 대상 + DB 대상)마다 터널 하나**를 재사용한다. 이게 핵심이다:
  자동완성 캐시·ERD·Session Monitor 가 접속을 열고 바로 닫는데, 그때마다 SSH 핸드셰이크를
  하면(1~2초) 도구를 못 쓴다. 쿼리 탭은 `LeaseAsync` 로 참조를 걸어 붙잡고, 참조가 없는
  터널은 5분 뒤 닫힌다.

**호스트 키 검증 (pgAdmin 기준):** SSH.NET 은 `HostKeyReceived` 를 구독하지 않으면
**어떤 호스트 키든 그냥 신뢰한다.** 그대로 두면 중간자가 bastion 인 척하고 SSH 비밀번호와
그 뒤의 DB 비밀번호를 전부 받아갈 수 있다 — 터널을 쓰는 이유 자체를 무너뜨린다.
그래서 pgAdmin 과 같은 규칙을 넣었다:

- 아는 키면 조용히 통과, `@revoked` 면 무조건 거부, 처음 보거나 다르면 **지문을 보여주고 묻는다**
  (`SHA256:…` — `ssh` 명령과 같은 표기라 관리자에게 받은 값과 눈으로 대조된다).
- 불일치는 알던 지문과 받은 지문을 나란히 보여주는 경고. **기본 버튼은 언제나 거부**다.
- `~/.ssh/known_hosts` 는 **읽기만** 한다(해시 항목·와일드카드·`[host]:port` 포함) — 터미널에서
  이미 붙어 본 사람은 아무것도 안 물어보게. 승인분은 `~/.prismone-studio/known_hosts` 에만 쓴다:
  다른 도구가 쓰는 파일을 GUI 가 말없이 고치면 안 된다.
- **물어볼 UI 가 없으면 거부한다**(`SshTunnelPool.HostKeyPrompt` 가 비어 있을 때 — 콘솔·테스트).
  물어볼 사람이 없다고 조용히 신뢰하면 이 기능이 없는 것과 같다.
- 물음은 핸드셰이크 도중 **동기**로 오므로 터널 접속을 통째로 스레드 풀에서 돌린다
  (`Task.Run`). UI 스레드에서 물으면 창을 띄우는 순간 교착한다.

**비밀 저장 (pgAdmin 의 "Prompt for password?"):** SSH 비밀번호·passphrase 는 DB 비밀번호와
같은 `PasswordCipher` 로 암호화해 남기지만, `SavePassword` 를 끄면 **암호문으로도 남기지
않는다** — 그 선택의 뜻은 "잘 숨겨라" 가 아니라 "두지 마라" 다. 저장을 끈 항목은 Login 을
누를 때 비밀번호 칸으로 들어간다(DB 비밀번호 재입력과 같은 흐름).

**DB 별로 따로 손댄 곳:**

- **Oracle** — `OracleErdCatalog` 는 provider 를 안 거치고 직접 접속 문자열을 만들어서
  거기서도 한 번 더 풀어 준다. 드라이버 풀링(MaxPoolSize=5)이 켜져 있어 터널이 풀보다
  오래 살아야 하는데, 탭이 참조를 잡고 있으므로 만족한다.
  *남은 제약*: 리스너가 다른 포트로 재접속을 지시하는 구성(SCAN 등)은 터널 밖으로 나간다.
- **MongoDB** — `MongoSession.BuildConnectionString` 이 provider 를 우회해 직접 만들기에
  거기서 풀어 준다. 터널일 때는 **`directConnection=true`** 를 붙인다 — 안 붙이면 드라이버가
  replica set 토폴로지를 탐색해 *서버가 알려준* 호스트로 다시 붙고, 그 주소는 터널 밖이라
  조용히 실패한다.
- **SQLite** — 파일 DB라 대상이 아니다. UI 에서 아예 비활성화한다(DataGrip 도 같다).

**신원 문제 하나:** 터널을 쓰면 DB 주소가 대개 `localhost:5432` 로 같아진다. 그래서
`SavedConnection.SameTarget`·`DisplayName` 에 점프 호스트를 넣었다 — 안 그러면 서로 다른
서버의 접속이 로그인 목록에서 하나로 뭉개져 서로를 덮어쓴다. 단 `DisplayDatabase` 는
건드리지 않았다(로그온 창이 그 문자열을 되파싱한다).

**ssh-agent (DataGrip 의 "authentication agent"):** SSH.NET 에는 agent 지원이 없다.
대신 `HostAlgorithm` 이 정확히 맞는 훅이었다 — 공개키 blob(`Data`)과 서명(`Sign`)만 내주면
되고, agent 가 하는 일이 정확히 그 둘이다. 그래서 OpenSSH agent 프로토콜을 직접 구현하고
(`Ssh/SshAgent.cs`) `AgentHostAlgorithm : HostAlgorithm` 으로 끼웠다. **개인키는 우리
프로세스에 들어오지 않는다** — 그게 agent 를 쓰는 이유다. RSA 키는 요즘 서버가 SHA-1 서명을
거부하므로 rsa-sha2-512 · rsa-sha2-256 · ssh-rsa 를 선호도 순으로 셋 다 올린다.
전송은 Linux·macOS 가 `$SSH_AUTH_SOCK` 유닉스 소켓, Windows 가 OpenSSH 명명 파이프다.
Pageant 고유의 WM_COPYDATA 공유메모리 방식은 넣지 않았다 — Pageant 를 OpenSSH 파이프로
노출하면(PuTTY 0.77+) 그대로 붙는다.

**`~/.ssh/config` (DataGrip 의 "OpenSSH config"):** `Ssh/SshConfig.cs`. OpenSSH 규칙 그대로
**먼저 나온 값이 이긴다** — 그래야 사람들이 파일 맨 아래에 `Host *` 를 두는 관례가 통한다.
`Host` 패턴(와일드카드·부정), `Include`(글로브), `Key=Value` 표기, `~` 확장을 다룬다.
`Match` 블록은 **통째로 건너뛴다**: 조건이 실행 환경에 달려 있어 우리가 판정할 수 없고,
잘못 적용하면 엉뚱한 호스트에 설정이 새기 때문이다.

**ProxyJump 다단 경유 (DataGrip 도 못 하는 것 — ProxyCommand 로 우회해야 한다):**
`Ssh/SshHops.cs` 가 설정을 홉 목록으로 편다. 터널은 **포워딩을 사슬로 잇는다** — 첫 홉에
붙어 두 번째 홉의 22번으로 가는 로컬 포워딩을 열고, 그 로컬 포트에 두 번째 SSH 세션을
붙이고, 이를 반복하다 마지막 홉에서 DB 로 포워딩한다. SSH.NET 의 공개 API 만으로 된다.

여기서 놓치기 쉬운 함정 하나: 중간 홉은 `127.0.0.1:임의포트` 로 붙지만 **호스트 키는 그
홉의 진짜 이름으로 대조해야 한다.** 루프백 주소로 대조하면 모든 점프 호스트가 같은 이름이
되어 검증이 통째로 무의미해진다. 그래서 "TCP 를 여는 주소" 와 "신원" 을 분리해 넘긴다.
순환(`a → b → a`)은 깊이로 끊는다 — 홉은 재귀가 풀릴 때 담기므로 목록 길이만 봐서는 못 잡는다.

**안 넣은 것:** SSH 설정을 여러 접속이 공유하는 구조(DataGrip 의 이름 붙인 SSH
configuration). 지금은 접속마다 따로 들고 있다.

테스트 51개 (설정 검증 · 프로필 전파 · 저장 신원 · Mongo 접속 문자열 · known_hosts 대조 12 ·
비밀 저장 3 · ~/.ssh/config 파싱 10 · 홉 확장 9). 실제 포워딩 · 호스트 키 물음 · agent 서명은
sshd 와 agent 가 있어야 해서 자동 테스트로 두지 않았다 — Oracle/Mongo 실접속 테스트와 같은 방침.
