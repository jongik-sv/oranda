# Tibero 드라이버 세션 실측 (T2 MS)

실측 2026-11-09. 드라이버 = TDP.NET Core `Tibero.DataAccess` 1.1.35(net5.0), WSL Ubuntu·.NET 10. 서버 = Tibero 6(빌드 302172, 프로토콜 2.15, `V$VERSION` 의 `PRODUCT_MINOR` 는 공백).
시험 계정 = 일반 계정(역할 `CONNECT`·`RESOURCE`, DBA 아님). 읽기 문장과 세션 설정(`ALTER SESSION`·`DBMS_APPLICATION_INFO`)만 썼다. 접속 정보(주소·포트·DB 이름·계정)는 싣지 않는다.
측정 프로그램은 리포 밖(시험 PC 의 임시 폴더)에 두었다. 설계 정본 = [tibero.md](tibero.md), 뷰·함수 차이 = [tibero-catalog-map.md](tibero-catalog-map.md).

## 1. 드라이버 형식

| 형식 | 바탕 | 쓸 곳 |
|---|---|---|
| `Tibero.DataAccess.Client.TiberoConnection` | `DbConnection` | 속성 `ServerVersion`(= `"6"`, 주 버전만), `DatabaseName`·`InstanceName`(= DB 이름), `HostName`(= **클라이언트** PC 이름), `ServiceName`·`Database`·`DataSource` = null. 쓰기 속성 `ModuleName`·`ActionName`·`ClientId`·`ClientInfo`. 이벤트 `StateChange` |
| `TiberoCommand` | `DbCommand` | `BindByName`·`ArrayBindCount`·`InitialLOBFetchSize`·`InitialLONGFetchSize`·`AddRowid`·`ImplicitRefCursors`·`CommandTimeout` (ODP.NET 과 같은 이름) |
| `TiberoException` (sealed, 공개 생성자 없음) | `DbException` | **서버 오류**. `Number` = 음수(`-8033`), `Errors[0].Number` 도 같음. `Message` = `ODP-8033:…` |
| `tbcomm.err.TbOdpException` (공개, 생성자 `(int, string)`) | **`System.Exception`** | **드라이버 쪽 오류**(접속 실패·끊긴 연결에 명령 등). `ErrorCode` = 음수(`-90603`), `Number` 속성 없음. `DbException` 이 아니다 |
| `TiberoConnectionStringBuilder` | `DbConnectionStringBuilder` | `Pooling`·`ConnectionTimeout`·`StatementCacheSize`·`DBAPrivilege`·`ProxyUserId`·`ValidateConnection`·`SelfTuning` 등(ODP.NET 과 같은 이름) |

- `TiberoConnection` 의 `ModuleName`·`ActionName`·`ClientInfo` 는 값을 넣기 전에 읽으면 `ArgumentException` 을 던진다(읽지 않는다).
- 드라이버 오류 상수(`tbcomm.err.TbError`): `COMM_ERROR`=-90400, `COMM_REFUSED`=-90401, `COMM_PROTOCOL_ERROR`=-90402, `COMM_CONNECTION_BROKEN`=-90403, `COMM_IO_ERROR_READ`…`COMM_IO_CORRUPTED_MSG`=-90405…-90410, `SVR_AUTHENTICATION_FAIL`=-90502, `SVR_CANCEL_FAIL`=-90526, `SVR_PASSWORD_GRACE_EXPIRED`=-90547, `MU_ACTION_ON_CLOSED_CONNECTION`=-90603, `MU_CONNECTION_CLOSED`=-90643, 내부 `INTERNAL_REQUESTED_DISCONNECT`=-590719, `INTERNAL_COMM_ERROR_INTERNAL_SOCKET`=-590720, `INTERNAL_COMM_IO_ERROR_READ_PACKET`=-590729, `INTERNAL_COMM_IO_ERROR_READ_STREAM_BUFFER`=-590730.

## 2. 연결 문자열·풀링·닫기

