# Tibero 지원 설계

결정: decisions D-046(2026-11-09, PRD D-03 뒤집음). 범위 = Oracle 과 같은 수준 전체.

대상 버전: Tibero 6 이상 전부(6·7 과 이후 판, 사용자 지시 2026-11-09). 시험 서버 = 6.2.15(빌드 302172, 프로토콜 2.15). 버전으로 막지 않고, 없는 뷰·기능만 버전 판정으로 끈다. 버전별로 다른 SQL 은 Oracle 처럼 카탈로그 파일 접미사(`.7.sql`)로 나눈다. Tibero 7 시험 서버는 아직 없다.

## 1. 접속 방법

| 후보 | Windows | macOS arm64 | 클라이언트 설치 | 판정 |
|---|---|---|---|---|
| TDP.NET Core (`Tibero.DataAccess`, .NET 5+, 관리 코드, Any CPU) | 문서상 지원 | 문서에 없음 → **실측 필요** | 필요 없음(문서) | **기본안** |
| ODBC (`libtbodbc`, `System.Data.Odbc`) | 있음 | macOS 빌드 없음 | Tibero 클라이언트 필요 | 안 됨(macOS) |
| Oracle Managed Driver 로 직접 | — | — | — | 안 됨(Tibero 는 자체 프로토콜, 기본 포트 8629) |
| JDBC(`jdbc:tibero:thin`) + Java 곁 프로세스 | 됨 | 됨 | JRE 필요 | 예비안(TDP.NET 이 macOS 에서 안 될 때) |

- 드라이버 배포: TDP.NET 은 nuget.org 에 없음. tibero.com 로그인 뒤 받는 `.nupkg`. 재배포 조건 확인 전에는 설치본(공개·사내 모두)에 넣지 않는다.
- **드라이버 위치 지정(D-046-1, 2026-11-09)**: 사용자가 설정 > 드라이버 「Tibero 드라이버(TDP.NET Core) 위치」에 `Tibero.DataAccess.dll`·폴더·`.nupkg` 를 지정한다. 개발·IT·WSL 은 환경 변수 `ORANDA_TIBERO_DRIVER` 가 설정보다 먼저다. `Oranda.Tibero` 는 드라이버를 컴파일 때 참조하지 않는다 — `src/Oranda.Tibero/Driver/` 의 리플렉션 어댑터가 첫 Tibero 사용 때 읽고 필요한 멤버를 모두 묶는다. 공개본·CI·사내 빌드가 같은 코드다(`#if TIBERO_DRIVER`·`ORANDA_TIBERO_FEED` 없앰). 어댑터 API·로더·경계 규칙 = [tibero-driver-adapter.md](tibero-driver-adapter.md).
- 읽기 전 검사(메타데이터만): 이름이 정확히 `Tibero.DataAccess`, 관리 코드, .NET(Core)용(`mscorlib` 참조인 .NET Framework 판 2.70 은 거절), 메이저 1(1.1.35 = 확인됨, 그 밖 1.x = 「시험하지 않은 버전」 경고 후 허용). `Oracle.DataAccess` 1.1.35(같은 패키지의 Oracle 호환 이름 변형)도 거절.
- 드라이버 의존 중 런타임에 없는 `System.Security.Permissions` 는 `Oranda.Tibero` 가 패키지로 함께 싣는다(MIT).
- 「사용자가 몰라도 되게」(D-046 사용자 요청): 재배포가 허용되면 설치본에 dll 을 넣고 기본 위치로 지정한다(어댑터는 그대로). 그 전에는 사용자가 한 번 지정한다.
- 앱은 `Oranda.Tibero` 어셈블리를 늦게 읽는다. 어셈블리가 있으면 DBMS 목록에 Tibero 가 늘 보이고, 드라이버가 지정되지 않았으면 접속 대화상자에 「Tibero 드라이버가 지정되지 않았습니다 [드라이버 지정…]」 띠를 보이고 [연결]·[접속 시험]을 끈다. Oracle 동작은 그대로.

## 2. 구조

