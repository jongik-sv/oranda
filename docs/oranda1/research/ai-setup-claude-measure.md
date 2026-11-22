# AI 연결 도우미 — claude 실측

- 회차 ai-onboard-1123. 계약 = [ai-setup-contract.md](ai-setup-contract.md). 절마다 잰 PC 가 다르다(Windows = win144 W2, Linux = L1).
- 규칙: 임시 폴더를 `USERPROFILE`·`HOME`·`CLAUDE_CONFIG_DIR` 로, `ANTHROPIC_API_KEY`·`ANTHROPIC_AUTH_TOKEN` 비움. 로그인·설치 실행 안 함. 끝나고 임시 폴더 지움.

## Windows

- 잰 날: 2026-11-23, win144(Windows 11 Pro 10.0.26300, x64). claude 2.1.330(네이티브 설치판, `%USERPROFILE%\.local\bin\claude.exe`). 시스템 `powershell.exe` = Windows PowerShell 5.1(`C:\WINDOWS\System32\WindowsPowerShell\v1.0`).
- 실행 = .NET `ProcessStartInfo`(`UseShellExecute=false`, `CreateNoWindow=true`, 표준 출력·오류 넘겨받기, UTF-8 로 읽기). 숨은 창에서 표준 출력·오류·종료 코드가 모두 온다.

### 판

| 명령 | 종료 코드 | 표준 출력 |
|---|---|---|
| `claude --version` | 0 | `2.1.330 (Claude Code)` 한 줄 |

→ 판 = 첫 줄의 `숫자.숫자.숫자`. 계약 `ClaudeInstallation.Version` 에 숫자만 넣는다.

### 로그인 상태 (로그인 안 된 임시 프로필)

| 명령 | 종료 코드 | 표준 출력 |
|---|---|---|
| `claude auth status` | **1** | JSON(아래). 기본이 JSON 이다 |
| `claude auth status --json` | **1** | 같은 JSON |
| `claude auth status --text` | **1** | `Not logged in. Run claude auth login to authenticate.` |

```json
{
  "loggedIn": false,
  "authMethod": "none",
  "apiProvider": "firstParty",
  "analyticsDisabled": false,
  "projectsDirectory": "C:\\…\\cfg\\projects",
  "configDirectory": "C:\\…\\cfg",
  "loginExpiresAt": null,
  "consoleProfileSignInOffered": true
}
```

- 표준 오류는 비어 있다. 글은 영어뿐이라 콘솔 코드 페이지(949)와 상관없다(UTF-8 로 읽어도 깨지지 않음).
- 판단 = JSON `loggedIn`. 로그인된 쪽의 종료 코드(0 으로 보임)는 재지 않았다 → 종료 코드는 보조로만 쓴다. `auth` 하위 명령이 없는 옛 판은 「상태 확인 미지원」(`ClaudeLoginStatus.StatusSupported = false`)으로 본다.
- JSON 에 설정 폴더 경로가 들어 있다 → 진단 글·로그에 원문을 넣지 않는다(계약 그대로, `Detail` 금지).
- 로그인된 쪽의 칸(계정 표시 글)은 이 PC 에서 재지 않았다(실제 프로필을 쓰지 않음) → Linux 절(L1).
- 상태 확인만 해도 `CLAUDE_CONFIG_DIR` 에 `.claude.json`·`backups/` 가 생기고, 가짜 `USERPROFILE` 아래 `AppData\Roaming` 이 생긴다(시험은 임시 폴더로).

### 로그인 시작 (도움말만)

`claude auth login [--claudeai(기본)|--console|--sso|--email <email>]`. 화면 없이 띄웠을 때 주소를 찍는지는 재지 않았다: Windows 에서는 claude 가 브라우저를 직접 열 수 있어 「주소를 열지 않음」 규칙에 걸린다 → Linux 절(L1)을 따른다.

### 숨은 창의 PowerShell 출력과 한글

| 명령(숨은 창) | 결과 |
|---|---|
| `powershell -NoProfile -ExecutionPolicy Bypass -Command "Write-Output 'line1'; Write-Output '한글 줄'; Write-Error 'err1'; exit 3"` | 종료 코드 3, 표준 출력 `line1` + **깨진 한글**, 표준 오류에 Write-Error 글(여러 줄 꼴) |
| 같은 명령 앞에 `[Console]::OutputEncoding=[Text.Encoding]::UTF8;` | 한글이 바르게 옴 |

→ 설치 실행기는 명령 앞에 `[Console]::OutputEncoding=[Text.Encoding]::UTF8;` 를 붙이고 출력을 UTF-8 로 읽는다(설치 끝 글의 ✅ 도 같은 까닭). Windows PowerShell 5.1 의 기본 출력은 OEM 코드 페이지(949)다.

