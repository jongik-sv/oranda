# Tibero 실행 실측 (TDP.NET 1.1.35, 서버 6.2.15)

ME(tb-exec), 2026-11-09. 읽기만(SELECT·PSM 블록·트랜잭션 제어). 쓰기·DDL 없음. 탐침 프로그램은 리포 밖(드라이버를 리포에 넣지 않음, 접속 정보는 출력하지 않음). 결정 = D-046, 설계 = [tibero.md](tibero.md).
트랜잭션 유무는 쓰기 없이 `DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE)`(PSM 블록 안)로 트랜잭션을 열고 다음 문장에서 `LOCAL_TRANSACTION_ID` 를 읽어 판정했다. `v$mystat` 의 `autocommit`·`number of begin tx` 값이 같은 결론을 뒷받침한다.

## 1. 자동 커밋

| 항목 | 실측 | 실행 서비스(E1)에 주는 뜻 |
|---|---|---|
| `TiberoConnection.AutoCommit` | **속성 없음**(ODP.NET 과 다름) | `conn.AutoCommit = false` 로 끌 수 없다 |
| 트랜잭션 객체 없이 실행 | 문장마다 자동 커밋(다음 문장의 트랜잭션 id = NULL) | 그대로 두면 SAFE-04(수동 커밋 기본) 위반 |
| `BeginTransaction()` 뒤 | 자동 커밋 꺼짐. `cmd.Transaction` 을 넣지 않아도 같은 연결의 모든 명령에 걸린다 | 세션 준비에서 한 번 `BeginTransaction()` 하고 연결이 닫힐 때까지 둔다 |
| `BeginTransaction()` 안에서 SQL `COMMIT`·`ROLLBACK` | 트랜잭션 끝, 하지만 **수동 모드는 그대로**(다음 문장이 새 트랜잭션을 연다). 뒤의 `tx.Rollback()` 도 오류 없음 | 사용자·세션의 커밋·롤백은 SQL 문장(`COMMIT`/`ROLLBACK`)으로 보낸다 |
| `tx.Commit()`·`tx.Rollback()`·`tx.Dispose()` | 트랜잭션 끝 + **자동 커밋으로 돌아감**. `Dispose()` 만 해도 미커밋 내용이 사라짐(롤백) | 세션 커밋·롤백에 `tx.Commit()` 을 쓰면 다음 문장부터 자동 커밋 → 쓰면 안 된다(쓰면 곧바로 `BeginTransaction()` 다시) |
| `BeginTransaction()` 두 번 | `InvalidOperationException`(이미 트랜잭션 안) | 세션당 한 번만 |
| 기본 격리 수준 | `ReadCommitted` | |

**tb-session 확인 필요**: 세션의 커밋·롤백(`TiberoSession`)이 `TiberoTransaction` 메서드를 쓰면 위 표대로 자동 커밋으로 돌아간다. 연결을 연 쪽이 `BeginTransaction()` 을 잡고, 커밋·롤백은 SQL 문장으로 보내는 규칙을 두 레인이 같이 쓴다.

## 2. 트랜잭션 추적·읽기 전용

| 항목 | 실측 |
|---|---|
| `DBMS_TRANSACTION` | 있음(SYS 패키지 + PUBLIC 동의어). 프로시저 = `COMMIT`·`LOCAL_TRANSACTION_ID`·`ROLLBACK`·`ROLLBACK_SAVEPOINT`·`SAVEPOINT`. **`READ_ONLY` 없음**(호출하면 TBR-15146 PSM 컴파일 오류) |
| `SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual` | 됨. 트랜잭션 없으면 NULL, 있으면 `12.66.243778` 모양 글 → Oracle 과 같은 추적 문장을 쓴다 |
| `LOCAL_TRANSACTION_ID(TRUE)` 를 SQL 안에서 | TBR-8026(BOOLEAN 인자는 SQL 에서 못 씀). PSM 블록 안에서는 됨 |
| `V$TRANSACTION` | 있음. 세션 열 = `SESS_ID`(Oracle 의 `SES_ADDR` 아님). 예비 경로로만 둔다 |
| 내 SID | `SELECT sid FROM v$mystat WHERE ROWNUM = 1` |
| 최상위 `SET TRANSACTION READ ONLY` | **TBR-7001 문법 오류**(대소문자 무관). `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` 는 됨 |
| `BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;` | 됨. `BeginTransaction()` 안에서 쓰면 읽기 전용 트랜잭션이 남는다(자동 커밋 모드에선 바로 끝나 의미 없음) |
| `EXECUTE IMMEDIATE 'SET TRANSACTION READ ONLY'` | TBR-7001 |