- 새 프로젝트 `src/Oranda.Tibero`. Oracle 처럼 Core 인터페이스를 구현하고 `[assembly: OrandaServiceAssembly]` 로 등록한다. `Oranda.Core`·`Oranda.Language` 는 Tibero 어셈블리를 참조하지 않는다(아키텍처 시험에 추가).
- `ConnectionProfile` 끝에 `Dbms Dbms = Dbms.Oracle` 를 더한다(D-004: 끝에 기본값으로 추가, 옛 파일은 Oracle 로 읽음).
- DBMS 선택 = 세션 단위. `ISessionManager` 가 프로필의 `Dbms` 로 공급자를 고른다. 서비스 인터페이스(`IExecutionService`·`IMetadataProvider`·`IExecutionPlanService`·`IMonitorService`·`ITuningService`·`IDataTransferService` 등)는 세션의 DBMS 로 구현을 고르는 디스패처 하나를 둔다. App 코드는 지금처럼 인터페이스만 본다.
- 카탈로그 SQL: `.sql` 헤더에 `dbms: oracle|tibero` 를 더한다. 없으면 `oracle`. Tibero 용 파일은 `src/Oranda.Tibero/Catalog/` 에 같은 `id` 로 둔다. `ISqlCatalog` 는 세션의 DBMS 로 파일을 고른다.
- 기능 지원 표시: `ServerFeatures`·`ServerCapabilities` 에 DBMS 축을 더한다. 안 되는 기능은 메뉴를 숨기지 않고 비활성 + 이유를 보인다. 이유는 두 단위로 나뉜다. ① 서비스 구현 유무(`DbmsServiceKeys`): 그 DBMS 에 서비스(`IMonitorService` 등) 구현이 아직 없으면 「Tibero 에서는 아직 지원하지 않음」 — 구현이 들어오면 저절로 풀린다. ② 기능 단위(`DbmsFeatureKeys` ← `DbmsFeature`·`DbmsSupport`): 서비스는 있어도 그 DBMS 가 해당 기능을 지원하지 않으면 「Tibero 에서는 지원하지 않음」 — 실측 표(`DbmsSupport`)를 고쳐야 풀린다. 서버 버전으로 정해지는 네 칸(`ServerFeatures`)은 3장 아래 「버전 기능 칸」에 따른다.
- 표시 이름·오류 코드: 상태 표시줄은 Oracle 이 `19c`·`26ai`, Tibero 가 `Tibero 6.2`(서버 `6.2.15` → 주.부)로 보인다(툴팁은 `Tibero 6.2.15`). 오류 코드는 `DbError.Code` 가 이미 DBMS 원문(`ORA-00942`·`TBR-8033`)이고, 번호만 있는 곳은 `DbErrorCodes.Display(dbms, number)` 로 쓴다 — App 에 `ORA-` 접두를 직접 붙이는 곳은 없다.
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
| 읽기 전용 접속 | 앱 관문만 막음 | 6.2.15 실측: 서버가 읽기 전용 트랜잭션을 지키지 않는다(최상위 `SET TRANSACTION READ ONLY` = -7001, PSM 블록 안 `ReadOnlyBlock` 은 받지만 쓰기를 막지 않음). 그래서 쓰기는 앱 정책 관문이 서버로 보내기 전에 막고, 행을 잠그는 `SELECT … FOR UPDATE`(OF·NOWAIT·WAIT·SKIP LOCKED 포함)도 쓰기로 보고 막는다(Oracle 은 서버가 ORA-01456 으로 막아 그대로). 접속 대화상자가 읽기 전용을 켠 Tibero 프로필에 안내를 보인다. 권고 = 확실히 막으려면 SELECT 권한만 있는 계정 |
| 결과 편집(ROWID)·LOB | 확인 필요 | |
| 스키마 브라우저·객체 상세 | 다르게 | `ALL_TAB_COLUMNS` 는 `ALL_TBL_COLUMNS` 의 동의어 등 뷰 이름 차이 |
| DDL 추출 | 다르게 | `DBMS_METADATA.GET_DDL` 은 INDEX·TRIGGER 를 받지 않음 → 따로 |
| PL/SQL(PSM) 편집·컴파일 오류 | 다르게 | 오류 뷰·번호 체계 차이 |
| 실행 계획(EXPLAIN PLAN) | 다르게 | `PLAN_TABLE` = 세션 임시 표, `DBMS_XPLAN.DISPLAY` |
| 커서의 실제 계획·A-Rows | 다르게 | `DISPLAY_CURSOR`, `GATHER_SQL_PLAN_STAT` 필요 |
| SQL Monitor 보고서·AWR·ASH | 안 됨(끔 유지, 실측 필요) | 6.2.15 실측([tibero-catalog-map.md](tibero-catalog-map.md)): `DBMS_SQLTUNE.REPORT_SQL_MONITOR` 는 있으나 인수가 달라 Oracle 호출 형태로는 ODP-11029. `DBA_HIST_*` 는 일부 있으나 `SQL_ID` 대신 `SQL_HASH_VALUE`·`*_DELTA` 열 없음. `V$ACTIVE_SESSION_HISTORY` 는 있으나 `SESSION_STATE`·`WAIT_CLASS` 없음·`EVENT` → `WAIT_EVENT`. Tibero 호출 형태로 보고서가 나오는지는 아직 못 쟀다 |
| 세션 모니터·세션 끊기 | 다르게 | `ALTER SYSTEM KILL SESSION (sid, serial#)`, SERIAL# 음수 버그 보정 |
| Lock 모니터 | 다르게 | V$ 뷰 차이 확인 |
| 트레이스·TKPROF·10053 | 안 됨 | 세션 트레이스: `DBMS_MONITOR` 패키지 없음(6.2.15 실측). TKPROF·10053 은 조사 대상이 아니었다 → 끔 유지, 실측 필요 |
| 표·열 통계, 자식 커서 | 다르게(6.2.15 실측) | `*_TAB_STATISTICS`·`*_IND_STATISTICS`·`GV$SQL_SHARED_CURSOR` 없음 → `*_TABLES`·`*_INDEXES` 통계 열을 쓴다. `DBMS_STATS` 는 있음. 자식 커서 사유는 끔 |
| 데이터 내보내기·가져오기 | 됨(T5 TI1a) | 값 변환·오류 분류만 다르게 |
| 데이터 복사 | 있지만 꺼짐(T5) | 실행기·대량 실행·방언 모두 있으나 등록 안 함 — 두 번째 계정 복사 IT 뒤 켬. 같은 계정 안 복사는 Core 가 막음. 켜기 전 남은 일 = [tibero-driver-transfer.md](tibero-driver-transfer.md) 4장 |
| 플래시백 조회 | 확인 필요 | |
| Java 저장 소스 | 안 됨(끔 유지, 실측 필요) | `DBMS_JAVA`·`*_JAVA_CLASSES` 등은 있으나 `*_JAVA_DERIVATIONS`·`*_JAVA_IMPLEMENTS`·`*_JAVA_INNERS` 없음, `DBMS_JAVA.SHORTNAME` 은 빈 값(6.2.15 실측). JAVA SOURCE 를 만들고 컴파일하는 것은 못 쟀다 |
| MyBatis 작업 공간 | 됨 | DB 와 무관한 부분 공통 |
| AI 튜닝 | 다르게 | 도구가 쓰는 계획·통계 경로를 따라감 |