| 확인 | 결과 |
|---|---|
| `Pooling=false`·`Connection Timeout=5` 키 | 받음(둘 다·함께) |
| `Pooling=false` 로 연 연결을 `Close()` | 1.5초 뒤 `V$SESSION` 에서 그 SID 가 사라짐(서버 세션 끝) |
| 기본(풀링) 연결을 `Close()` | 그 SID 가 `V$SESSION` 에 **남음**(풀에 돌아감) → 세션 관리자는 `Pooling=false` 필수 |
| `StateChange` | 열 때 `Closed->Open`, 닫을 때 `Open->Closed` 한 번씩 |
| 닫힌 연결에 명령 | `TbOdpException` `ODP-90603:Invalid operation: disconnected from the server.` |
| 없는 포트로 열기 | `TbOdpException` `-90401` `ODP-90401:Connection refused by the server. - No route to host <주소>:<포트>` (메시지에 주소가 실린다 → 로그에 그대로 쓰지 않는다) |
| 틀린 DB 이름으로 열기 | `TbOdpException` `-12079` `ODP-12079:The requested DB_NAME does not match with the server DB_NAME.` |
| 세션이 서버에서 죽음(KILL) | **실측 못 함**: 시험 계정에 `ALTER SYSTEM` 이 없다. 번호는 오류 참조 안내서의 `12001`(ERROR_SESS_KILLED)·`12002`·`12094` 와 드라이버 상수(위)로 둔다 |

## 3. 세션 신원(ServerInfo 재료)

| 값 | 얻는 법(실측) | 안 되는 것 |
|---|---|---|
| SID | `SELECT SID FROM V$MYSTAT WHERE ROWNUM = 1` | `SYS_CONTEXT('USERENV','SID')` = `ODP-11006` |
| SERIAL# | `V$SESSION.SERIAL#` (= `AUDSID` = `SYS_CONTEXT('USERENV','SESSIONID')`) | |
| 세션 사용자·현재 스키마 | `SYS_CONTEXT('USERENV','SESSION_USER')`·`'CURRENT_SCHEMA'` | `'CURRENT_USER'` = `ODP-11006` |
| DB 이름 | `SYS_CONTEXT('USERENV','DB_NAME')` = `V$DATABASE.NAME` = `V$INSTANCE.DB_NAME` | `'DB_UNIQUE_NAME'`·`'SERVICE_NAME'`·`'CON_NAME'`·`'CDB_NAME'` = `ODP-11006` |
| 인스턴스 이름 | `SYS_CONTEXT('USERENV','INSTANCE_NAME')` = `V$INSTANCE.INSTANCE_NAME`(시험 서버는 DB 이름과 같음) | |
| 인스턴스 번호 | `SYS_CONTEXT('USERENV','INSTANCE')` = `V$INSTANCE.INSTANCE_NUMBER` = 0 | |
| 서버 호스트 | `SYS_CONTEXT('USERENV','SERVER_HOST')` = `V$INSTANCE.HOST_NAME` | `TiberoConnection.HostName` 은 클라이언트 PC 이름이라 쓰지 않는다 |
| 클라이언트 쪽 | `'HOST'`·`'IP_ADDRESS'`·`'OS_USER'`·`'TERMINAL'`(null)·`'CLIENT_INFO'`(= 프로그램 이름) | `'CLOUD_SERVICE'`·`'ISDBA'`·`'AUTHENTICATION_METHOD'`·`'ACTION'` = `ODP-11006` |
| 언어 | `'LANG'` = `KO`, `'LANGUAGE'` = `UTF8` | |
| 클러스터(TAC) | `V$PARAMETERS`(끝에 S) `NAME='CLUSTER_DATABASE'` → `NO` | `V$PARAMETER` 는 없음(`ODP-8033`) |
| 버전 | `V$VERSION` 의 (NAME, VALUE): `PRODUCT_MAJOR=6`, `PRODUCT_MINOR=' '`, `BUILD_NUMBER`, `PROTOCOL_MAJOR/MINOR`, `STABLE_VERSION`. `BANNER` 는 마지막 줄만(플랫폼) | |
| 멀티테넌트·Autonomous·에디션·관리 팩 | 없음 → false·null | |

- `DbKey` 재료: `DB_UNIQUE_NAME` 이 없으므로 그 자리에 `DB_NAME` 을 넣는다. `CON_NAME` 은 `-`.
- `V$SESSION` 자기 행 열(6.2.15): `SID, SERIAL#, AUDSID, USER#, USERNAME, IPADDR, COMMAND, STATUS, SCHEMA#, SCHEMANAME, TYPE, SQL_ID, SQL_CHILD_NUMBER, PREV_SQL_ID, PREV_CHILD_NUMBER, SQL_ET, LOGON_TIME, STATE, WLOCK_WAIT, WAIT_EVENT, WAIT_TIME, PGA_USED_MEM, SQL_TRACE, PROG_NAME, CLIENT_PID, PID, WTHR_ID, OS_THR_ID, OSUSER, MACHINE, TERMINAL, MODULE, ACTION, CLIENT_INFO, CLIENT_IDENTIFIER, PDML_ENABLED, PDML_STATUS, PDDL_STATUS, PQ_STATUS, ROW_WAIT_OBJ_ID, ROW_WAIT_FILE_NO, ROW_WAIT_BLOCK_NO, ROW_WAIT_ROW_NO, CONSUMER_GROUP, CONSUMED_CPU_TIME`. Oracle 과 다른 열은 tibero-catalog-map.md 4장.