### 설치 스크립트 (`irm https://claude.ai/install.ps1` 내용만 읽음, 실행 안 함)

- 하는 일: 64비트 확인 → `https://downloads.claude.ai/claude-code-releases/latest` 로 판 읽기 → `manifest.json` 의 SHA256 → `%USERPROFILE%\.claude\downloads\claude-<판>-win32-x64.exe` 로 받기(ARM64 는 `win32-arm64`) → 해시 검사 → `claude.exe install latest` 실행(실행기·셸 연결 설치) → 받은 파일 지움.
- **진행 막대 없음**: `$ProgressPreference = 'SilentlyContinue'`. 받는 동안 출력이 없다(받는 파일 크기는 이 PC 의 설치판 `claude.exe` 2.1.330 이 약 388MB — 같은 판일 때의 어림) → 화면은 「끝을 모르는 진행 막대」 + 로그.
- 표준 출력 줄(스크립트 글에서 읽음): `Setting up Claude Code...` → (`claude install` 의 출력 — 실행하지 않아 모양은 재지 않음) → 빈 줄 → `✅ Installation complete!` → 빈 줄.
- 실패: `Write-Error "…"` 뒤 `exit 1`(또는 `claude install` 의 종료 코드). 글 예: `Failed to get latest version: …`, `Failed to get a valid version from downloads.claude.ai …`(지역 차단·망), `Failed to download binary: …`, `Checksum verification failed`, `Claude Code does not support 32-bit Windows.`. `-Command` 안의 `iex` 가 `exit` 하면 powershell 프로세스 종료 코드가 된다.
- 실패 종류 가름(L2 구현 참고): `Failed to get`·`Failed to download`·`downloads.claude.ai` → `Network`, 그 밖 0 아님 → `Unknown`, 0 이면 다시 찾아서 있으면 성공.
- 설치 위치 = `%USERPROFILE%\.local\bin\claude.exe`(이 PC 의 실제 설치판 위치와 같음). 이 PC 의 사용자 PATH 에는 `%USERPROFILE%\.local\bin` 이 들어 있다(설치기가 넣은 것으로 보임, 설치를 다시 돌려 확인하지는 않음).

### 설치 뒤 다시 찾기 (PATH 가 옛 값일 때)

- 앱 프로세스의 PATH 는 앱을 띄울 때 값이라 설치 뒤에도 `.local\bin` 이 없을 수 있다. `ClaudeExecutable.Find` 는 PATH 다음에 `KnownDirectories(home)` 를 보고, 첫 줄이 `<home>\.local\bin` 이라 `claude.exe` 를 찾는다. `home` = `Environment.GetFolderPath(UserProfile)`(환경 변수가 아니라 셸 폴더라 PATH·USERPROFILE 변경과 무관).
- 이미 있는 시험: `CliDriverTests.Claude_outside_path_is_found_in_a_usual_install_folder`(PATH = `C:\Windows` 만인데 `<home>\.local\bin\claude.exe` 를 찾음).

### 계약에 미치는 것

- 계약 고침 없음. `IClaudeSetupService` 결과 모양 그대로 쓸 수 있다(판 = `--version` 첫 줄 숫자, 상태 = `auth status --json` 의 `loggedIn`, `StatusSupported` = 2.1.330 에서 참, 설치 = 숨은 창 + 줄 단위 출력 + 끝난 뒤 다시 찾기).

## Linux

### 조건

- PC: Ubuntu(커널 6.8), 화면 없음(`DISPLAY`·`WAYLAND_DISPLAY` 없앰), claude **2.1.330**(네이티브 설치판, `~/.local/bin/claude` → `~/.local/share/claude/versions/2.1.330` 심볼릭 링크)
- 사용자 실제 HOME·`~/.claude` 를 건드리지 않음: 임시 폴더 `T` 를 만들고
  `HOME=$T CLAUDE_CONFIG_DIR=$T/.claude ANTHROPIC_API_KEY= ANTHROPIC_AUTH_TOKEN=` (+ `CLAUDECODE`·`CLAUDE_CODE_ENTRYPOINT`·`CLAUDE_CODE_OAUTH_TOKEN` 제거)로만 실행
- 실제 로그인 안 함(주소를 열지 않음, 몇 초 뒤 끊음). 설치 명령 실행 안 함(스크립트는 파일로 받아 읽기만)
- 브라우저 호출 여부 = `BROWSER` 를 「불렸다는 표시만 남기고 실패하는」 가짜 스크립트로 바꿔 확인

### 1. 판·로그인 상태

