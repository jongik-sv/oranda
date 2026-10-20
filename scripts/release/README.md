# 배포 스크립트

| 파일 | 하는 일 | 사람 자원 |
|---|---|---|
| `pack-windows.ps1` | Velopack 으로 Windows 설치 파일·델타 생성, 크기 검사 | Authenticode 인증서(`ORANDA_SIGN_PARAMS`) |
| `check-installer-size.ps1` | 설치 파일 80 MB 이하 검사 | 없음 |
| macOS | `.github/workflows/ci.yml` macos 작업(서명·공증·pkg) | Apple 비밀값 |
| 피드 게시 | `artifacts/release/` 를 사내 정적 파일 서버에 복사 | 서버 주소·권한 |

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

composite 는 입력 가능까지 약 −0.6 s(`spikes/TSK-01-04/RESULT.md`, 1670 ms 대 2276 ms)의 대가로 설치 파일을 30 MB 키운다. 80 MB 목표와 함께 둘 수 없으므로 어느 쪽을 바꿀지 사람이 정한다.