## 4. 권한·도구 판정(ServerCapabilities 재료)

| 확인 | 결과 |
|---|---|
| `SESSION_PRIVS` | 읽힘. 시험 계정 = `CREATE PROCEDURE, CREATE SEQUENCE, CREATE SESSION, CREATE TABLE, CREATE TRIGGER, SELECT ANY TABLE` |
| `SESSION_ROLES` | 읽힘. `CONNECT, RESOURCE` |
| `USER_ROLE_PRIVS` | 읽힘(GRANTEE, GRANTED_ROLE, ADMIN_OPTION, DEFAULT_ROLE) |
| `V$SESSION` 읽기 | 일반 계정도 됨(`SELECT 1 FROM V$SESSION WHERE ROWNUM <= 1`) → `CanQueryPerformanceViews` 는 실제로 읽어 보고 정한다 |
| `DBMS_MONITOR` | 패키지 없음(ALL_OBJECTS 에 없음) → 세션 트레이스 불가(DbmsSupport 의 `SessionTrace` 와 맞음) |
| 있는 패키지 | `DBMS_APPLICATION_INFO`·`DBMS_SESSION`·`DBMS_OUTPUT`·`DBMS_TRANSACTION`(SYS + PUBLIC 동의어), `DBMS_LOCK`(PUBLIC 동의어만, SYS 패키지 없음) |
| `DBA`·`SELECT ANY DICTIONARY`·`ALTER SYSTEM` 이름 | 이 계정엔 없어 이름 일치는 확인 못 함(Tibero 문서상 같은 이름). `ServerCapabilities.From` 을 그대로 쓴다 |

## 5. 세션 설정

| 설정 | 결과 |
|---|---|
| `ALTER SESSION SET NLS_DATE_FORMAT`·`NLS_TIMESTAMP_FORMAT`·`NLS_TIMESTAMP_TZ_FORMAT`·`NLS_SORT`·`NLS_NUMERIC_CHARACTERS` | 됨, `NLS_SESSION_PARAMETERS` 에 반영 |
| `ALTER SESSION SET NLS_LANGUAGE`·`NLS_TERRITORY` | **안 됨** `ODP-7197:Invalid initialization parameter name 'NLS_LANGUAGE'.` → 건너뛰고 이유를 남긴다 |
| `NLS_SESSION_PARAMETERS` 기본값 | `NLS_DATE_FORMAT=YYYY/MM/DD`, `NLS_DATE_LANGUAGE=KOREAN`, `NLS_TIME_FORMAT`, `NLS_TIMESTAMP_FORMAT=YYYY/MM/DD HH24:MI:SSXFF`, `NLS_TIMESTAMP_TZ_FORMAT`, `NLS_LENGTH_SEMANTICS=BYTE`, `NLS_COMP`·`NLS_SORT=BINARY`, `NLS_NUMERIC_CHARACTERS=.,`, `NLS_CURRENCY=$` (10행, `NLS_LANGUAGE`·`NLS_TERRITORY` 행 없음) |
| `BEGIN DBMS_APPLICATION_INFO.SET_MODULE(:m, :a); DBMS_SESSION.SET_IDENTIFIER(:c); END;` (BindByName) | 됨 → `V$SESSION.MODULE`·`ACTION`·`CLIENT_IDENTIFIER` 에 반영. `SYS_CONTEXT` 의 `MODULE`·`CLIENT_IDENTIFIER` 도 바뀜 |
| `DBMS_APPLICATION_INFO.SET_CLIENT_INFO` | 됨 → `V$SESSION.CLIENT_INFO` (`SYS_CONTEXT('USERENV','CLIENT_INFO')` 는 프로그램 이름 그대로) |
| 연결 속성 `ClientId` | 다음 왕복에서 `CLIENT_IDENTIFIER` 에 반영 |
| 연결 속성 `ModuleName`·`ActionName`·`ClientInfo` | 넣어도 오류는 없으나 `V$SESSION` 에 **반영 안 됨**(앞서 SET_MODULE 로 넣은 값 그대로) → 세션 식별은 PL/SQL 블록으로 한다 |
| Keep Alive `SELECT 1 FROM DUAL` | 됨 |

## 6. 오류 모양(S4 재료)

서버 오류 = `TiberoException`, `Number` 음수, 메시지 머리말 `ODP-nnnnn:`(번호는 서버의 `TBR-nnnnn` 과 같다). 문장 위치는 메시지 안에 `at line L, column C of <객체>:` + 원문 줄 + `^` 줄로 온다.

