# 배포 스크립트

| 파일 | 하는 일 | 사람 자원 |
|---|---|---|
| `pack-windows.ps1` | Velopack 으로 Windows 설치 파일·델타 생성, 크기 검사 | Authenticode 인증서(`ORANDA_SIGN_PARAMS`) |
| `check-installer-size.ps1` | 설치 파일 80 MB 이하 검사 | 없음 |
| macOS | `.github/workflows/ci.yml` macos 작업(서명·공증·pkg) | Apple 비밀값 |
| 피드 게시 | `artifacts/release/` 를 사내 정적 파일 서버에 복사 | 서버 주소·권한 |

폐쇄망용 오프라인 설치 파일은 `Setup.exe`·`.pkg` 자체다(설치 중 네트워크 불필요).
Velopack 은 릴리스 메타데이터의 해시로 패키지 무결성을 확인한다. 서명 확인 실패 시 설치를 거부하는 동작은 서명한 실제 빌드로 확인해야 한다.