### 버전 기능 칸(`ServerFeatures.For(major, Dbms.Tibero)`, 6.2.15 실측)

| 칸 | 값 | 근거 |
|---|---|---|
| `RowLimiting` (FETCH FIRST·OFFSET) | 켬 | `FETCH FIRST 1 ROWS ONLY`·`OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY` 실행됨 |
| `Release12Columns` (identity·invisible 열, editionable) | 끔 | `*_TAB_COLS` 에 `IDENTITY_COLUMN`·`USER_GENERATED`, `*_OBJECTS` 에 `EDITIONABLE` 열 없음. identity·invisible DDL 직접 시험은 안 했다 → 실측 필요 |
| `Multitenant` (CDB/PDB) | 끔 | `V$DATABASE.CDB` 열 없음, `SYS_CONTEXT('USERENV','CON_NAME')` → ODP-11006 |
| `Release23Types` (도메인·VECTOR·BOOLEAN) | 끔 | `*_TAB_COLS.DOMAIN_NAME` 열 없음. VECTOR·BOOLEAN 열은 안 쟀다 → 실측 필요. Tibero 7 은 서버가 없어 못 쟀다 |

## 4. 시험

- 단위 시험: 공급자 없이 도는 부분(디스패처, 카탈로그 선택, 오류 분류, 프로필 읽기)은 지금 CI 에서 돈다.
- 통합 시험: 공개 Tibero 컨테이너가 없다. `ORANDA_IT_TIBERO_*` 환경 변수(주소·포트·DB 이름·계정·비밀번호)가 있을 때만 돈다(`ItDb` 와 같은 건너뛰기 규칙, `ORANDA_IT_REQUIRE=1` 이면 실패). 접속 정보는 리포·로그·커밋에 넣지 않는다. CI 는 비밀이 없으면 건너뛴다.
- 쓰기·DDL 시험: 전용 시험 사용자와 접두어(`ORT_`)만 쓰고, 처음엔 읽기 시험만 돈다. 쓰기 시험 전에 사용자에게 한 번 확인한다.
- 공유 시험 서버(운영 아님)를 쓴다. 운영 DB 에는 접속하지 않는다.

