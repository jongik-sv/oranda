# 릴리스 내는 법

업데이트 피드 = 이 저장소의 GitHub Releases(`https://github.com/jongik-sv/oranda`, 공개). CI(`.github/workflows/ci.yml`)가 릴리스 빌드에 이 주소를 기본값(`OrandaUpdateFeed`)으로 넣으므로, 설치본은 설정의 피드 주소가 비어 있으면 여기서 새 버전을 찾는다. 로컬 빌드는 값이 없어 「피드 없음」이다. 앱은 GitHub API(인증 없이 IP 당 시간 60회)를 쓰지 않고 `…/releases/latest/download/releases.{win|osx}.json` 과 같은 경로의 nupkg 를 받으므로(D-047), 채널 파일과 full·delta 패키지는 반드시 같은 최신 정식 릴리스에 함께 올라가야 한다.

한 릴리스에 두 채널이 함께 올라간다(Velopack 채널 이름 = 파일 이름의 `win`·`osx`).

| 채널 | 만드는 작업 | 올라가는 파일 |
|---|---|---|
| `win` | `windows` (windows-latest) | `Oranda-win-Setup.exe`, `Oranda-win-Portable.zip`, `Oranda-<버전>-full.nupkg`(+ 이전 릴리스가 있으면 `-delta.nupkg`), `releases.win.json`, `RELEASES`, `assets.win.json` |
| `osx` | `macos` (macos-14, arm64) | `Oranda-osx-Setup.pkg`, `Oranda-osx-Portable.zip`, `Oranda-<버전>-osx-full.nupkg`(+ delta), `releases.osx.json`, `assets.osx.json` |

Windows 줄은 2026-10-25 로컬 `vpk pack --channel win` 결과다. macOS 줄의 nupkg 이름(채널 접미사 `-osx`, Windows 기본 채널만 접미사 없음)은 Velopack 명명 규칙에서 옮긴 것으로 아직 맥 실행으로 확인하지 않았다. 두 채널의 full nupkg 이름이 같으면 한 릴리스에 올릴 때 겹치므로 첫 릴리스에서 확인한다.

두 패키지 모두 설치 폴더의 `licenses/` 에 `LICENSE.txt`·`THIRD-PARTY-NOTICES.md`·`SkiaSharp-THIRD-PARTY-NOTICES.txt` 를 넣는다(`scripts/release/copy-notices.ps1`). Oracle 드라이버 라이선스가 배포본마다 사본을 요구하기 때문이다(THIRD-PARTY-NOTICES.md 부록 A).

## 태그 푸시로 내기

1. 올릴 커밋에서 태그를 만든다. 태그 이름이 패키지 버전이 된다(`vX.Y.Z` → `X.Y.Z`, 시험판은 `vX.Y.Z-beta.1` 형식).
   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```
2. `ci` 워크플로의 `windows`·`macos` 작업이 각자 패키지를 만든다(아래 「서명 상태별 동작」). 두 작업이 같은 규칙으로 버전을 정하고, `release` 작업이 두 버전이 같은지 확인한 뒤 `vpk upload github --channel win` → `--channel osx` 순서로 GitHub Release `Oranda 0.2.0` 에 올린다(`--publish --merge`: 두 번째 채널은 같은 릴리스에 더해진다). 이전 릴리스가 있으면 각 작업이 먼저 받아 델타 패키지도 만든다.
3. 설치본은 시작 20초 뒤(24시간마다) 또는 도움말 > 업데이트 확인에서 새 버전을 찾아 내려받고, 종료할 때 적용한다.

## 릴리스 노트

- 태그를 붙이기 전에 `docs/release-notes/<버전>.md`(예: `0.1.2.md`)를 커밋한다. 사용자가 읽는 말로 「새 기능」·「고친 것」을 적는다.
- CI 가 그 파일을 `vpk pack --releaseNotes` 로 패키지에 넣는다. 설치본의 업데이트 창(도움말 > 업데이트 확인)이 이 노트를 보여 준다(`ReleaseNotesFormatter`: `#` 머리글, `-` 목록).
- 같은 파일이 앱에도 포함되어(`docs/release-notes/*.md` 가 임베디드 리소스) 업데이트 뒤에도 도움말 > 새 기능(릴리스 노트)에서 오프라인으로 볼 수 있다. 파일을 더하면 빌드만으로 따라 들어간다.
- `release` 작업이 같은 파일로 GitHub Release 본문을 채운다(`gh release edit --notes-file`).
- 파일이 없으면 노트 없이 묶고 태그 빌드에 경고를 남긴다.

`release` 작업은 Linux 러너라 vpk 의 기본 채널이 `linux` 다. 그래서 업로드·다운로드마다 `--channel` 을 꼭 적는다(2026-10-25 이전 워크플로는 채널을 적지 않아 macOS 패키지를 찾지 못했을 것으로 보인다 — 실제 실행 기록은 없음).

## 서명 상태별 동작

