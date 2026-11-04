---
title: 사전 조건
description: 설치 전에 확인할 운영체제, 설치 파일, 접속 조건을 안내합니다.
---

설치하기 전에 아래 조건을 확인하세요.

| 항목 | Windows | macOS |
|---|---|---|
| 지원 운영체제 | Windows 10/11 x64 | macOS 14.7 이상, Apple Silicon |
| 설치 파일 | `Oranda-win-Setup.exe` 또는 `Oranda-win-Portable.zip` | `Oranda-osx-Setup.pkg` |
| 받는 곳 | [GitHub Releases](https://github.com/jongik-sv/oranda/releases) | 같음 |
| 파일 크기 | 100 MB 이하 | 100 MB 이하 |

## Oracle Client

Oracle Client는 설치하지 않아도 됩니다. Oranda는 Oracle 접속에 필요한 드라이버를 설치 파일에 포함합니다.

## 인터넷 연결

설치 파일은 인터넷 없이도 설치됩니다. 폐쇄망에서는 설치 파일만 옮기면 됩니다. 자동 업데이트는 업데이트 서버에 닿아야 동작합니다. 업데이트 서버를 바꾸는 방법은 [업데이트와 릴리스 노트](../update/)에서 설명합니다.

## 접속할 데이터베이스

Oranda로 접속하려면 아래 정보가 필요합니다.

- 호스트 이름 또는 IP 주소
- 포트 번호
- 서비스 이름
- 사용자 이름과 비밀번호

지원하는 서버 버전의 범위는 도움말 메뉴의 **Oranda 정보**에서 확인합니다.

## 다음 단계

- Windows 사용자는 [Windows 설치](../windows/)로 이동하세요.
- macOS 사용자는 [macOS 설치](../macos/)로 이동하세요.
