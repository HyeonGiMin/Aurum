# Aurum × Claude Code — MCP 연결 가이드

Claude Code 를 터미널에 띄워 두고 말로 시키면, Claude 가 **실행 중인 Aurum 화면에서**
스키마를 보고 → 에디터에 SQL 을 쓰고 → 실행하고 → 결과를 읽습니다.
하는 일은 전부 Aurum 화면에 그대로 보이고, 데이터를 바꾸는 일은 매번 사용자 승인을 받습니다.

```
Claude Code (터미널) ──MCP(HTTP, localhost)──▶ 실행 중인 Aurum ──▶ DB
```

---

## 1. 준비

- Aurum (이 기능이 들어간 버전). 소스에서 실행한다면:
  ```powershell
  cd D:\07.Source\iap\Aurum
  dotnet run --project src/PrismOne.Studio
  ```
- Claude Code (`claude` 명령)

## 2. Aurum 에서 MCP 서버 켜기 (처음 한 번)

1. **Tools > MCP Server (Claude Code)…**
2. **"MCP 서버 켜기"** 체크 → 포트는 기본 `47900` → **OK**
   - 상태바에 `MCP server listening — http://localhost:47900/mcp` 가 뜨면 성공
3. 다시 같은 창을 열어 **[명령 복사]** — 화면에는 토큰이 가려져 있지만 복사되는 명령에는 전체 토큰이 들어 있습니다.

이후로는 Aurum 을 띄우면 서버가 자동으로 열립니다.

## 3. Claude Code 에 등록 (처음 한 번)

복사한 명령을 터미널에 붙여 넣습니다. **`--scope user`** 를 붙이면 어느 폴더에서 Claude Code 를 열어도 쓸 수 있습니다
(빼면 명령을 실행한 폴더에서만 보입니다).

```powershell
claude mcp add --transport http --scope user aurum http://localhost:47900/mcp --header "Authorization: Bearer <복사된 토큰>"
```

확인:

```powershell
claude mcp list
# aurum: http://localhost:47900/mcp (HTTP) - ✓ Connected
```

> Aurum 이 실행 중이어야 Connected 로 나옵니다.

## 4. 사용

1. **Aurum 을 먼저 띄우고 DB 에 로그온**합니다.
2. Claude Code 를 열고 `/mcp` 로 `aurum` 이 connected 인지 확인합니다.
3. 말로 시킵니다.

```
aurum 으로 지금 접속된 DB 테이블 목록 보여줘
patient 테이블 구조 보고, 최근 7일 등록 건수를 날짜별로 뽑아줘
방금 결과에서 건수가 0인 날이 있는지 확인해줘
탭 1 에디터에 있는 내 SQL 좀 봐주고 느린 이유 알려줘
```

Claude 는 **`Claude` 라는 전용 탭**을 만들어 작업합니다 — 내가 쓰던 탭의 SQL 을 덮어쓰지 않습니다.
내 탭을 보게 하려면 "탭 1" 처럼 번호로 말하면 됩니다.

### Claude 가 쓰는 도구

| 도구 | 하는 일 |
|---|---|
| `aurum_status` | 접속·탭 목록·트랜잭션·실행 상태 |
| `list_tables` · `describe_table` | 스키마 (introspection 캐시 — DB 왕복 없음) |
| `validate_sql` | 없는 테이블·컬럼 검사 (에디터의 빨간 밑줄과 같은 검사) |
| `get_editor` · `set_editor` · `new_tab` | 에디터 읽기/쓰기 (Ctrl+Z 로 되돌릴 수 있음) |
| `run_query` · `get_results` | 실행과 결과 페이지 읽기 |
| `commit` · `rollback` | 트랜잭션 확정/취소 |

## 5. 조작 중 표시와 입력 잠금

Chrome 확장이 브라우저를 제어할 때처럼:

- Claude 가 도구를 쓰는 동안 창에 **주황 테두리 + 상단 배너**
  ("Claude 가 Aurum 을 조작하는 중 (run_query) — 입력이 잠겨 있습니다") 가 뜨고 마우스·키보드가 잠깁니다.
  연달아 호출할 때 깜빡이지 않게 마지막 호출 뒤 3초간 유지합니다.