## 5. 단계

| 단계 | 내용 | 막힘 |
|---|---|---|
| T0 | 드라이버 확보, win30·Mac 에서 접속·`SELECT * FROM V$VERSION` 실측(macOS arm64 판정). **2026-11-09: TDP.NET Core `Tibero.DataAccess` 1.1.35(net5.0, 관리 코드)로 WSL(Linux)·.NET 10 에서 접속·읽기 확인.** macOS 실측 남음 | macOS 실측 |
| T1 | DBMS 추상화: `Dbms` 프로필 칸, 접속 대화 상자의 DBMS 선택, 세션별 공급자 디스패처, 카탈로그 `dbms:` 헤더, `DbErrorKind`, App 의 ODP.NET 직접 사용 걷어 내기. **2026-11-09: Oracle 쪽(O0~O6) 끝, main 반영** — 특성 시험, App 인라인 SQL 의 `.sql` 이전, `OracleErrorClassifier`(ORA → `DbErrorKind`), `Dbms.Oracle` 서비스 키, C4 계약 구현(접속 시험·서버 버전 정책·스키마 문장·DDL 내보내기·열 순서 변경), `OracleSessionManager` 의 비 Oracle 프로필 거부. 남음: App 쪽 using 교체·TnsResolver 합치기(tb-app), C7 디스패처(tb-core) | App·Core 쪽 |
| T2 | `Oranda.Tibero` 접속·실행·취소·결과·DBMS_OUTPUT, 지원 표시. **2026-11-09 D-046-1: 드라이버 위치 지정 + 리플렉션 어댑터로 바꿈**(tb-driver DV0~DV6: 어댑터 API 표 → 빌드 정리 → 로더·검사 → 어댑터 → 가짜 드라이버 시험 → 접속 시험·오류 분류 이전 → 실제 드라이버 IT). 설정 화면·접속 대화상자 띠 = tb-appgate DU0~DU4 | T0, T1 |
| T3 | 카탈로그·스키마 브라우저·객체 상세·DDL·PSM 편집 | T2 |
| T4 | 실행 계획·세션·Lock 모니터·튜닝 | T2 |
| T5 | 데이터 복사·내보내기·가져오기, AI 튜닝 경로 | T2 |
| T6 | 배포: 재배포가 허용되면 설치본에 드라이버를 넣고 기본 위치로 지정, 아니면 지금처럼 사용자 지정. 사용 설명서(드라이버 받기·지정) | TmaxTibero 확인 |

T1 은 Oracle 동작을 바꾸지 않는 리팩터링이라 지금 레인으로 진행한다. T3·T4·T5 는 T2 뒤 병렬 레인으로 나눈다.
