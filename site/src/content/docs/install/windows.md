---
title: Windows 설치
description: Windows에서 Oranda를 설치하거나 설치 없이 실행하는 방법을 안내합니다.
---

## 설치 파일로 설치하기

1. [GitHub Releases](https://github.com/jongik-sv/oranda/releases)에서 `Oranda-win-Setup.exe`를 받습니다.
2. 받은 파일을 실행합니다. 관리자 권한은 필요하지 않습니다. Oranda는 내 계정의 `%LocalAppData%\Oranda` 폴더에 설치됩니다.
3. 설치가 끝나면 바탕 화면과 시작 메뉴에 바로 가기가 생깁니다.
4. 설치가 끝나면 Oranda를 실행하세요. 이어서 [첫 접속](../../connect/first-connection/)을 따라 하세요.

### SmartScreen 경고

현재 Windows 빌드에는 코드 서명이 없습니다. 그래서 SmartScreen이 「Windows의 PC 보호」 창을 표시합니다.

1. 창에서 **추가 정보**를 누릅니다.
2. 아래에 나타나는 **실행** 단추를 누릅니다.

브라우저가 내려받기를 막으면 내려받기 목록에서 **… → 유지 → 그래도 계속**을 고르세요.

## 설치 없이 쓰기 (Portable)

1. `Oranda-win-Portable.zip`을 받습니다.
2. 압축을 풀기 전에 PowerShell에서 인터넷 표시를 지웁니다.

   ```powershell
   Unblock-File .\Oranda-win-Portable.zip
   ```

3. 원하는 폴더에 압축을 풀고 `Oranda.exe`를 실행합니다.

Portable 버전도 같은 방식으로 자동 업데이트됩니다.

## 실행이 막힐 때

- **스마트 앱 컨트롤**, WDAC, AppLocker가 켜진 PC는 서명 없는 프로그램을 막습니다. 이때는 「추가 정보」가 나타나지 않습니다. 서명된 릴리스가 나오기 전에는 그 PC의 정책이 Oranda를 허용해야 합니다. 설정 위치는 Windows 보안 → 앱 및 브라우저 컨트롤 → 스마트 앱 컨트롤입니다.
- 조직 PC에서는 보안 프로그램이 실행을 막을 수 있습니다. 관리자에게 문의하세요.
- 설치 기록은 `%LocalAppData%\velopack\velopack.log`에 남습니다.

## 제거하기

- 설치 버전: 설정 → 앱 → 설치된 앱 → Oranda → **제거**를 누릅니다.
- Portable 버전: 압축을 푼 폴더를 지웁니다.

제거해도 설정, 접속 목록, 작업 공간은 남습니다. 모두 지우려면 `%APPDATA%\Oranda` 폴더도 지우세요.
