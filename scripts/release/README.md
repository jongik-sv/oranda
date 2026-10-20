# 배포 스크립트

| 파일 | 하는 일 | 사람 자원 |
|---|---|---|
| `pack-windows.ps1` | Velopack 으로 Windows 설치 파일·델타 생성, 크기 검사 | Authenticode 인증서(`ORANDA_SIGN_PARAMS`) |
| `check-installer-size.ps1` | 설치 파일 80 MB 이하 검사 | 없음 |
| macOS | `.github/workflows/ci.yml` macos 작업(서명·공증·pkg) | Apple 비밀값 |
| 피드 게시 | `artifacts/release/` 를 사내 정적 파일 서버에 복사 | 서버 주소·권한 |
| 업데이트 확인 | 앱이 피드(설정 > 업데이트의 주소, 비면 빌드 기본값)에서 `releases.*.json`·`.nupkg` 를 읽는다 | 피드 위치 결정 |

폐쇄망용 오프라인 설치 파일은 `Setup.exe`·`.pkg` 자체다(설치 중 네트워크 불필요).
Velopack 은 릴리스 메타데이터의 해시로 패키지 무결성을 확인한다. 서명 확인 실패 시 설치를 거부하는 동작은 서명한 실제 빌드로 확인해야 한다.

- 앱의 `Program.Main` 첫 줄이 `VelopackApp.Build().Run()` 이다. vpk pack 은 실행 파일의 진입점에서 이 호출을 찾지 못하면 멈춘다. Velopack 패키지 버전(`Directory.Packages.props`)은 vpk 버전(CI 의 `VPK_VERSION`)과 같아야 한다.
- 설치하지 않고 실행하면 `Run()` 은 바로 돌아온다. 다만 Velopack 진단 로그 몇 줄을 남긴다(Windows `%LocalAppData%\velopack\velopack.log`, macOS `~/Library/Logs/velopack.log`).
- 게시본에는 PDB 가 없다(`Oranda.App.csproj` 의 `ExcludePdbFromPublish`). vpk 도 기본으로 `*.pdb` 를 뺀다.

## 크기 (TSK-10-06)

맥에서 미리 볼 수 있다: 게시 뒤 `vpk "[win]" pack ... --runtime win-x64` 는 서명 없이 Windows `Setup.exe` 를 만든다. 정식 확인은 Windows 에서 `pack-windows.ps1` 로 한다.

2026-10-20 맥북 측정, 무서명. 80 MB = 83,886,080 bytes.

| 설치 파일 | 크기 | 판정 |
|---|---|---|
| Windows `Oranda-win-Setup.exe`, ReadyToRun composite (현재 설정) | 96,805,788 bytes (92.3 MB) | 초과 |
| Windows 같은 게시, composite 끔 (`-p:PublishReadyToRunComposite=false`) | 66,478,983 bytes (63.4 MB) | 이하 |
| macOS `Oranda-osx-Setup.pkg`, 일반 ReadyToRun (CI 와 같은 게시) | 69,514,156 bytes (66.3 MB) | 이하 |

composite 는 입력 가능까지 약 −0.6 s(`spikes/TSK-01-04/RESULT.md`, 1670 ms 대 2276 ms)의 대가로 설치 파일을 30 MB 키운다. 결정 D-025(2026-10-20): 시작 시간 이득이 기준(0.3 s)보다 커서 composite 는 유지하고, 80 MB 는 다른 수단(트리밍, composite 범위 축소, 불필요한 파일 정리)으로 줄인다. 후보는 docs/wbs.md TSK-10-06 note 에 있다.

## 앱의 자동 업데이트 (TSK-10-01)

- 앱은 설치본(Setup.exe·.pkg 로 설치)에서만 업데이트를 확인한다. `dotnet run`·`bin/Release` 직접 실행은 「개발 실행본」이라 안내만 나온다.
- 피드 주소: 설정 > 업데이트의 「업데이트 서버 주소」(http(s) 주소, 폴더·UNC 경로, `https://github.com/<owner>/<repo>`). 비워 두면 빌드 때 넣은 기본값을 쓴다: `dotnet publish … -p:OrandaUpdateFeed=<주소>`. 둘 다 비면 「업데이트 서버가 설정되지 않았습니다」.
- 버전: CI 가 같은 값을 `dotnet publish -p:Version=0.1.<run_number>` 와 `vpk pack --packVersion 0.1.<run_number>` 에 넘긴다. 정보 창·업데이트 창은 설치본이면 Velopack 설치 버전을 보인다.
- 적용: 새 버전은 시작 20초 뒤 24시간에 한 번 확인해 백그라운드로 받는다. 적용은 앱을 끝낼 때(기본) 또는 「지금 다시 시작해 적용」이며, 둘 다 저장 안 한 탭·미커밋 트랜잭션 확인을 통과한 뒤에만 갱신기를 예약한다. 시작 때 받아 둔 업데이트를 자동 적용하는 Velopack 기본값은 껐다(`SetAutoApplyOnStartup(false)`). 끝내지 못한 업데이트는 다음 시작 때 상태 표시줄 「업데이트 x.y.z 준비됨」으로 보인다.
- 로컬 폴더 피드로 확인하는 방법: 서로 다른 두 버전을 같은 `--outputDir` 로 `vpk pack` → 낮은 버전을 설치 → 설정 > 업데이트에 그 폴더 경로를 넣고 도움말 > 업데이트 확인….
