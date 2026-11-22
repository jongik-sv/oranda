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