- **[중지] 또는 Esc** — 실행 중인 쿼리를 취소하고 Claude 의 조작을 멈춥니다.
  오른쪽 아래 **[재개]** 를 누르기 전까지 Claude 의 요청은 "사용자가 중지함" 으로 거절됩니다.
- 승인 창은 별도 창이라 잠금과 상관없이 답할 수 있습니다.

## 6. 승인 — 데이터를 바꾸는 SQL

**단순 SELECT 만 바로 실행**됩니다. 그 밖의 문장(INSERT/UPDATE/DELETE, DDL, `SELECT … FOR UPDATE`,
`SELECT … INTO`, PL/SQL 블록, 부작용 있는 함수 `nextval` 등)은 **SQL 전문을 보여주는 승인 창**이 뜹니다.

| 탭의 Tx 모드 | Claude 에게 시킨 말 | 묻는 것 |
|---|---|---|
| 수동 커밋 (기본) | "업데이트 해줘" | ① 실행 승인 → 미확정으로 남음 → ② COMMIT 승인 (따로) |
| 수동 커밋 (기본) | "업데이트하고 **커밋까지** 해줘" | **실행 + COMMIT 승인 한 번** — 실행 중 오류가 나면 자동 ROLLBACK |
| Auto Commit | 무엇이든 | **실행 승인 한 번** (실행 즉시 확정 — 빨간 경고) |

- 승인 창은 **5분 안에 답하지 않으면 거부**로 처리합니다.
- 승인 기다리는 중·쿼리 실행 중에 들어온 다른 요청은 줄 서지 않고 *busy* 를 받습니다 (재전송된 쓰기가 두 번 실행되지 않게).
- COMMIT·ROLLBACK 을 따로 요청할 때도 승인 창이 뜹니다.
- 확정 전 변경은 툴바 **Commit (Ctrl+F5) / Rollback (Ctrl+F6)** 으로 직접 처리해도 됩니다.

## 7. 보안과 기록

- 서버는 **localhost 에만** 열리고 **Bearer 토큰**이 없으면 401 입니다. 브라우저 페이지에서 오는 요청(외부 Origin)은 403 으로 거부합니다.
- 토큰은 접속 비밀번호와 같은 방식으로 암호화해 `~/.prismone-studio/mcp.json` 에 둡니다.
  유출이 의심되면 MCP 창에서 **[토큰 재발급]** → `claude mcp remove aurum` → 새 명령으로 다시 등록.
- Claude 에게 넘어가는 결과는 **호출당 최대 200행**, 긴 셀은 500자에서 자릅니다.
- **감사 로그** `~/.prismone-studio/mcp-audit.jsonl` — Claude 가 실행·조회한 SQL, 승인 여부, 돌려준 행 수 (행 데이터는 남기지 않음).
- 한계: 사용자 정의 함수(`select my_func()`)의 부작용은 판별하지 못합니다 — 수동 커밋 모드가 마지막 안전망입니다.
  **민감한 DB 에서는 MCP 서버를 꺼 두세요.**

## 8. 문제 해결

| 증상 | 해결 |
|---|---|
| `claude mcp list` 에서 Failed | Aurum 이 꺼져 있거나 MCP 서버가 꺼짐 → Aurum 을 켜고 Claude Code 에서 `/mcp` → aurum → **Reconnect** |
| 상태바 "포트를 열 수 없습니다" | 다른 프로그램이 47900 사용 중 → MCP 창에서 포트 변경 → `claude mcp remove aurum` 후 새 명령으로 재등록 |
| 401 (토큰 재발급 뒤) | `claude mcp remove aurum` → 새로 복사한 명령으로 재등록 |
| "busy" 응답 | 앞 쿼리가 실행 중이거나 승인 창이 떠 있음 → Aurum 화면의 승인 창에 답하기 |
| "user stopped Claude's control" | [중지] 를 누른 상태 → 오른쪽 아래 **[재개]** |
| "not connected" | Aurum 에서 DB 로그온 (Ctrl+L) |