- 읽기 전용 프로필 세션 준비 = Oracle 의 `DBMS_TRANSACTION.READ_ONLY` 대신 `BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;`(문장마다, 수동 모드 안에서).
- **읽기 전용 트랜잭션이 쓰기를 막지 않는다**(쓰기 IT #28, `TiberoBulkMeasureIt.Measure_read_only_transaction_in_psm_block`): 블록 뒤 `INSERT` 성공(행 1), DDL 성공(암시적 커밋으로 끝남), `SELECT … FOR UPDATE` 성공. 읽기 일관성(블록 시점 스냅숏)은 된다 — 다른 세션이 커밋한 행이 안 보인다. `ROLLBACK`·`COMMIT` 이 끝낸다. Oracle 은 ORA-01456 으로 막는다.
- 그래서 Tibero 읽기 전용 프로필의 쓰기 차단은 **정책 관문(`ExecutionPolicy` ReadOnlyProfile)뿐**이다: DML·DDL·PSM·분류 불확실 문장을 서버에 보내기 전에 거부한다(단위 시험 `A_read_only_tibero_profile_is_protected_only_by_the_policy_gate…`). 준비 블록은 스냅숏 일관성만 준다. 남는 틈: 정책 관문이 질의로 통과시키는 `SELECT … FOR UPDATE` 는 Tibero 에서 행을 잠근다(Oracle 은 서버가 거부). 서버 쪽 추가 막이는 T2 에서 안 함(조정 결정, 사용자 보고).
- 사용자가 스크립트에 쓴 최상위 `SET TRANSACTION READ ONLY` 는 서버가 거부한다(TBR-7001 그대로 보인다). 고쳐 보내지 않는다.

## 3. 비질의 실행·바인드·OUT 값

| 항목 | 실측 |
|---|---|
| `ExecuteNonQuery` 반환 | PSM 블록·SELECT·COMMIT·ROLLBACK = -1. DML 행 수는 쓰기 IT 에서 확인 |
| `ExecuteReader` 로 PSM 블록 | 됨. `FieldCount=0`, `RecordsAffected=-1` → 분류 불확실 문장을 리더로 보내는 Oracle 경로를 그대로 쓴다 |
| SELECT 의 `RecordsAffected` | -1 |
| `BindByName` | 있음, 기본 false → Oracle 처럼 true 로 둔다. 같은 이름 두 번(`:a + :a`)도 매개변수 하나로 됨 |
| OUT 값 형식 | `Int32`·`Date` 로 선언해도 `Value` 는 **`TiberoString`**(`Date` 는 NLS 모양 글 `2026/11/09`). NULL 은 `DBNull` 이 아니라 `IsNull=true` 인 `TiberoString`, `ToString()` 은 `"null"` → OUT 값은 `INullable.IsNull` 을 먼저 보고 변환한다(tb-result R1 `TiberoValues`) |
| `CommandTimeout` | 기본 0. **2초로 둬도 지켜지지 않음**(25초 넘게 계속, 질의·PSM 모두) → 시간 제한은 드라이버에 맡기지 않고 토큰(`CancelAfter`) + `Cancel()` 로 건다 |
| 파싱 오류 위치 | `TiberoException.Errors[0]` 에 `ParseErrorOffset` 같은 속성은 없다(속성 = `ArrayBindIndex`·`DataSource`·`Message`·`Number`·`Procedure`·`Source`). 위치는 **메시지**에 실려 온다(「at line L, column C of null」 + `^` 줄, tb-session 실측) → E1 은 `TiberoErrorClassifier.ToDbError(ex, stmt.Text)`(S4)로 Offset·Line·Column 을 채운다 |

## 4. 취소

| 항목 | 실측 |
|---|---|
| 취소 오류 | `TiberoException.Number = -12040`(`ODP-12040:Statement canceled.`) → `TiberoErrorClassifier` 12040 = `Cancelled` 와 맞음 |
| `cmd.Cancel()` 호출 | 0~1ms 에 돌아온다(부르는 스레드를 막지 않음). 실행 중이 아닐 때 불러도 오류 없고 다음 실행에 영향 없음 |
| `ExecuteReaderAsync(ct)`·`ExecuteNonQueryAsync` | **동기**: 문장이 끝날 때까지 부른 스레드를 막고, 돌아올 때 이미 끝난 Task. 토큰은 드라이버가 받아 `Cancel()` 과 같은 효과 → 실행 서비스는 드라이버 호출을 `Task.Run` 으로 옮긴다(UI 스레드가 막히지 않게) |
| 서버가 실제로 멈추는 시간 | **크고 들쭉날쭉**: 10ms(1회) · 15~24초 · 다수가 50~58초. 질의(3중 카티전 `COUNT(*)`)·PSM 무한 루프·`CONNECT BY` 모두 비슷. 끝은 늘 12040 |
| 가져오기 중(리더 열린 뒤 `Read()` 반복) `Cancel()` | **효과 없음**: 행이 계속 온다. 리더 `Dispose()` 는 3ms → 가져오기 멈춤은 클라이언트 쪽(읽기 중단 + 리더 닫기, `FetchCancellation`)으로 한다 |
| 취소 뒤 세션 | 연결 `Open`, `SELECT 1` 정상, 트랜잭션 id 변화 없음 |

- 취소를 눌러도 서버가 최대 1분 가까이 계속 돌 수 있다. 그동안 그 연결은 바쁘다(세션 명령 잠금을 쥔 채). E2 의 화면 처리 = Oracle 과 같게 드라이버가 돌아올 때까지 기다리며 「취소 중」(결정 요청 → 조정 세션).
- 취소 시험용 오래 도는 읽기 질의(SLEEP 없음, `DBMS_LOCK` 없음): `SELECT COUNT(*) FROM all_objects a, all_objects b, all_objects d`(시험 서버 `all_objects` ≈ 3,000행 → 끝나지 않음). 시험은 2초 뒤 취소하고 최대 90초 기다린다. 서버 CPU 를 쓰므로 IT 는 한 번에 하나(실행기 flock).

## 5. DBMS_OUTPUT

| 항목 | 실측 |
|---|---|
| `DBMS_OUTPUT.ENABLE(NULL)`·`PUT`·`PUT_LINE`·`NEW_LINE`·`DISABLE` | 됨. `DISABLE` 뒤 `PUT_LINE` 은 버려진다. `ENABLE(2000)` 에 10,000바이트를 넣어도 넘침 오류 없음 |
| `GET_LINES` 서명 | `(LINES OUT DBMS_OUTPUT.CHARARR, NUMLINES IN OUT NUMBER)`. `DBMSOUTPUT_LINESARRAY` 형식 없음 |
| `GET_LINES(:lines, :n)` 배열 바인드(`PLSQLAssociativeArray`) | **안 됨**(TBR-15146). 일반 PSM 연관 배열 OUT 바인드도 안 됨 → Oracle 의 배열 바인드 경로를 못 쓴다 |
| `GET_LINE(:line, :status)` 반복 | 됨. 비면 `status=1`. 한 줄에 왕복 1회 |
| **채택**: 블록 안 `GET_LINES` → CLOB OUT 으로 묶어 받기 | 됨, 왕복 1회에 최대 N줄. 줄마다 `<길이>:<본문>\n`, NULL 줄 = `-\n`. 한글·5,000자 줄 그대로. `PUT_LINE('a' || CHR(10) || 'b')` 는 서버가 두 줄(`a`, `b`)로 나눠 돌려준다(Oracle 은 한 줄). CLOB OUT 의 `Value` 도 `TiberoString`. 버퍼가 비면 `n=0`, 값은 `IsNull` |
| 블록이 오류로 끝난 뒤 | 오류 앞에서 낸 줄이 버퍼에 남아 거둘 수 있다(Oracle 과 같음) |
| 같은 연결에 질의 리더가 열려 있을 때 거두기 | **안 됨**: CLOB OUT 읽기가 드라이버 안에서 `NullReferenceException`(`TiberoClob.Read`, 로케이터 빔. win144 #20). 같은 때 `SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual`(VARCHAR2)은 됨 → 실행 서비스는 리더가 열린 채(행을 낸 불확실 문장·FOR UPDATE)로는 거두지 않고, 출력은 다음 문장 뒤에 나온다. 거두기의 NRE 는 삼킨다(결과를 깨지 않음) |

거두기 블록(E3):

```sql
DECLARE l DBMS_OUTPUT.CHARARR; n INTEGER := :max; r CLOB;
BEGIN
  DBMS_OUTPUT.GET_LINES(l, n);
  FOR i IN 1..n LOOP
    IF l(i) IS NULL THEN r := r || '-'; ELSE r := r || LENGTH(l(i)) || ':' || l(i); END IF;
    r := r || CHR(10);
  END LOOP;
  :n := n; :r := r;
END;
```

`n < :max` 이면 그만 부른다. 길이는 글자 수(`LENGTH`)라 .NET 문자열 길이와 같다(보조 평면 글자는 IT 로 확인).

## 6. AUTOTRACE 통계(`v$mystat`)

- `v$mystat` 열 = `SID, STAT#, NAME, CLASS, VALUE`(이름이 직접 있다). Oracle 문장 `v$mystat s JOIN v$statname n ON n.statistic# = s.statistic#` 은 **TBR-8026**(`statistic#` 없음) → Tibero 문장 = `SELECT name, value FROM v$mystat WHERE value <> 0`.
- 권한: 시험 계정(일반 사용자)으로 읽힌다. 통계 2,391개.
- D5 판정 = **켠다**(Tibero 이름으로 바꿔 보인다). 질의 1개의 전후 차이로 값이 움직이는 것 확인(`logical reads`·`consistent block gets`·`current block gets`·`parse count`·`execute count`·`sorts (memory)`·`sent message size (byte)`·`user calls`).

| Oracle 이름(`SessionStatistics.Interesting`) | Tibero 이름 |
|---|---|
| session logical reads | logical reads |
| db block gets | current block gets |
| consistent gets | consistent block gets |
| physical reads | physical reads |
| redo size | redo log size |
| user commits | the number of user commits performed |
| parse count (total) / (hard) | 같음 |
| execute count | 같음 |
| sorts (memory) / (disk) | 같음 |
| bytes sent via SQL*Net to client | sent message size (byte) |
| bytes received via SQL*Net from client | **없음**(받은 바이트 통계 없음) → 줄을 뺀다 |
| SQL*Net roundtrips to/from client | user calls(가장 가까움, 이름 아래 「호출 수」로 보인다) |

자리(D5) = `src/Oranda.Tibero/Execution/TiberoSessionStatistics.cs`(`SessionStatisticsSource`: 위 문장 + 오른쪽 이름 표, 받은 바이트 줄 뺌)를 `TiberoSqlPlusCommandProcessor` 가 Core 처리기에 넘긴다. 출력 창에는 Tibero 이름 그대로 보인다(`user calls` 포함). 통계 질의는 실행 서비스의 질의 경로로 읽으므로 실제 값은 E4(질의 경로) 뒤부터 나온다 — 그 전엔 읽기 실패 = 통계 줄 없음.

## 7. 기본 형식(`DbConnection`·`DbCommand`·`DbParameter`·`DbException`)으로 되는 것과 안 되는 것

조정 지시(드라이버 고유 형식 직접 사용을 늘리지 않음)에 맞춰 위 실측을 나눈다.

| 필요 | 기본 형식으로 | 드라이버 고유(어댑터 요청) |
|---|---|---|
| 수동 커밋 | `DbConnection.BeginTransaction()` | — |
| 취소 | `DbCommand.Cancel()`, 토큰 | — |
| 이름 바인드 | — | `TiberoCommand.BindByName = true` |
| CLOB OUT(DBMS_OUTPUT 거두기) | `DbParameter.DbType` 에 CLOB 없음 | `TiberoDbType.Clob` |
| OUT 값 NULL 판정·변환 | `DBNull` 아님 | `INullable.IsNull`(`Tibero.DataAccess.Types`) |
| 오류 번호 | `DbException.ErrorCode` 는 HRESULT(-2147467259) | `TiberoException.Number`(분류기는 이미 리플렉션) |
| LOB 미리 읽기 | — | `TiberoCommand.InitialLOBFetchSize`(tb-result) |

## 8. 남은 실측(쓰기 IT, D3)

- DML 뒤 `ExecuteNonQuery` 행 수, DML 뒤 트랜잭션 id
- 읽기 전용 트랜잭션 안 INSERT 거부 오류 번호
- DDL 뒤 수동 모드 유지 여부(DDL 의 암시적 커밋 뒤 다음 DML 이 새 트랜잭션에 머무는지)
- `SELECT … FOR UPDATE` 뒤 트랜잭션 id

## 9. 실행 서비스 반영(T2 마감, tb-exec E1′~E5·D5)

`src/Oranda.Tibero/Execution/TiberoExecutionService.cs` = `IExecutionService`(`[OrandaService(Dbms = Tibero)]`, Core `ExecutionServiceDispatcher` 가 세션 DBMS 로 고른다). `OracleExecutionService` 이식이고, 이 문서의 실측이 다른 곳만 다르다.

| 항목 | Tibero 구현 | 근거 |
|---|---|---|
| 순서 | 치환(`SubstitutionText` + SQL*Plus DEFINE) → 정책 관문(`PolicyGate`) → SQL*Plus 명령은 처리기로 → 드라이버 | Oracle 과 같음 |
| 세션 준비 | 첫 실행 때 `DBMS_OUTPUT.ENABLE(NULL)` 1회. 읽기 전용 프로필 = 문장마다 `BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;` | 1장(자동 커밋 속성 없음 — 세션이 연 직후 잡은 트랜잭션이 끈다), 2장(최상위 = TBR-7001) |
| 드라이버 호출 | `Task.Run` 안에서 `Execute*Async`(동기) | 3장 |
| 바인드·OUT | tb-result `TiberoBindBuilder`·`TiberoValues.ReadOut`. NUMBER OUT = Varchar2 로 받아 되읽음(결정40) | values 문서 6장 |
| 질의 | 리더 → `TiberoResultSets.Create`(결과 집합이 명령·리더 소유, 페이지마다 명령 잠금). 불확실 문장도 행이 있으면 결과 집합. REF CURSOR OUT = 결과 집합(하나씩 감싸 실패 때 새지 않게) | R2 |
| 트랜잭션 추적 | `SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual`, 공급자 NULL 판정(`IsProviderNull`). 순수 질의 뒤엔 안 보냄 | 2장 |
| DBMS_OUTPUT | 질의가 아닌 문장 뒤(성공·서버 오류 모두) `CollectBlock` 반복(1000줄씩, CLOB 묶음). 리더가 열린 채(행을 낸 불확실 문장·FOR UPDATE)로는 안 거둠 → 다음 문장 뒤. 거두기 실패는 결과를 깨지 않음 | 5장 |
| 취소 | 토큰 → `DbCommand.Cancel()`. 드라이버가 돌아올 때까지(10ms~58초) 호출이 안 끝남 = App 의 「취소 중」. TBR-12040 + 토큰 취소 = Cancelled, 우리 취소 없는 12040 = Failed. 문장 시간 제한 없음(Oracle 도 없음) | 4장, 결정22 |
| 스크립트 | Oracle 루프 이식(Stop/Continue/Ask·ContinueAll, WHENEVER SQLERROR EXIT = `IScriptDirectives`) | — |
| 변경 일지 | DDL 대상마다 `ChangeCapture` + `TiberoDdlSource` = 「보관 안 함 + 이유」 행(`ChangeSnapshotCaptured`), 서버 읽기 없음 | D4 |
| AUTOTRACE 통계 | `TiberoSessionStatistics`(6장) 를 `TiberoSqlPlusCommandProcessor` 가 Core 처리기에 넘김 | D5 |

시험: 단위 = `tests/Oranda.Tibero.Tests/Execution/TiberoExecutionServiceTests.cs`(가짜 드라이버, `FakeDriver` 컬렉션), IT = `TiberoExecutionServiceIt.cs`(읽기만: 바인드·트랜잭션·읽기 전용 준비·DBMS_OUTPUT·질의·스크립트·REF CURSOR·리더 열린 채 거두기 안 함·긴 질의 취소).

### 남은 일(T3 이후)

- 변경 일지 실제 보관: `TiberoDdlSource` 를 DBMS_METADATA 대응 읽기로 바꾼다(T3). 「객체 없음」 번호 실측 필요.
- 쓰기 IT(8장, D3 — `ORANDA_IT_TIBERO_WRITE=1`, ORT_ 표): DML 행 수·트랜잭션 id, 읽기 전용 안 INSERT 거부 번호, DDL 뒤 수동 모드, FOR UPDATE 뒤 트랜잭션 id.
- 리더가 열린 채 출력 거두기: 필요하면 VARCHAR2 OUT(32767바이트 한계) 거두기 블록을 실측해 붙인다(지금은 다음 문장으로 미룸).
- TIMESTAMP WITH (LOCAL) TIME ZONE·BINARY_DOUBLE/FLOAT·INTERVAL OUT 값 = 서버 글자 그대로(tb-result 몫).
- 가짜 드라이버 `TiberoCommand.Cancel()` 호출 횟수(결정62 허가, 아직 안 넣음) — 넣으면 취소 단위 시험이 「Cancel 이 불렸다」를 단언할 수 있다.
- Tibero 대량 실행(`IBulkExecutionService`) 없음 — T5.

## 10. 되돌리기 지점 실측(RB-13, 서버 6.2.15, TDP.NET 1.1.35, 2026-11-13 win30)

시험 = `tests/Oranda.Tibero.Tests/Integration/RollbackDialectMeasureIt.cs`(쓰기 IT, `ORB_RB` 표만 만들고 지움). 설계 = `docs/superpowers/specs/2026-11-13-rollback-point-design.md` 6.6·15장. 측정 시험이라 단언 없이 `RB13 …` 줄을 남긴다.

| 항목 | 결과 | 방언 값 |
|---|---|---|
| `INSERT … RETURNING ROWID INTO :ORANDA_RID` (실행 서비스, OUT Varchar2 크기 미지정) | 성공, 1행, OUT = 18글자 string, `ROWID = CHARTOROWID(값)` 1행. `RETURNING ROWIDTOCHAR(ROWID)` 도 같음 | `SupportsReturningRowId = true` |
| 내부 저장점 `SAVEPOINT ORANDA$RB`(`$` 이름) → `SELECT … FOR UPDATE`(3행) → 사용자 문장 실패(TBR-5074) | 실패 뒤에도 다른 세션 `FOR UPDATE NOWAIT` = TBR-12033(잠금 남음). `ROLLBACK TO SAVEPOINT ORANDA$RB` 뒤 NOWAIT 성공(잠금 풀림). 트랜잭션 id 는 남는다(「미커밋 있음」) | 설계 RD-5 그대로 |
| 같은 이름 저장점 다시 걸기, `ROLLBACK TO ORANDA$RB`(SAVEPOINT 생략) | 됨 | — |
| 모르는 저장점 `ROLLBACK TO SAVEPOINT x` | TBR-21008(저장점 없음) | 분류 Other |
| FOR UPDATE 잠근 행 수 = UPDATE 영향 행 수 | 3 = 3 | — |
| NUMBER `TO_CHAR(c, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')` | 소수 29자리(`12345678901234567890.123456789`)·38자리 정수·음수 소수 모두 그대로, 따옴표 없는 리터럴로 1행 | Oracle 과 같음 |
| DATE `SYYYY-MM-DD HH24:MI:SS` | 0001-01-01 포함 1행(부호 자리는 공백 없이 `2026-…`) | 같음 |
| TIMESTAMP(9) `…FF9` | 9자리 그대로 1행 | 같음 |
| TIMESTAMP(9) WITH TIME ZONE `…FF9 TZR` | 지역 이름(`Asia/Seoul`)·오프셋(`-03:30`) 모두 그대로 읽히고 `TO_TIMESTAMP_TZ(…, 같은 마스크)` 1행 | 같음 |
| TIMESTAMP WITH LOCAL TIME ZONE: `SYS_EXTRACT_UTC(CAST(c AS TIMESTAMP WITH TIME ZONE))` + `CAST(FROM_TZ(TO_TIMESTAMP(…), 'UTC') AS TIMESTAMP WITH LOCAL TIME ZONE)` | 1행 | 같음 |
| INTERVAL DAY(9) TO SECOND(9)·YEAR(9) TO MONTH: `TO_CHAR(c)` → `TO_DSINTERVAL`·`TO_YMINTERVAL` | `+000000003 04:05:06.123456789`·`-000000002-03`, 1행 | 같음 |
| RAW `RAWTOHEX` → `HEXTORAW` | 1행 | 같음 |
| CHAR·NCHAR | 읽은 값이 끝 공백까지 채워 옴(`'ab   '`), 그 글자 그대로 리터럴 1행 | 같음 |
| VARCHAR2 작은따옴표 두 번 | 1행 | 같음 |
| NVARCHAR2·NCHAR `UNISTR('\AC00…')` | 1행. BMP 밖 글자는 서로게이트 쌍 `\D83D\DE00` 이 글자 1개 | 같음 |
| BINARY_DOUBLE 열 그대로(.NET double) → `TO_BINARY_DOUBLE('R 형식')` | 1행. `TO_CHAR(c)` 글자(`3.3333333333333333E-002`)도 1행 | 같음 |
| **BINARY_FLOAT** 열 그대로(.NET float `0.1`) | `TO_BINARY_FLOAT('0.1')` = 1행, **`TO_BINARY_DOUBLE('0.1')` = 0행**. `TO_CHAR(c)`(`1.00000001E-001`) + `TO_BINARY_FLOAT` 도 1행 | `TO_BINARY_FLOAT` (설계 6.6 표의 `TO_BINARY_DOUBLE` 은 FLOAT 열에서 0행 — 조정자 보고) |
| NaN·무한대 상수 `BINARY_DOUBLE_NAN`·`BINARY_FLOAT_INFINITY`·`-BINARY_DOUBLE_INFINITY` | 넣기·`c = 상수` 1행. NaN 의 `TO_CHAR` = `Nan` | 같음 |
| ROWID `ROWIDTOCHAR(ROWID)` → `CHARTOROWID('…')`, `ROWID IN (…)` | 18글자, 1행 | 같음 |
| 큰 열 NULL 여부 `CASE WHEN c IS NULL THEN 1 ELSE 0 END` | 됨 | 같음 |
| 시점 읽기 세 방법 | `GET_SYSTEM_CHANGE_NUMBER` 이 `TIMESTAMP_TO_TSN(SYSTIMESTAMP)` 보다 12 크다(시각→TSN 은 몇 초 늦다). `V$DATABASE.CURRENT_TSN` 은 이 계정에서 됨 | 순서 그대로(정확한 방법 먼저) |
| `AS OF TSN n`·`AS OF SCN n`(정확한 TSN) | 둘 다 됨 | `AsOf` = `AS OF TSN n` |
| `AS OF TSN` 이 표를 만들기 전 시점(늦은 `TIMESTAMP_TO_TSN`) 또는 시점 뒤 DDL(`ALTER TABLE ADD`) | **TBR-21003 「Snapshot is too old」** — Oracle 의 ORA-01466(정의 바뀜) 자리에도 이 번호가 나온다 | 21003 → `SnapshotTooOld`. 정의 바뀜 번호는 따로 없다(`DefinitionChangedNumbers` 빈 목록) |
| `AS OF TSN 1`(아주 옛 시점) | TBR-21034 「지정한 시각에 맞는 스냅숏 없음」(ORA-08180 자리) | 21034 → `SnapshotTooOld` |
| `AS OF TSN` 미래 값(지금 + 10⁹) | **오류 없음**, 지금 데이터(Oracle 은 ORA-08181) | 잘못된 시점 번호 없음(`InvalidPointNumbers` 빈 목록). 시점 직접 지정은 앱이 지금 TSN 이하인지 먼저 본다 |
| `AS OF TSN -5` | TBR-5072(NUMBER 변환) | — |
| `VERSIONS BETWEEN TSN MINVALUE AND MAXVALUE` | TBR-8022(구문) — catalog-map 과 같음 | 화면 3 은 WHERE 범위부터(RD-15) |
| DDL 암묵 커밋 | 성공 DDL, 의미 오류로 실패한 DDL(TBR-7102 이미 있음·7454 형식 없음·7071 객체 없음)은 앞 INSERT 를 **커밋**(트랜잭션 id NULL, 다른 세션에 보임). 구문 오류(TBR-7001)는 커밋 안 함(id 남음, 안 보임). Oracle 과 같은 모양 | 끝 판정 = 「모름 → 판정」 그대로 |
| 트랜잭션 id 모양 | `99.99.999999`(usn.slot.sqn) | — |
| 식별자 최대 길이(ASCII) | 128 바이트 됨, 129 = TBR-23007 | `MaxIdentifierBytes = 128` |
| `ALL_TRIGGERS`(TABLE_OWNER·TABLE_NAME·STATUS·TRIGGERING_EVENT·TRIGGER_TYPE), `ALL_CONSTRAINTS.DELETE_RULE` | 열 있음 | 부작용 찾기 = Oracle 과 같은 사전 |
| 가상 열 `GENERATED ALWAYS AS (…) VIRTUAL`·`B AS (…)` | 됨. `ALL_TAB_COLS.VIRTUAL_COLUMN` = **`Y`/`N`**(Oracle `YES`/`NO`), `HIDDEN_COLUMN` 도 `N` | `meta.columns` 가 이미 `YES`/`NO` 로 바꿔 `IsVirtual` 을 채운다 → DELETE 원복 INSERT 가 뺀다 |
| 숨김 열 `INVISIBLE` | TBR-7001(없음) | `IsInvisible` 늘 거짓 |
| IDENTITY 열 | 없음(`ALL_TAB_IDENTITY_COLS` TBR-8103, `IDENTITY_COLUMN` 열 TBR-8026 — copy 사전 실측과 같음) | `IsIdentity` 늘 거짓 |
| 부작용 찾기 `TiberoRollbackValues.SideEffectsSql`(바인드 `:owner`·`:name` 이름 바인드) | 켜진 트리거(`TRIGGER 이름`)와 ON DELETE CASCADE 자식 FK(`FK CASCADE 자식표.FK`)를 찾음 | 그대로 |
| `AS OF TSN n`(`TiberoRollbackValues.AsOf`, `GET_SYSTEM_CHANGE_NUMBER` 로 읽은 n) | 그 뒤 커밋한 UPDATE 앞 값을 읽음 | 그대로 |