### Windows (SignPath Foundation, D-014)

| 조건 | 동작 |
|---|---|
| 비밀값 `SIGNPATH_API_TOKEN`·`SIGNPATH_ORGANIZATION_ID` 없음 (지금, 승인 전) | 서명 없이 패키지. `Signature report` 단계가 무서명 개수를 경고로 남긴다. 받는 사람은 SmartScreen 에서 「추가 정보 → 실행」(docs/user/install.md) |
| 비밀값 있음 + 보통 push·PR | 서명하지 않는다(서명 요청은 사람이 승인해야 하므로 릴리스에만 쓴다). 포크 PR 에는 비밀값이 전달되지 않는다 |
| 비밀값 있음 + 태그 push 또는 수동 실행 | ① 게시 폴더의 `Oranda.*`(Oranda 자신의 exe·dll)만 산출물로 올려 SignPath 에 서명 요청(artifact configuration `app-files`) → 승인되면 서명본을 게시 폴더에 덮어씀 → ② `vpk pack`(서명 매개변수 없음) → ③ `Oranda-win-Setup.exe` 를 다시 서명 요청(artifact configuration `installer`) → 서명본으로 바꿈. 승인을 최대 1시간 기다린다 |

- 액션: `signpath/github-action-submit-signing-request@v3.0`(2026-09-10 태그).
- 저장소 설정: Secrets `SIGNPATH_API_TOKEN`(SignPath CI 사용자 토큰), `SIGNPATH_ORGANIZATION_ID`. Variables(선택) `SIGNPATH_PROJECT_SLUG`(기본 `oranda`), `SIGNPATH_SIGNING_POLICY_SLUG`(기본 `release-signing`).
- SignPath 쪽 준비: 프로젝트 `oranda`, 신뢰 빌드 시스템 = GitHub.com, 원본 확인(origin verification) 켬, artifact configuration 두 개 — `app-files`(zip 안 `Oranda*.exe`·`Oranda*.dll` 의 PE 서명, 제품 이름 `Oranda`·버전 일치 규칙), `installer`(zip 안 `Oranda-win-Setup.exe`).
- 서명하지 않는 것: Avalonia·Dock·SQLitePCLRaw 같은 외부 dll(SignPath OSS 조건: 외부 라이브러리는 그 배포자가 서명), Microsoft·Oracle dll(원래 서명 유지, D-034), Velopack `Update.exe`(외부 바이너리). 그래서 서명본도 무서명 파일이 남는다. 스마트 앱 컨트롤·WDAC 가 켜진 PC 에서 무서명 dll·`Update.exe` 가 막히는지는 서명본이 나온 뒤 확인해야 한다.
- 로컬 Authenticode·Azure 서명은 그대로 `scripts/release/pack-windows.ps1`(scripts/release/README.md 「코드 서명」).

### macOS (Apple Developer ID)

| 조건 | 동작 |
|---|---|
| Apple 비밀값(`APPLE_CERTIFICATE_P12_BASE64` 등) 있음 | 서명·공증한 `.pkg`, Gatekeeper 검사 |
| 없음 | 서명 없이 패키지. 받는 사람은 「우클릭 → 열기」 또는 격리 해제(docs/user/install.md) |

## 수동 실행

Actions > ci > Run workflow(`workflow_dispatch`). 태그가 아니므로 버전은 `0.1.<실행 번호>`, 태그는 `v0.1.<실행 번호>` 로 올라간다.

## 올리지 않는 경우

보통의 push·PR 은 산출물(`release-windows-x64`·`release-macos-arm64`)만 남기고 GitHub Release 는 만들지 않는다. `release` 작업만 `contents: write` 권한을 가지며 토큰은 `GITHUB_TOKEN` 뿐이다.

## 첫 릴리스 전 확인

- 저장소 Settings > Actions > General 의 Workflow permissions 가 읽기 전용이어도 된다(`release` 작업이 자기 권한을 올린다).
- 첫 릴리스에는 받을 이전 릴리스가 없어 `Download previous release` 단계가 「no previous release to download」 를 찍고 넘어간다.
- Windows 설치 파일 크기: 2026-10-25 로컬 무서명 측정 `Oranda-win-Setup.exe` 80,388,692 bytes(licenses 포함, 상한 83,886,080 bytes 까지 약 3.3 MB). 디버거·덤프 전용 런타임 파일(mscordaccore·mscordbi·DiaSymReader·createdump)·독립 GC(clrgc·clrgcexp)·한/영 밖 위성 리소스를 게시본에서 빼 83,939,374 bytes 에서 줄였다(TSK-08-07, `src/Oranda.App/Oranda.App.csproj`). 그래서 설치본 앱에는 디버거를 붙일 수 없다(충돌 보고는 예외 글). 패키지를 더하거나 올리면 크기를 다시 잰다.
- 공개 배포 전에 노출 점검 결과(조직 내부 정보)를 정리해야 한다(TSK-08-02, 결과는 저장소 밖에 둠).
