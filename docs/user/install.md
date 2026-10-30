# 설치

[English](install.en.md)

| 항목 | Windows | macOS |
|---|---|---|
| 지원 | Windows 10/11 x64 | macOS 14.7 이상, Apple Silicon |
| 설치 파일 | `Oranda-win-Setup.exe` (또는 `Oranda-win-Portable.zip`) | `Oranda-osx-Setup.pkg` |
| 받는 곳 | [GitHub Releases](https://github.com/jongik-sv/oranda/releases) | 같음 |
| 크기 | 100 MB 이하 | 100 MB 이하 |

Oracle Client 는 설치하지 않아도 된다. 설치 파일은 인터넷 없이도 설치된다(폐쇄망은 파일만 옮기면 된다).

## Windows

### 설치
1. `Oranda-win-Setup.exe` 를 실행한다. 관리자 권한 없이 내 계정에 설치된다(`%LocalAppData%\Oranda`). 바탕 화면과 시작 메뉴에 바로 가기가 생긴다.
2. 지금 Windows 빌드는 코드 서명이 없다(SignPath Foundation 서명 신청 중). 그래서 SmartScreen 이 「Windows의 PC 보호」 창을 띄운다.
   - 창의 **추가 정보** 를 누르고, 아래에 생기는 **실행** 단추를 누른다.
   - 브라우저가 내려받기를 막으면(「일반적으로 다운로드되지 않음」) 내려받기 목록에서 **…→ 유지 → 그래도 계속** 을 고른다.
3. 처음 실행하면 접속 관리자가 열린다. 다음 문서 [connect.md](connect.md).

### 설치 없이 쓰기(Portable)
1. `Oranda-win-Portable.zip` 을 받은 뒤, 풀기 전에 PowerShell 에서 인터넷 표시를 지운다: `Unblock-File .\Oranda-win-Portable.zip`
2. 원하는 폴더에 풀고 `Oranda.exe` 를 실행한다. Portable 도 같은 방식으로 자동 업데이트된다.

### 실행이 막힐 때
- **스마트 앱 컨트롤**·WDAC·AppLocker 가 켜진 PC 는 서명 없는 프로그램을 막는다. 「추가 정보」가 보이지 않는다. 서명된 릴리스가 나올 때까지는 그 PC 의 정책이 허용해야 한다(Windows 보안 > 앱 및 브라우저 컨트롤 > 스마트 앱 컨트롤).
- 조직 PC 는 보안 프로그램이 막을 수 있다. 관리자에게 문의한다.
- 설치 기록: `%LocalAppData%\velopack\velopack.log`.

## macOS

### 설치
1. `Oranda-osx-Setup.pkg` 를 받는다.
2. 서명·공증된 릴리스면 두 번 눌러 안내를 따른다.
3. 서명 없는 릴리스면 macOS 가 「확인되지 않은 개발자」 또는 「악성 코드가 없는지 확인할 수 없습니다」 로 막는다.
   - macOS 14: Finder 에서 `.pkg` 를 **우클릭(Control-클릭) → 열기** → 다시 **열기**.
   - macOS 15 이상(우클릭 열기가 없어짐): 한 번 열어 막힌 뒤 **시스템 설정 > 개인정보 보호 및 보안** 맨 아래의 **그래도 열기** 를 누르고 암호를 넣는다.
   - 또는 터미널에서 격리 표시를 지운 뒤 연다: `xattr -d com.apple.quarantine ~/Downloads/Oranda-osx-Setup.pkg`
4. 설치가 끝나면 응용 프로그램 폴더의 Oranda 를 연다. 그래도 막히면 `xattr -dr com.apple.quarantine /Applications/Oranda.app`.

## 자동 업데이트
- 설치본은 시작 20초 뒤, 하루에 한 번 GitHub Releases 에 새 버전이 있는지 확인하고 백그라운드로 내려받는다. 도움말 > 업데이트 확인 으로 바로 확인할 수도 있다.
- 새 버전은 **앱을 끝낼 때** 적용된다(또는 「지금 다시 시작해 적용」). 저장 안 한 탭·커밋 안 한 트랜잭션이 있으면 먼저 묻는다.
- 끄기: **설정 > 업데이트** 에서 시작 때 확인·자동 내려받기를 끈다. 조직 내부 피드를 쓰려면 같은 곳의 「업데이트 서버 주소」 에 넣는다.
- 업데이트 파일은 릴리스 메타데이터의 해시로 확인한다. Windows 업데이트도 첫 설치와 같이 서명 없이 받는다(서명 승인 전).

## 제거
- **Windows**: 설정 > 앱 > 설치된 앱 > Oranda > **제거**. Portable 은 폴더를 지운다.
- **macOS**: 응용 프로그램 폴더의 Oranda 를 휴지통으로 옮긴다.
- 설정·접속 목록·작업 공간은 제거해도 남는다. 모두 지우려면 아래 폴더도 지운다.
  - Windows: `%APPDATA%\Oranda`
  - macOS: `~/Library/Application Support/Oranda`, 키체인 접근에서 이름이 `Oranda` 인 항목(저장한 접속 비밀번호)

## 오류 보고
- 프로그램이 멈추면 오류 내용이 로그 폴더에 저장된다(도움말 > 로그 폴더 열기).
- 도움말 > 오류 보고서 에서 내용을 확인하고, 보내도 좋을 때만 「전송」을 누른다. 누르지 않으면 아무것도 나가지 않는다.
- 접속 비밀번호 같은 값은 보고서에서 `***` 로 가려진다.
