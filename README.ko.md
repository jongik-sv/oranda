# Oranda

[English](README.md)

Oranda 는 Windows·macOS 용 무료 오픈 소스 Oracle 데이터베이스 클라이언트다. 오래된 *Orange for ORACLE* 5.0 을 대신하려고 만들었다. SQL·PL/SQL 편집, 결과 그리드, 실행 계획, 세션·Lock 모니터, 튜닝 도구를 Oracle Client 설치 없이 쓴다.

> 스크린샷 자리: `docs/images/screenshot-main.png` (추가 예정)

- Oracle Client·Instant Client 가 필요 없다. 관리 코드 ODP.NET Core 드라이버(Thin)로 붙는다.
- Windows x64 와 Apple Silicon Mac 에서 같은 기능.
- Java·Electron 이 아닌 .NET 10 + Avalonia 데스크톱 앱.
- MIT 라이선스, 무료.

## 주요 기능

- **접속** — EZConnect, `tnsnames.ora` 별칭, 호스트·포트·서비스 입력, TLS(TCPS)·Autonomous Database 지갑, 동시 접속 여러 개, 접속별 색·환경 라벨(개발·검증·운영), SQL Developer·`tnsnames.ora`·Orange 접속 목록 가져오기.
- **SQL·PL/SQL 을 실제로 파싱하는 편집기** — 문법 강조, 오류 표시, 문맥 자동완성(테이블·열·패키지 멤버·힌트), 정의로 이동, 포매터, 템플릿, SQL 탭 여러 개를 담는 작업 공간, 비정상 종료 복구.
- **실행과 결과 그리드** — 스크립트·문장 실행, 바인드 변수, 결과 편집, LOB 편집기·행 보기, CSV·Excel 내보내기·가져오기, 플래시백 조회.
- **실행 계획** — 예상 계획, 커서의 실제 계획, 계획 비교, 계획 그래프, SQL Monitor 보고서(Tuning Pack 보유를 선언했을 때).
- **스키마 브라우저·PL/SQL 도구** — 객체 상세·DDL, 컴파일 오류 창이 있는 PL/SQL 편집기, PL/SQL 실행 대화 상자, 무효 객체 컴파일, 휴지통.
- **모니터링** — 세션 모니터, SQL 모니터(Top SQL·대기·AWR 계획 이력), 대기 트리와 블로커 알림이 있는 Lock 모니터.
- **튜닝** — 표·열 통계, 자식 커서, 트레이스 뷰어·TKPROF 요약, 변형 SQL 벤치마크가 있는 튜닝 도우미, HTML 파일 하나로 만드는 튜닝 보고서.
- **MyBatis 작업 공간** — 매퍼 XML 을 열어 파라미터를 넣고, 펼친 SQL 을 보고, 태그 조합을 검사하고, 바꾼 문장만 저장한다.
- **Role 별 메뉴** — **설정 > 기능** 에서 Role(SQL 개발자·PL/SQL 개발자·MyBatis 개발자·DB 튜너·운영 DBA·조회 전용)을 하나 이상 고르면 그 Role 의 메뉴·기능만 보인다. 「조회 전용」 Role 은 DML·DDL·PL/SQL 실행을 막는다. Role 을 고르지 않으면 모든 기능이 켜져 있다.
- **안전장치** — 읽기 전용 접속, 운영 DB 색 표시, 위험한 문장 실행 전 확인, 팩 보유를 선언하지 않으면 Diagnostics·Tuning Pack 뷰를 쓰지 않음.
- **단축키** — 기본·Orange 5·PL/SQL Developer·SQL Developer 프리셋.
- **언어** — 한국어·영어 화면.

## 지원 버전

| | 지원 |
|---|---|
| Oracle Database | 11.2 이상. 시험한 버전은 11.2, 19c, 23ai/26ai(23.26.x), Autonomous Database(Serverless). 12c·18c·21c 는 막지 않지만 시험하지 않았다. 11.1 이하는 안내와 함께 접속을 거부한다. |
| Windows | Windows 10·11, x64 |
| macOS | macOS 14.7 이상, Apple Silicon(ARM64) |
| 지원 안 함 | Intel Mac·Windows ARM(돌 수 있으나 시험 안 함), Linux 데스크톱(빌드만 됨, 배포 안 함) |

