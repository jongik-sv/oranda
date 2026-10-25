# TSK-01-03 실제 DB 접속 시험 결과 (26ai 컨테이너 범위)

## 시험 구성
- `tests/Oranda.Oracle.IntegrationTests/Integration/ServerProbeIntegrationTests.cs` — 모두 `Category=Integration`.
- `OracleFreeProbeTests` (4개 중 2개): Testcontainers.Oracle 4.16.0 로 `gvenzl/oracle-free:23-slim` 기동 → ODP.NET(Oracle.ManagedDataAccess.Core 23.26.301) 접속 → `ServerProbe.ProbeAsync` 결과를 별도 SQL(`product_component_version`, `v$pdbs`, `v$instance`, `v$mystat`, `session_roles`)로 읽은 값과 비교.
  - SYSTEM(DBA) 계정, 시험 안에서 만든 `CREATE SESSION` 만 가진 일반 계정 두 가지.
- `ExternalDbProbeTests` (2개): 환경 변수가 있을 때만 실제로 접속한다. 없으면 즉시 반환(통과 처리, xunit 2.5 에 동적 Skip 이 없음).
  - `ORANDA_IT_19C_CONNSTR` — 별도 19c, ODP.NET 연결 문자열 전체. 버전 19 단정 + 실제값 비교.
  - `ORANDA_IT_ADB_CONNSTR` — ADB 연결 문자열 전체. `ORANDA_IT_ADB_WALLET` 이 있으면 지갑 폴더로 TNS_ADMIN/WalletLocation 설정, 없으면 지갑 없는 TLS.
- 기본 `dotnet test Oranda.sln` 은 csproj 의 `VSTestTestCaseFilter=Category!=Integration` 으로 Integration 을 뺀다. 명령줄 `--filter Category=Integration` 이 이 값을 덮어쓴다(확인함).

## 실행 결과 (Windows 11, podman 5 WSL 머신)
- `dotnet test tests/Oranda.Oracle.IntegrationTests --filter Category=Integration` : 통과 4 / 실패 0 (약 50~110초, 컨테이너 기동 포함). 시험 끝에 컨테이너는 Testcontainers 가 제거(남은 컨테이너 0).
- `dotnet test Oranda.sln` (컨테이너 없이 기본): 전부 통과, Integration 제외.
- 26ai 확인값: 주 버전 23, 멀티테넌트 true, PDB `FREEPDB1`(= `v$pdbs`), 인스턴스명·SID·서비스명 일치, SYSTEM 은 IsDba/CanQueryDictionary/CanQueryPerformanceViews true, 일반 계정은 모두 false (v$session 조회 시 ORA-00942 직접 확인).

## podman 호환 문제와 해결
- Testcontainers 의 기본 로그 대기(`UntilMessageIsLogged`)가 podman npipe 의 로그 API 응답을 읽지 못한다(`HttpConnection.CreateResponseMessage: Sequence contains no elements`). 컨테이너는 떠 있다.
- 해결: `.WithWaitStrategy(Wait.ForUnixContainer())`(대기 없음) + 픽스처에서 SYSTEM 접속을 3초 간격 최대 5분 재시도해 DB 가 열릴 때까지 기다린다. Docker Desktop 에서는 기본 대기로도 되지만 이 방식도 동일하게 동작한다.
- 환경 변수: `DOCKER_HOST=npipe://./pipe/docker_engine`, `TESTCONTAINERS_RYUK_DISABLED=true` (Ryuk 이 문제가 되는지는 따로 확인하지 않고 껐다).
- 비밀번호는 실행마다 만든 임시 GUID 기반 값이며 어디에도 쓰지 않는다.

## src 수정 요청 (후속 l0103-db-2 에서 1번 반영됨: 행 존재 COUNT 기준 + EXECUTE ANY PROCEDURE 인정, 시험으로 확인)
1. `src/Oranda.Oracle/Connection/ServerProbe.cs` 의 `canExecuteDbmsMonitor`: `CanRunAsync` 는 쿼리가 오류 없이 실행되면 true 를 돌려주므로, `all_tab_privs` 에 DBMS_MONITOR EXECUTE 행이 없어도 true 가 된다. 26ai 일반 계정(해당 권한 행 0건)에서 `CanTraceSessions == true` 로 관측됨. 행 존재 여부(`SELECT COUNT(*)`)나 실제 호출 가능 여부로 판정해야 한다. 고치면 `Plain_account_probe_reports_no_privileges` 에 `Assert.False(CanTraceSessions)` 를 추가한다.
2. (참고) `ServerProbe` 의 `OracleException` 삼킴은 의도된 설계이나, 진단용으로 실패 사유를 남길 수단이 없다.

## 보류 항목과 필요한 사람 자원
| 항목 | 필요한 것 |
|---|---|
| 공유 19c DB | 접속 정보(호스트·서비스·계정). 가능하면 DBA 계정과 일반 계정 각 1개. `ORANDA_IT_19C_CONNSTR` 로 시험 실행 |
| ADB (지갑 있음/없음 TLS) | ADB 인스턴스와 지갑 zip, 지갑 없는 TLS 허용 설정. `ORANDA_IT_ADB_CONNSTR`, `ORANDA_IT_ADB_WALLET` |
| macOS ARM | Apple Silicon Mac 한 대. 이미지가 arm64 를 지원하는 `gvenzl/oracle-free:23-slim` 은 컨테이너(podman/Docker Desktop)로, 또는 별도 DB 로 같은 시험 실행 |

## decisions 후보
- Testcontainers 로 podman 을 쓸 때는 로그 대기 전략 대신 접속 재시도로 준비 여부를 판정한다(로그 API 비호환).
- 컨테이너가 필요한 시험은 `Category=Integration` 으로 표시하고 test csproj 의 기본 필터로 제외한다. 19c·ADB 시험은 환경 변수(`ORANDA_IT_*`)가 없으면 아무것도 하지 않는다.
- `ServerProbe` 의 DBMS_MONITOR 점검은 행 존재 기준으로 고친다(TSK-01-02 후속 수정).
