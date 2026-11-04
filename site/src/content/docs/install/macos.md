---
title: macOS 설치
description: macOS에서 Oranda를 설치하고 보안 경고를 처리하는 방법을 안내합니다.
sidebar:
  order: 3
---

## 설치하기

1. [GitHub Releases](https://github.com/jongik-sv/oranda/releases)에서 `Oranda-osx-Setup.pkg`를 받습니다.
2. 서명과 공증이 끝난 릴리스이면 파일을 두 번 눌러 안내를 따릅니다.
3. 설치가 끝나면 응용 프로그램 폴더의 Oranda를 엽니다.
4. 이어서 [첫 접속](../../connect/first-connection/)을 따라 하세요.

## 서명 없는 릴리스를 열 때

서명 없는 릴리스는 macOS가 「확인되지 않은 개발자」 또는 「악성 코드가 없는지 확인할 수 없습니다」라는 메시지로 막습니다. 운영체제 버전에 맞는 방법을 쓰세요.

- **macOS 14**: Finder에서 `.pkg` 파일을 우클릭(또는 Control-클릭)하고 **열기**를 고릅니다. 다시 나타나는 창에서 **열기**를 누릅니다.
- **macOS 15 이상**: 우클릭 열기가 없어졌습니다. 파일을 한 번 열어 막힌 뒤 **시스템 설정 → 개인정보 보호 및 보안** 맨 아래의 **그래도 열기**를 누르고 암호를 입력합니다.
- **터미널**: 격리 표시를 지운 뒤 엽니다.

  ```bash
  xattr -d com.apple.quarantine ~/Downloads/Oranda-osx-Setup.pkg
  ```

설치 뒤에도 앱이 막히면 아래 명령으로 앱의 격리 표시를 지웁니다.

```bash
xattr -dr com.apple.quarantine /Applications/Oranda.app
```

## 제거하기

응용 프로그램 폴더의 Oranda를 휴지통으로 옮깁니다.

제거해도 설정, 접속 목록, 작업 공간은 남습니다. 모두 지우려면 아래 항목도 지우세요.

- `~/Library/Application Support/Oranda` 폴더
- 키체인 접근에서 이름이 `Oranda`인 항목 (저장한 접속 비밀번호)
