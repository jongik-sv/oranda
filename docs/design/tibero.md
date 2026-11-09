# Tibero 지원 설계

결정: decisions D-046(2026-11-09, PRD D-03 뒤집음). 범위 = Oracle 과 같은 수준 전체.

대상 버전: Tibero 6(시험 서버 = 6.2.15, 사용자 확인 2026-11-09)과 Tibero 7. 버전별로 다른 SQL 은 Oracle 처럼 카탈로그 파일 접미사(`.7.sql`)로 나눈다. Tibero 7 시험 서버는 아직 없다.

## 1. 접속 방법

| 후보 | Windows | macOS arm64 | 클라이언트 설치 | 판정 |
|---|---|---|---|---|
| TDP.NET Core (`Tibero.DataAccess`, .NET 5+, 관리 코드, Any CPU) | 문서상 지원 | 문서에 없음 → **실측 필요** | 필요 없음(문서) | **기본안** |
| ODBC (`libtbodbc`, `System.Data.Odbc`) | 있음 | macOS 빌드 없음 | Tibero 클라이언트 필요 | 안 됨(macOS) |
| Oracle Managed Driver 로 직접 | — | — | — | 안 됨(Tibero 는 자체 프로토콜, 기본 포트 8629) |
| JDBC(`jdbc:tibero:thin`) + Java 곁 프로세스 | 됨 | 됨 | JRE 필요 | 예비안(TDP.NET 이 macOS 에서 안 될 때) |

- 드라이버 배포: TDP.NET 은 nuget.org 에 없음. tibero.com 로그인 뒤 받는 `.nupkg`. 재배포 조건 확인 전에는 공개 배포본(GitHub Releases)에 넣지 않는다.
- 「사용자가 몰라도 되게」(사용자 요청): 재배포가 허용되면 설치본에 넣는다. 허용 전에는 리포 밖 로컬 NuGet 피드(`ORANDA_TIBERO_FEED`)로 빌드에 넣는 경로를 둔다. 사내 배포본은 이 경로로 만든다.
- 앱은 Tibero 어셈블리를 늦게 읽는다(`Oranda.Tibero` 어셈블리가 없으면 DBMS 목록에 Tibero 가 안 보이고, Oracle 동작은 그대로).

## 2. 구조

- 새 프로젝트 `src/Oranda.Tibero`. Oracle 처럼 Core 인터페이스를 구현하고 `[assembly: OrandaServiceAssembly]` 로 등록한다. `Oranda.Core`·`Oranda.Language` 는 Tibero 어셈블리를 참조하지 않는다(아키텍처 시험에 추가).
- `ConnectionProfile` 끝에 `Dbms Dbms = Dbms.Oracle` 를 더한다(D-004: 끝에 기본값으로 추가, 옛 파일은 Oracle 로 읽음).
- DBMS 선택 = 세션 단위. `ISessionManager` 가 프로필의 `Dbms` 로 공급자를 고른다. 서비스 인터페이스(`IExecutionService`·`IMetadataProvider`·`IExecutionPlanService`·`IMonitorService`·`ITuningService`·`IDataTransferService` 등)는 세션의 DBMS 로 구현을 고르는 디스패처 하나를 둔다. App 코드는 지금처럼 인터페이스만 본다.
- 카탈로그 SQL: `.sql` 헤더에 `dbms: oracle|tibero` 를 더한다. 없으면 `oracle`. Tibero 용 파일은 `src/Oranda.Tibero/Catalog/` 에 같은 `id` 로 둔다. `ISqlCatalog` 는 세션의 DBMS 로 파일을 고른다.
- 기능 지원 표시: `ServerFeatures`·`ServerCapabilities` 에 DBMS 축을 더한다. 안 되는 기능은 메뉴를 숨기지 않고 비활성 + 이유(「Tibero 에서는 지원하지 않음」)를 보인다.
- 오류 번호: `DbError` 에 `Dbms` 와 원문 코드(`ORA-01013`·`TBR-nnnn`)를 둔다. 의미 분류(취소·교착·연결 끊김·제약 위반·스냅샷 너무 오래됨 등)는 Core 의 `DbErrorKind` 로 모으고, Oracle·Tibero 공급자가 번호를 분류로 옮긴다. App 의 `OracleException`·숫자 비교는 분류로 바꾼다.
- App 의 ODP.NET 직접 사용(2개 파일, `ConnectionService`·`ConnectionTesters`)과 Oranda.Oracle 정적 도우미 사용(10개 파일)을 인터페이스 뒤로 옮긴다. App 의 인라인 Oracle SQL(약 10곳)은 카탈로그 파일로 옮긴다.
- 편집기 언어: Tibero SQL·PSM 은 Oracle 문법과 대부분 같아 같은 렉서·파서를 쓴다. 자동완성의 `DBMS_*` 목록은 DBMS 별로 거른다.

## 3. 기능별 지원 표(첫 판, 실측으로 고침)

