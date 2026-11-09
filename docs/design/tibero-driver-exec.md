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
- 읽기 전용이 쓰기를 실제로 막는지는 쓰기 시험이라 여기서 재지 않았다 → 쓰기 IT(D3, `ORANDA_IT_TIBERO_WRITE=1`, `ORT_` 표)에서 「읽기 전용 트랜잭션 안 INSERT 가 실패」로 확인한다.
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
