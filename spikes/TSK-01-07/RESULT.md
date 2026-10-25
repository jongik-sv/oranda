# TSK-01-07 결과 (골격)

## 산출물
- `.github/workflows/ci.yml`: windows(빌드·단위/UI·게시·시작 측정·artifact), macos-14(빌드·단위/UI·게시·vpk 패키징·조건부 서명/공증·크기 검사·artifact), integration(ubuntu, `Category=Integration`).
- `tests/Oranda.Perf`: 시작 측정 콘솔(외부 패키지 없음). `--exe <게시본> --runs 5 --out <dir> [--budget-ms 1000] [--enforce]` -> `perf-startup.json`·`perf-startup.md`.
- 실제 실행 검증은 미수행: App 계측 훅 미구현, 러너 미연결(골격까지).

## 비밀값 이름 (GitHub Actions secrets)
서명·공증은 `APPLE_CERTIFICATE_P12_BASE64` 가 비어 있으면 건너뛰고 무서명으로 패키징한다.
| 이름 | 용도 |
|---|---|
| APPLE_CERTIFICATE_P12_BASE64 | Developer ID 인증서(.p12) base64. 존재 여부가 서명 단계 스위치 |
| APPLE_CERTIFICATE_PASSWORD | .p12 암호 |
| APPLE_KEYCHAIN_PASSWORD | 임시 키체인 암호(임의 값) |
| APPLE_SIGN_APP_IDENTITY | `Developer ID Application: ... (TEAMID)` |
| APPLE_SIGN_INSTALL_IDENTITY | `Developer ID Installer: ... (TEAMID)` (.pkg) |
| APPLE_ID | 공증용 Apple ID |
| APPLE_TEAM_ID | 팀 ID |
| APPLE_APP_PASSWORD | 앱 전용 암호(notarytool) |
Windows Authenticode 는 이번 골격 범위 밖(코드 서명 인증서 확정 후 추가).

## App 계측 훅 규약 (제안)
환경 변수 `ORANDA_PERF=1` 일 때 App 이 stdout 에 한 줄씩 출력:
`ORANDA_PERF stage=<appbuilder|window-created|first-render> ms=<Process.GetCurrentProcess().StartTime 기준 경과 ms>`
- appbuilder: `BuildAvaloniaApp()` 반환 직후(Program.Main)
- window-created: MainWindow 생성자 끝 또는 `Opened`
- first-render: 첫 `TopLevel.RequestAnimationFrame`/`Renderer` 첫 프레임 완료 시점
WinExe 는 stdout 이 없을 수 있으므로 Windows 에서는 `AttachConsole` 대신 `ORANDA_PERF_FILE` 경로 방식도 가능(Perf 도구 수정 필요).

## 미결
- Windows 게시 stdout: WinExe 는 리다이렉트 파이프에 쓰기가 가능하나 콘솔 부착 없이 `Console.Out` 이 막힐 수 있음. 훅 구현 시 확인.
- Perf 예산 강제(`--enforce`)는 예산 확정 후 활성화(현 측정 1.8~1.9 s > 1000 ms).
- macOS 성능 측정은 TRD 상 주 1회(스케줄 워크플로 추후).
- `tests/Oranda.Perf` 는 sln 미포함; 조정자가 머지 때 추가. CI 는 `dotnet run --project` 로 직접 호출.