| 명령 | 출력 | 종료 코드 |
|---|---|---|
| `claude --version` | `2.1.330 (Claude Code)` (한 줄, `<판> (Claude Code)`) | 0 |
| `claude auth status` (기본 = JSON) | 아래 JSON | **미로그인 1** / 로그인 0 |
| `claude auth status --json` | 기본과 같음 | 같음 |
| `claude auth status --text` | `Not logged in. Run claude auth login to authenticate.` | 1 |

미로그인 JSON(경로는 `$T` 로 줄임):

```json
{
  "loggedIn": false,
  "authMethod": "none",
  "apiProvider": "firstParty",
  "analyticsDisabled": false,
  "projectsDirectory": "$T/.claude/projects",
  "configDirectory": "$T/.claude",
  "loginExpiresAt": null,
  "consoleProfileSignInOffered": true
}
```

로그인된 쪽 모양(로그인 없이 바이너리 문자열에서 확인 — 실제 값은 못 봄):

- 종료 코드 = `process.exit(loggedIn ? 0 : 1)`
- `authMethod` 값: `none`·`claude.ai`(구독 로그인)·`oauth_token`·`api_key`·`api_key_helper`·`third_party`
- `authMethod == "claude.ai"` 일 때 덧붙는 칸: `email`·`orgId`·`orgName`·`subscriptionType`·`storedClaudeAiLogin`(true)·`planTier`
- Console 로그인 쪽도 `email`·`orgId`·`orgName` 이 붙음. 조직 로그인이 설정돼 있으면 `organizationSignInOrgId`
- 칸은 판마다 늘어남(`consoleProfileSignInOffered` 처럼) → **모르는 칸은 무시하고 `loggedIn`(bool) 만 판단에 씀**. 화면에 계정을 보이려면 `email` 이 있을 때만(없으면 「로그인됨」 만)

### 2. `claude auth login` (화면·TTY 없음, 표준 입력 닫음, `timeout 15`)

표준 출력(표준 오류는 비어 있음, 주소의 고유 값은 `…`):

```
Opening browser to sign in…
If the browser didn't open, visit:
ESC]8;;https://claude.ai/oauth/authorize?…BEL https://claude.ai/oauth/authorize?…ESC]8;;BEL
Paste code here if prompted > 
```

- 주소는 **표준 출력 3번째 줄**. 터미널 링크 꾸밈(OSC 8: `ESC ] 8 ; ; <주소> BEL <보이는 글> ESC ] 8 ; ; BEL`)으로 감싸여 같은 주소가 두 번 나옴 → 읽는 쪽은 `https://` 로 시작해 `BEL`(0x07)·`ESC`·공백 전까지를 잘라 씀. 마지막 줄 `Paste code here if prompted > ` 는 줄바꿈이 없음(줄 단위로 읽으면 끝에 가서야 나옴 — 주소 줄은 그 전에 옴)
- 갈래별 주소 모양:
  - 기본·`--claudeai`: `https://claude.ai/oauth/authorize?<짧은 불투명 값>` (쿼리가 한 덩어리)
  - `--sso`: `https://claude.ai/oauth/authorize?login_method=…`
  - `--console`: `https://platform.claude.com/oauth/authorize?code=…&client_id=…&response_type=…&redirect_uri=…&scope=…&code_challenge=…&code_challenge_method=…&state=…` (`redirect_uri` = `https://platform.claude.com/oauth/code/callback`)
- **브라우저를 스스로 엶**: 네 갈래 모두 시작 직후 `BROWSER` 를 1번 부름. `BROWSER` 가 없으면 `xdg-open` 을 쓰는 것으로 보임(화면 없는 PC 에선 실패해도 조용히 넘어가고 같은 출력)
- **이 PC 안에서 끝나는 길이 있음**: 시작하면 `127.0.0.1:<임의 포트>` 에서 듣기 시작함(브라우저가 로그인 뒤 이 주소로 돌아오면 붙여 넣기 없이 끝남). 다른 PC 의 브라우저에서 열면 화면에 코드가 나오고 `Paste code here` 에 붙여 넣는 길
- 붙여 넣기 대기: 표준 입력이 닫혀도(EOF) 끝나지 않고 계속 기다림 → 15초 시간 초과(종료 124)로 끊김. 잘못된 모양의 글을 넣으면 표준 오류에 `That doesn't look like the full code. Copy all of it, including the # in the middle.` 를 찍고 계속 기다림
- 끊기: `SIGTERM` 에 0.3초 안에 끝남(종료 143), 남는 자식 프로세스·듣는 포트 없음
- 「로그인이 끝났다」 는 표준 출력으로 기다리지 않고 `claude auth status` 를 몇 초마다 다시 읽는 쪽이 확실함(끝난 뒤 출력은 로그인 없이 확인 못 함)

### 3. 그 밖의 하위 명령 (도움말만)