| 영역 | Tibero | 비고 |
|---|---|---|
| 접속(호스트·포트·DB 이름) | 됨 | `Data Source=((INSTANCE=(HOST=)(PORT=)(DB_NAME=)))`. `tbdsn.tbr` 별칭은 나중 |
| TLS·지갑·Autonomous | 안 됨 | Oracle 전용 |
| SYSDBA 등 인증 모드 | 다르게 | Tibero 권한 체계 확인 필요 |
| SQL 실행·바인드·취소 | 됨 | `TiberoCommand.Cancel()`, `CommandTimeout` |
| DBMS_OUTPUT | 됨(확인 필요) | |
| 결과 편집(ROWID)·LOB | 확인 필요 | |
| 스키마 브라우저·객체 상세 | 다르게 | `ALL_TAB_COLUMNS` 는 `ALL_TBL_COLUMNS` 의 동의어 등 뷰 이름 차이 |
| DDL 추출 | 다르게 | `DBMS_METADATA.GET_DDL` 은 INDEX·TRIGGER 를 받지 않음 → 따로 |
| PL/SQL(PSM) 편집·컴파일 오류 | 다르게 | 오류 뷰·번호 체계 차이 |
| 실행 계획(EXPLAIN PLAN) | 다르게 | `PLAN_TABLE` = 세션 임시 표, `DBMS_XPLAN.DISPLAY` |
| 커서의 실제 계획·A-Rows | 다르게 | `DISPLAY_CURSOR`, `GATHER_SQL_PLAN_STAT` 필요 |
| SQL Monitor 보고서·AWR·ASH | 안 됨(첫 판) | Oracle 팩 기능 |
| 세션 모니터·세션 끊기 | 다르게 | `ALTER SYSTEM KILL SESSION (sid, serial#)`, SERIAL# 음수 버그 보정 |
| Lock 모니터 | 다르게 | V$ 뷰 차이 확인 |
| 트레이스·TKPROF·10053 | 안 됨(첫 판) | |
| 표·열 통계, 자식 커서 | 확인 필요 | |
| 데이터 복사·내보내기·가져오기 | 됨(대부분 공통) | 오류 번호 분류만 다르게 |
| 플래시백 조회 | 확인 필요 | |
| Java 저장 소스 | 안 됨 | |
| MyBatis 작업 공간 | 됨 | DB 와 무관한 부분 공통 |
| AI 튜닝 | 다르게 | 도구가 쓰는 계획·통계 경로를 따라감 |

## 4. 시험

- 단위 시험: 공급자 없이 도는 부분(디스패처, 카탈로그 선택, 오류 분류, 프로필 읽기)은 지금 CI 에서 돈다.
- 통합 시험: 공개 Tibero 컨테이너가 없다. `ORANDA_IT_TIBERO_*` 환경 변수(주소·포트·DB 이름·계정·비밀번호)가 있을 때만 돈다(`ItDb` 와 같은 건너뛰기 규칙, `ORANDA_IT_REQUIRE=1` 이면 실패). 접속 정보는 리포·로그·커밋에 넣지 않는다. CI 는 비밀이 없으면 건너뛴다.
- 쓰기·DDL 시험: 전용 시험 사용자와 접두어(`ORT_`)만 쓰고, 처음엔 읽기 시험만 돈다. 쓰기 시험 전에 사용자에게 한 번 확인한다.
- 공유 시험 서버(운영 아님)를 쓴다. 운영 DB 에는 접속하지 않는다.

## 5. 단계

| 단계 | 내용 | 막힘 |
|---|---|---|
| T0 | 드라이버 확보, win30·Mac 에서 접속·`SELECT * FROM V$VERSION` 실측(macOS arm64 판정) | 시험 서버 접속 정보, TDP.NET `.nupkg` |
| T1 | DBMS 추상화: `Dbms` 프로필 칸, 접속 대화 상자의 DBMS 선택, 세션별 공급자 디스패처, 카탈로그 `dbms:` 헤더, `DbErrorKind`, App 의 ODP.NET 직접 사용 걷어 내기 | 없음(지금 시작 가능) |
| T2 | `Oranda.Tibero` 접속·실행·취소·결과·DBMS_OUTPUT, 지원 표시 | T0, T1 |
| T3 | 카탈로그·스키마 브라우저·객체 상세·DDL·PSM 편집 | T2 |
| T4 | 실행 계획·세션·Lock 모니터·튜닝 | T2 |
| T5 | 데이터 복사·내보내기·가져오기, AI 튜닝 경로 | T2 |
| T6 | 배포: 재배포 확인 결과에 따라 설치본에 넣기 또는 사내 빌드 경로, 사용 설명서 | TmaxTibero 확인 |

T1 은 Oracle 동작을 바꾸지 않는 리팩터링이라 지금 레인으로 진행한다. T3·T4·T5 는 T2 뒤 병렬 레인으로 나눈다.