근거: `docs/PRD.md` 7.1, `docs/decisions.md` D-026·D-030.

## 설치

[GitHub Releases](https://github.com/jongik-sv/oranda/releases) 에서 최신 릴리스를 받는다.

| OS | 파일 |
|---|---|
| Windows | `Oranda-win-Setup.exe`(설치) 또는 `Oranda-win-Portable.zip` |
| macOS | `Oranda-osx-Setup.pkg` |

Windows 빌드는 **아직 코드 서명이 없다**([코드 서명 정책](#코드-서명-정책)). SmartScreen 이 「Windows의 PC 보호」를 띄우면 **추가 정보 → 실행** 을 누른다. 서명 없는 macOS 빌드는 처음 한 번 **우클릭 → 열기** 로 연다. 자세한 순서·자동 업데이트·제거: [docs/user/install.md](docs/user/install.md).

### 처음 실행

1. Oranda 를 시작하면 접속 관리자가 열린다.
2. 접속을 더하고(EZConnect 문자열, TNS 별칭, 호스트·포트·서비스) **테스트** → **접속**. [docs/user/connect.md](docs/user/connect.md) 참고.
3. 선택: **설정 > 기능** 에서 Role 을 골라 쓰지 않는 메뉴를 숨긴다.

## 소스에서 빌드

필요한 것: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Windows 또는 macOS.

```sh
git clone https://github.com/jongik-sv/oranda.git
cd oranda
dotnet build Oranda.sln
dotnet run --project src/Oranda.App
```

시험:

```sh
# 단위·화면(Headless) 시험
dotnet test Oranda.sln --filter "Category!=Integration&Category!=Perf"
# Oracle 통합 시험은 Testcontainers 로 Oracle Free 컨테이너를 띄운다(Docker 또는 Podman 필요)
dotnet test Oranda.sln --filter "Category=Integration"
```

설치 패키지는 `.github/workflows/ci.yml` 이 [Velopack](https://velopack.io) 으로 만든다. [docs/release.md](docs/release.md) 참고.

## 코드 서명 정책

Windows 릴리스는 지금 **서명이 없다**. 오픈 소스 무료 코드 서명을 SignPath Foundation 에 신청했고, 승인되면 Windows 릴리스 바이너리를 아래 고지와 함께 서명한다.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)

- 이 저장소에서 GitHub Actions 릴리스 워크플로가 빌드한 바이너리만 서명한다. 외부 라이브러리는 원래 배포자의 서명을 그대로 두거나 서명 없이 둔다.
- 서명 요청은 승인자가 하나하나 직접 승인한다.
- 역할:
  - 커미터·리뷰어: [jongik-sv](https://github.com/jongik-sv)
  - 승인자: [jongik-sv](https://github.com/jongik-sv)

macOS 릴리스는 릴리스 워크플로에 Apple 인증 정보가 있으면 Developer ID 로 서명·공증하고, 없으면 서명 없이 낸다.

## 개인 정보

Oranda 는 사용자가 설정한 데이터베이스에만 접속한다. 그 밖에는:

- **업데이트 확인**: 설치본은 하루에 한 번(시작 20초 뒤) [GitHub Releases](https://github.com/jongik-sv/oranda/releases) 에 새 버전이 있는지 묻는다. GitHub 로 가는 보통의 HTTPS 요청이며 [GitHub 개인정보 처리방침](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)이 적용된다. **설정 > 업데이트** 에서 끌 수 있다.
- **오류 보고서**는 내 PC 에만 저장된다. 내용을 확인하고 **전송** 을 눌렀을 때만 보낸다. 비밀번호는 가려진다.
- 사용 통계·원격 측정은 모으지 않는다.

## 라이선스

Oranda 는 [MIT 라이선스](LICENSE)다. Oracle Data Provider for .NET(Oracle Free Use Terms and Conditions 로 배포)을 비롯한 외부 구성 요소는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 에 있다.

Oracle 은 Oracle 및 그 계열사의 등록 상표다. Orange for ORACLE 은 웨어밸리의 제품이다. Oranda 는 독립 프로젝트이며 Oracle·웨어밸리와 관계가 없고 그들의 보증을 받지 않았다.