- `claude setup-token` — 「Set up a long-lived authentication token (requires Claude subscription)」. 오래 가는 열쇠를 만들어 화면에 보여 주는 길 → 도우미에서는 쓰지 않음(열쇠를 직원이 다루게 됨)
- `claude install [target] [--force]` — 네이티브 판 설치(`stable`·`latest`·판 번호). 설치 스크립트가 내려받은 뒤 이걸 부름
- `claude doctor` — 설치 상태 점검(현재 폴더 설정 파일을 읽음). 진단 글 보조로만 쓸 만함, 필수 아님
- `claude update` — 새 판 확인·설치

### 4. 미로그인 상태의 `claude -p` (Oranda 인자 모양)

`ClaudeCliArguments.Build` 와 같은 모양(`-p --mcp-config <빈 설정> --strict-mcp-config --allowedTools mcp__orandactl --setting-sources project --tools "" --output-format stream-json --input-format stream-json --verbose --disallowedTools WebFetch WebSearch Bash`, 표준 입력 = stream-json 한 줄 「OK 라고만 답하세요」):

- 0.7초 만에 **종료 1**, 표준 오류 비어 있음, 네트워크 요청 없이 끝남(`duration_api_ms: 0`)
- 줄 3개:
  1. `{"type":"system","subtype":"init",…,"apiKeySource":"none",…}` — 시작 정보는 평소처럼 옴
  2. `{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Not logged in · Please run /login"}],…},"error":"authentication_failed","is_api_error_message":true,…}`
  3. `{"type":"result","subtype":"success","is_error":true,"terminal_reason":"api_error","api_error_status":null,"result":"Not logged in · Please run /login",…}`
- `--output-format text` = 표준 출력 `Not logged in · Please run /login`, 종료 1. `json` = 위 3번 줄과 같은 결과 한 줄
- 주의: `result.subtype` 은 `success` 인데 `is_error` 가 true. 지금 파서는 `is_error` 를 보므로 `cli_error` 로 끝남
- **판단 근거(L3)**: `assistant` 줄의 맨 위 칸 `"error":"authentication_failed"` → 오류 코드 `cli_not_logged_in`. 글(`Not logged in`)은 판·언어가 바뀌면 달라질 수 있어 보조로만(`result` 글이 `Not logged in` 으로 시작할 때도 같은 코드)

### 5. 설치 스크립트 (`curl -fsSL https://claude.ai/install.sh`, 파일로 받아 읽기만)

- 261줄 bash. 인자 `[stable|latest|VERSION]`. `sudo` 로 돌리면 거절(종료 1)
- 진행: `$HOME/.claude/downloads` 에 받아 체크섬 확인 → 표준 출력 `Setting up Claude Code...` → `claude install` 실행 → 성공 시 빈 줄·`✅ Installation complete!`·빈 줄, 종료 0
- 실패 글(모두 표준 오류, 종료 1): `Either curl or wget is required but neither is installed`, `Unsupported operating system: …`, `Unsupported architecture: …`, `Failed to get a valid version from downloads.claude.ai (got unexpected content).`(+ 지역 안내 줄), `Platform … not found in manifest`, `Download failed`, `Checksum verification failed`. `claude install` 이 시그널로 죽으면 `Installation was killed before it could finish (exit code N).`(137 = 메모리 부족 안내 한 줄 더)
- 설치 위치 = `~/.local/bin/claude`(기존 `ClaudeExecutable.KnownDirectories` 에 있음) → 설치 직후 PATH 가 옛 값이어도 찾힘
- 진행 표시는 줄 몇 개뿐(내려받기 퍼센트 없음) → 진행 막대는 「끝날 때까지 움직이는 막대」 + 받은 줄 로그가 맞음

### 결론

- **자동 확인 가능.** 로그인 시작 = `claude auth login` 을 뒤에서 띄우고 표준 출력에서 주소를 읽음(복사용). 브라우저는 claude 가 스스로 엶. 끝남 확인 = `claude auth status --json` 을 몇 초마다 읽어 `loggedIn == true`(종료 0). 「로그인 끝났어요」 단추는 예비로 둠(자동 확인 실패·다른 PC 브라우저 대비)
- 끊기 = 프로세스 트리 끝냄(SIGTERM 으로 깔끔히 끝남). 표준 입력이 닫혀도 스스로 끝나지 않으므로 도우미가 단계를 떠날 때 반드시 끊어야 함
- 미로그인 연결 시험 = `assistant` 줄 `error: "authentication_failed"` 로 바로 구분됨 → `cli_not_logged_in`
- 다른 PC 브라우저에서 로그인하는 경우(붙여 넣기 코드)는 도우미 범위 밖으로 둠: 같은 PC 브라우저가 기본