| 문장 | Number | 메시지(줄 바꿈 = ⏎) |
|---|---|---|
| `SELEC 1 FROM DUAL` | -15146 | `ODP-15146:PSM compilation error.⏎ODP-15002:Parsing failed.⏎at line 1, column 1 of null:⏎SELEC 1 FROM DUAL⏎^^^^^` |
| `SELECT a,⏎  b⏎FROM DUAL WHERE` | -8007 | `ODP-8007:Missing boolean expression.⏎at line 3, column 11 of null:⏎FROM DUAL WHERE⏎          ^` |
| `SELECT *⏎  FROM ORT_NO_SUCH_TABLE_XYZ` | -8033 | `…not found.⏎at line 2, column 9 of null:⏎  FROM ORT_NO_SUCH_TABLE_XYZ⏎       ^` |
| `SELECT 1,⏎       NO_SUCH_COL FROM DUAL` | -8026 | `ODP-8026:Invalid identifier.⏎at line 2, column 9 of null:⏎…⏎       ^` |
| `SELECT * FROM SYS.USER$` (권한 없음) | -8033 | 권한 없는 객체도 「없음」(8033)으로 온다. `at line 1, column 16` |
| `BEGIN⏎  x := 1;⏎END;` | -15146 | `ODP-15146:PSM compilation error.⏎ODP-15044:Undefined identifier 'X'.⏎at line 2, column 3 of null:⏎  x := 1;⏎  ^` |
| `DECLARE … BEGIN⏎  n := 1/0;⏎END;` | -5070 | `ODP-5070:Divide by zero error.⏎TBR-15163: Unhandled exception at  line 2.` |
| `SELECT 1/0 FROM DUAL` | -5070 | 위치 없음 |
| `SELECT 1 FROM DUAL WHERE 1 = 'a'` | -5074 | 위치 없음 |
| `SELECT … INTO` 2행 | -15114 | `ODP-15114:Too many rows returned.⏎TBR-15163: Unhandled exception at  line 1.` |
| `SELECT … INTO` 0행 | -15104 | `ODP-15104:No matching data found.⏎TBR-15163: …` |
| `RAISE_APPLICATION_ERROR(-20001, 'boom')` | -20001 | `ODP-20001:boom.⏎TBR-15163: …` |
| 취소·`CommandTimeout`(앞선 실측) | -12040 | `ODP-12040:Statement canceled.` |

위치 규칙(S4 가 쓰는 것):
- `at line L` 의 L = 문장 원문 기준 1부터 센 줄.
- 열은 `^` 줄의 첫 `^` 위치(1부터)를 쓴다. 보고된 `column C` 는 `^` 와 같거나(문법 오류) 1 크다(객체·식별자 오류: `^` 는 이름 첫 글자, C 는 그다음). `^` 줄이 없으면 C 를 쓴다.
- `Unhandled exception at  line L.` (PSM 실행 오류) = 줄만 있다 → 그 줄 첫 글자.
- 메시지 줄 끝에 공백이 붙어 온다(`"…found.   "`).

## 7. 구현에 주는 결론

1. 세션 관리자 연결 문자열에 `Pooling=false` 를 넣는다(S1). 기본 풀링이면 닫아도 서버 세션이 남는다.
2. 오류 분류기는 `TiberoException`(Number) 과 `TbOdpException`(ErrorCode) 둘 다 본다(S4). 접속 시험(`TiberoConnectionProbe`)도 `TbOdpException` 을 잡아야 한다(지금은 `DbException` 만 잡아 접속 실패가 예외로 샌다).
3. 끊김(`DbErrorKind.Disconnected`) = 서버 12001·12002·12094·2069 + 드라이버 90400·90402·90403·90405~90410·90603·90643·590719·590720·590729·590730. 접속 거부(90401)·DB 이름 불일치(12079)는 접속 실패라 끊김으로 보지 않는다(Oracle 의 12541 과 같은 취급).
4. ServerInfo: `Version` = `V$VERSION` 조립값(`6`), `SessionId` = V$MYSTAT, `DatabaseName`·`DbUniqueName` 자리 = DB_NAME, `InstanceName`·`HostName` = SYS_CONTEXT, `ServiceName`·`ContainerName` = null, `IsRac` = `V$PARAMETERS.CLUSTER_DATABASE`(읽기 실패면 false), `Dbms = Tibero`(S2).
5. 세션 식별 = Oracle 과 같은 PL/SQL 블록(SET_MODULE + SET_IDENTIFIER). NLS 는 `NLS_LANGUAGE`·`NLS_TERRITORY` 를 빼고 적용한다(S3).
