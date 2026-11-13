# 되돌리기 지점 설계 (원복 스크립트·실행 지점 기록·시점 비교)

작성일: 2026-11-13. 상태: 설계(구현 전). 관련: TSK-13-05 플래시백, TSK-13-07 변경 일지, TSK-13-31·TSK-13-44 변경 추적, SAFE-03 운영 확인, D-046(오류는 종류로 판단), D-065(개발 접속 확인 생략).

표기: **[사용자]** = 사용자가 결정함, **[설계]** = 시안에서 정하고 사용자가 확인함, **[설계자 결정 RD-n]** = 사용자가 설계자에게 맡겨 이 문서에서 정함(사용자가 뒤집을 수 있음, 목록은 20장), **[구현]** = 구현자에게 맡김.

시안(HTML 3판, 사용자 승인): `rollback-proposal.html`(세션 scratchpad). 화면 1 변경 일지, 화면 2 원복 스크립트, 화면 3 이 실행 전과 비교, 화면 4 운영 확인 창, 화면 5 설정. 이 문서는 시안을 정본으로 삼고, 시안이 정하지 않은 동작과 모듈 경계·공개 계약을 더한다.

## 1. 목적과 이유

SQL 편집기에서 UPDATE·DELETE·INSERT 를 실행하고 커밋하면, 지금 Oranda 에는 되돌릴 수단이 없다. 어느 시점으로 돌아가야 하는지조차 기록되지 않아, 사용자가 시각을 추측해 `AS OF` 질의를 직접 써야 한다. 그리드 편집의 「이전 값으로 되돌리기」는 그리드에 남은 행만 「N분 전」 기준으로 되돌린다. 지운 행은 되살리지 못하고, 새로 넣은 행은 건너뛴다. DDL 은 변경 일지가 이전 DDL 을 보관하지만, TRUNCATE 와 DROP COLUMN 으로 사라진 데이터는 살리지 못한다.

이 기능은 쓰기 문장을 실행할 때 두 가지를 남긴다.

- 운영 접속: 문장을 거꾸로 되돌리는 **원복 스크립트**를 함께 만든다. UPDATE 의 원복은 이전 값으로 되돌리는 UPDATE, DELETE 의 원복은 지운 행을 넣는 INSERT, INSERT 의 원복은 넣은 행을 지우는 DELETE 다 **[사용자]**.
- 모든 접속: 실행 직전 시점 번호(Oracle SCN, Tibero TSN)를 기록한다. 원복 스크립트가 없는 실행은 이 시점과 지금 데이터를 견주어 되돌린다 **[설계]**.

원복 스크립트는 운영 접속에서만 만든다. 개발·테스트 접속은 SCN 만 기록하고, 되돌릴 때는 시점 비교(화면 3)를 쓴다 **[사용자, 2026-11-12]**. 원복 스크립트는 같은 조건으로 한 번 더 읽고 잠그므로 실행이 느려지는데, 이 비용은 실수가 비싼 운영 접속에서만 치르겠다는 판단이다.

## 2. 목표

- 운영 접속에서 UPDATE·DELETE·한 행 INSERT … VALUES 를 실행하면, 커밋 한 번 단위의 원복 스크립트가 이 PC 에 암호화되어 남는다.
- 모든 접속에서 쓰기 문장을 보내기 직전의 시점 번호와 커밋 시점 번호가 변경 일지에 남는다.
- 변경 일지에서 커밋 묶음을 골라 원복 스크립트를 열거나(화면 2), 기록한 시점과 지금을 비교해 되돌린다(화면 3).
- 되돌리기는 자동으로 실행하지 않는다. 미리 본 뒤 커밋하지 않은 상태로 반영하고, 커밋이나 롤백은 사용자가 한다 **[설계]**.
- 그 뒤 다른 사람이 다시 바꾼 행은 덮어쓰지 않는다. 원복 스크립트에서는 그런 행이 0행으로 끝나고, 시점 비교에서는 충돌로 따로 표시되어 기본으로 제외된다 **[설계]**.
- Oracle 과 Tibero 가 같은 기능 수준이다. 차이는 공급자 방언과 실측 항목에서만 다룬다(15장).
- 원복 스크립트의 생성 실패, 시점 읽기 실패는 사용자의 실행을 막지 않는다. 이유는 실행 로그와 일지에 남긴다.

## 3. 비목표

- `FLASHBACK TABLE … TO SCN`. 빠르지만 다른 사람의 변경까지 지우고, ROW MOVEMENT 를 켜는 DDL 이 필요하다. 원복 스크립트와 시점 비교가 미리보기와 함께 같은 결과를 낸다 **[설계자 결정 RD-2]**(TSK-13-05 의 제외 결정을 유지).
- Flashback Transaction Query(`UNDO_SQL`), LogMiner, 복원 지점, FLASHBACK DATABASE. DBA 권한이 필요하다.
- 서버에 객체를 설치하는 방법(로그 테이블, 트리거, 임시 테이블). PRD C-05 를 따른다. 예외는 사용자가 확인 창에서 고르는 백업 테이블(CTAS, 9장) 하나뿐이다.
- 접속 간 데이터 복사(`IBulkExecutionService`)의 원복. 복사 대상은 운영 접속이 될 수 없고(데이터 복사 설계 4장), 대상 처리가 다시 실행해도 같은 결과를 낸다.
- 첫 단계의 원복 스크립트 대상 밖 문장: MERGE, PL/SQL 블록 안의 DML, 뷰를 통한 DML, DB 링크 너머의 표, INSERT … SELECT, 대상이 인라인 뷰인 문장. 이 문장들은 SCN 만 기록한다 **[설계]**.
- 트리거·ON DELETE CASCADE 로 다른 표가 함께 바뀐 것의 원복. 확인 창에서 미리 알리기만 한다 **[설계]**.
- 앱이 죽은 뒤 원복 스크립트를 이어 만들기. 커밋하지 않은 트랜잭션은 서버가 되돌리므로 잃을 것이 없다(7.4).

## 4. 반목표 (이렇게 되면 실패)

- 원복 스크립트 생성이나 시점 기록 때문에 사용자 문장이 실패하거나, 실행되지 않거나, 다른 결과를 낸다.
- 사용자 문장이 실패했는데, 원복 스크립트를 만들려고 건 행 잠금이 트랜잭션 끝까지 남는다(6.2 의 내부 저장점이 이것을 막는다).
- 원복 스크립트나 시점 비교가 그 뒤 다른 사람이 바꾼 값을 말없이 덮어쓴다.
- 롤백한 트랜잭션의 원복 스크립트가 「확정」으로 남아, 실행하면 지우지 않은 행을 다시 넣는다.
- 행 데이터가 평문으로 디스크에 남는다. 원복 스크립트의 값은 암호화한 파일에만 있고, `oranda.db` 에는 가린 문장 요약과 상태만 있다.
- 리터럴이 정확하지 않아(소수 자리, 시간대, 국가 문자) 「실행 직후 값」 조건이 맞지 않는다. 그러면 모든 원복 문장이 0행으로 끝나는데, 겉으로는 「다른 사람이 바꾼 행」과 구분되지 않는다. 6.6 의 리터럴 규칙과 왕복 시험(16장)이 이것을 막는다.
- 개발·테스트 접속에서 원복 스크립트가 만들어진다. 이 접속에서는 설정으로도 켤 수 없다 **[사용자]**.

## 5. 제약

- 추가 권한 없이 동작한다. 원복 스크립트는 문장 대상 표의 SELECT 권한과 FOR UPDATE 권한만 쓴다. DML 권한이 있으면 FOR UPDATE 도 된다. 시점 비교는 내 표면 추가 권한이 없고, 다른 스키마 표는 FLASHBACK 권한이 필요하다.
- 사용자에게 보이는 쓰기(사용자 문장, 원복 실행, 시점 비교 반영, 백업 테이블 CTAS)는 지금처럼 실행 엔진(`IExecutionService`)의 정책 관문을 지난다. 원복 실행과 비교 반영도 사용자 문장과 같은 경로다.
- 기능 안쪽의 문장(시점 읽기, `COUNT(*)`, 트리거 찾기, 잠금 읽기 `SELECT … FOR UPDATE`, 실행 직후 값 읽기, 끝 판정 질의, 내부 `SAVEPOINT ORANDA$RB`·`ROLLBACK TO`)은 정책 관문을 거치지 않고 `IRollbackChannel` 로 연결에 직접 보낸다. 사용자가 확인한 문장에 딸린 일이기 때문이며, 변경 일지가 DDL 앞에서 `DBMS_METADATA` 를 읽는 방식(`ChangeCapture`)과 같다. 이 문장들을 `IExecutionService` 로 보내면 명령 잠금을 다시 잡아 교착되거나 확인 창이 다시 뜬다. 이 문장들은 실행 이력에도 남기지 않는다.
- 실행 엔진 안에서는 세션의 명령 잠금(D-041)을 쥔 채 연결에 직접 보낸다. 서비스 API(`IMetadataProvider` 등)를 다시 부르면 같은 잠금을 다시 잡아 교착된다. 그래서 메타데이터와 예상 행 수는 정책 관문 **앞**, 잠금 **밖**에서 읽는다(6.1).
- Core 는 다른 Oranda 어셈블리를 참조하지 않는다(`LayerDependencyTests`). 문장 모양 분석은 Language 가 구현하고 Core 인터페이스로 넘긴다.
- 의미 판단은 오류 번호가 아니라 `DbErrorKind` 로 한다(D-046). DBMS 별 번호는 공급자 분류기와 방언에 둔다.
- 시험 데이터·접속 이름은 예시 값만 쓴다(`prod-example`, `dev-example`, `SAMPLE.PRODUCTS`).

## 6. 층 A: 원복 스크립트 (운영 접속만)

### 6.1 문장 하나의 흐름

공급자 실행 서비스(`OracleExecutionService`·`TiberoExecutionService`)의 `RunAsync` 순서에 단계 셋이 끼어든다. 굵은 글씨가 새 단계다.

1. 치환 변수 처리.
2. **문장 모양 분석과 제안(`IRollbackCapture.PrepareAsync`)**: 명령 잠금 밖에서 한다. 쓰기 문장이면 `IDmlAnalyzer` 로 치환 뒤 글의 모양(`DmlShape`)을 얻는다. 운영 접속이고 설정이 켜져 있고 대상 문장이면 메타데이터를 읽고(시노님 풀기, 열·PK·가상·ID 열), 트리거·CASCADE FK 를 찾고, 같은 WHERE 로 `COUNT(*)` 를 돌려 예상 행 수를 얻는다. `COUNT(*)` 는 10초 제한이다(`ChangeCapture.Timeout` 과 같은 값). 넘거나 권한이 없으면 「예상 행 수 알 수 없음」 이고 실행은 계속한다 **[설계자 결정 RD-11]**.
3. 정책 관문(`PolicyGate.DecideAsync`): 2단계의 제안(`ConfirmExtras`)을 확인 창에 넘기고, 사용자의 답(`ConfirmAnswer`)을 받는다(10장).
4. 명령 잠금 잡기.
5. **시작(`IRollbackCapture.BeginAsync`)**: 층 B 의 시점을 읽는다(7.1). 원복 스크립트를 만들기로 했으면 내부 저장점을 걸고 잠금 읽기를 한다(6.2 의 1단계).
6. 드라이버 실행: `IStatementCapture.StatementToSend` 를 보낸다. 한 행 INSERT 면 `RETURNING ROWID INTO` 가 붙은 글이다.
7. 트랜잭션 추적(`LOCAL_TRANSACTION_ID`, 지금 코드 그대로). 읽은 트랜잭션 id 를 다음 단계에 넘긴다.
8. **마무리(`IStatementCapture.CompleteAsync` / 실패·취소면 `AbandonAsync`)**: 행 수 대조, 실행 직후 값 읽기, 원복 문장 만들기, 원장(7.3)에 기록. 결과 요약을 `ExecutionResult.Rollback` 에 싣는다.

사용자 화면에는 변화가 거의 없다. 실행 로그에 「원복 스크립트 12문장(운영 접속, 커밋하면 확정)」 한 줄이 더해진다 **[설계]**. `ExecutionResult.Statement` 와 실행 이력에는 사용자가 쓴 원래 글이 남는다. `RETURNING` 을 붙였으면 실행 로그에 그 사실을 한 줄 남긴다 **[설계자 결정 RD-1]**.

### 6.2 실행 순서 네 단계 (UPDATE 예)

| 단계 | 보내는 문장 | 하는 일 |
|---|---|---|
| 0 내부 저장점 | `SAVEPOINT ORANDA$RB` | 잠금 읽기 앞에 건다. 사용자 문장이 실패·취소되면 `ROLLBACK TO SAVEPOINT ORANDA$RB` 로 잠금을 푼다 **[설계자 결정 RD-5]** |
| 1 먼저 잠그고 읽기 | `SELECT ROWID, <키 열>, <SET 열> FROM <문장의 표 참조 그대로> WHERE <문장의 WHERE 그대로> FOR UPDATE` | 사용자 문장과 같은 바인드를 쓴다. 상한 + 1 행까지만 읽는다 |
| 2 사용자 문장 실행 | 사용자 문장 | 영향 행 수와 1단계에서 잠근 행 수를 견준다(6.4) |
| 3 실행 직후 값 읽기 | `SELECT ROWID, <키 열>, <SET 열> FROM <표> WHERE ROWID IN (:r1 … :r1000)` | 1000개씩 나눈다. 커밋 전이라 내 세션이 바꾼 값이 보인다. 이 값을 원복 문장의 조건에 넣는다 |
| 4 트랜잭션 끝 | `COMMIT` / `ROLLBACK` / `ROLLBACK TO 저장점` | `COMMIT` → 묶음 확정, `ROLLBACK` → 묶음 무효, `ROLLBACK TO` → 그 저장점 뒤 문장만 무효(7.3) |

- 0단계가 필요한 이유: 1단계의 `SELECT … FOR UPDATE` 는 사용자 문장과 별개의 문장이다. 그래서 사용자 문장이 실패해 문장 단위로 되돌려져도 1단계가 건 행 잠금은 풀리지 않고, 운영 DB 의 다른 세션을 트랜잭션이 끝날 때까지 막는다. 내부 저장점으로 되돌리면 1단계 이후의 잠금만 풀린다. 그 결과 사용자 문장이 실패한 뒤에도 세션이 「미커밋 있음」으로 보일 수 있다. 화면은 이 경우 「원복 스크립트용 잠금은 풀었습니다」를 실행 로그에 남긴다. 저장점 이름은 사용자가 쓰지 않을 이름(`ORANDA$RB`)을 쓰고, 문장마다 같은 이름으로 옮긴다. Oracle 에는 저장점 해제가 없으므로 성공 뒤에는 그대로 둔다.
- 1단계의 표 참조와 WHERE 는 문장 글에서 그대로 잘라 쓴다(`DmlShape.TableText`, `WhereText`). 그래서 파티션 절과 별칭이 그대로 들어간다. WHERE 가 없는 UPDATE·DELETE 는 WHERE 없이 읽는다(정책이 이미 확인을 받았다).
- 1단계가 다른 세션의 잠금을 기다리면 사용자 문장도 어차피 기다린다. 기다리는 동안 취소하면 지금처럼 드라이버 취소로 끝나고, 0단계 저장점으로 되돌린다.
- 3단계에서 PK 를 바꾸지 않은 UPDATE 는 PK 로 읽어도 되지만, 행 수가 같으므로 ROWID 로 읽는다. 파티션 키를 바꿔 행이 다른 파티션으로 옮겨지면(ROW MOVEMENT) ROWID 가 바뀌어 일부 행을 읽지 못한다. 그러면 그 문장을 「불완전」으로 둔다.

DELETE 는 3단계가 없다(행이 없어졌다). 한 행 INSERT … VALUES 는 0·1단계가 없고, 2단계에서 `RETURNING ROWID INTO :ORANDA_RID` 로 받은 ROWID 로 3단계를 한다. 3단계는 모든 열을 읽어 기본값·트리거·시퀀스로 채워진 값까지 잡는다.

### 6.3 문장별 원복 규칙

| 실행한 문장 | 원복 문장 | 먼저 읽는 것 | 원복 문장의 조건 |
|---|---|---|---|
| UPDATE | 이전 값으로 되돌리는 UPDATE(행마다 한 문장) | ROWID, 키 열, SET 대상 열 | 키 = 실행 직후 키 값 **그리고** SET 대상 열 = 실행 직후 값. PK 를 바꾼 UPDATE 와 PK 없는 표는 ROWID 를 쓴다 |
| DELETE | 지운 행을 넣는 INSERT(행마다 한 문장) | ROWID 와 모든 열 | 없음. 같은 키가 이미 있으면 ORA-00001 이 나며, 원복 실행이 이것을 충돌로 알린다 |
| INSERT … VALUES (한 행) | 넣은 행을 지우는 DELETE | 없음. 실행 뒤 `RETURNING ROWID` 로 찾아 읽는다 | PK 가 있으면 PK = 넣은 값 **그리고** 나머지 열 = 넣은 값. PK 가 없으면 모든 열 = 넣은 값 **그리고** `ROWNUM = 1`. NULL 은 `IS NULL` **[설계자 결정 RD-1]** |
| INSERT … SELECT | 만들지 않음 | — | 묶음을 「불완전 · INSERT … SELECT」 로 표시하고, 되돌릴 때는 시점 비교(화면 3)로 넘긴다 **[설계자 결정 RD-1]** |
| MERGE, PL/SQL, 뷰, DB 링크, 인라인 뷰 대상, `RETURNING`·`LOG ERRORS` 가 이미 있는 문장, 여러 행 VALUES, INSERT ALL | 만들지 않음 | — | 「만들지 않음 · 대상 아님(MERGE)」 처럼 이유를 적고 SCN 만 기록한다 |

세부 규칙:

- UPDATE 에서 이전 값과 실행 직후 값이 모든 SET 열에서 같은 행은 원복 문장을 만들지 않는다(바뀐 것이 없다).
- 원복 UPDATE 의 SET 에는 SET 대상 열만 넣는다. 트리거가 같은 행의 다른 열(예: 수정 시각)을 바꿨다면 그 열은 되돌리지 않는다. 확인 창이 트리거가 있다고 미리 알린다.
- DELETE 의 원복 INSERT 는 가상 열(`IsVirtual`)을 빼고, 숨김 열(`IsInvisible`)은 이름을 적어 넣는다. ID 열(`IsIdentity`)이 있으면 `GENERATED ALWAYS` 일 때 ORA-32795 로 거부되므로, 그 문장을 「불완전 · ID 열」로 둔다(원복 문장은 만들어 두고 주석으로 알린다) **[설계자 결정 RD-12]**.
- 큰 열(LOB·LONG·XMLTYPE·JSON·VECTOR·객체 형식)은 조건에 넣지 않는다. DELETE 로 지운 행의 큰 열 값이 NULL 이 아니면 그 행은 원복 INSERT 를 만들 수 없어 문장을 「불완전 · 큰 열」로 둔다 **[설계, RD-12 로 행 단위로 좁힘]**.
- PK 가 없는 표에서 한 묶음 안의 두 문장이 같은 행을 건드리면(예: UPDATE 뒤 DELETE), 되살린 행은 ROWID 가 바뀌어 앞 문장의 원복이 그 행을 찾지 못한다. 엔진이 ROWID 겹침으로 이것을 알아보고 묶음을 「불완전 · PK 없는 행을 두 번 바꿈」으로 둔다 **[설계자 결정 RD-13]**.
- PK 가 있는 표에서는 지웠다가 되살린 행도 PK 로 찾으므로 역순 실행이 맞게 돈다(시안 화면 2 의 1040 행).
- `RETURNING ROWID INTO :ORANDA_RID` 는 문장에 같은 이름의 바인드가 없을 때만 붙인다. 공급자 방언이 이 기능을 확인하지 않았으면(`SupportsReturningRowId = false`, Tibero 실측 전) 붙이지 않고 문장을 「불완전 · RETURNING 미지원」으로 둔다.

### 6.4 행 수 대조

1단계에서 잠근 행 수와 2단계의 영향 행 수가 다르면 문장을 「불완전 · 행 수 다름(잠금 12, 실행 13)」으로 둔다. 다를 수 있는 경우는 WHERE 에 `SYSDATE`·`ROWNUM`·`DBMS_RANDOM` 이 있거나, 두 문장 사이에 다른 세션이 조건에 맞는 행을 넣고 커밋한 경우다. 이때 실행 로그에 경고를 남기고, 설정 「잠근 행 수와 실행한 행 수가 다르면 커밋 전에 알리기」가 켜져 있으면 커밋할 때 다시 묻는다(10.3) **[설계자 결정 RD-18]**.

### 6.5 상한

문장당 상한은 기본 100,000행이다(설정, 1~1,000,000). 2단계의 예상 행 수가 상한을 넘으면 확인 창의 「원복 스크립트 만들기」가 꺼지고 「상한을 넘어 원복 스크립트 없이 실행합니다(예상 123,456행 > 100,000)」가 보인다. 사용자는 실행하거나 취소한다 **[설계]**. 예상 행 수를 몰랐거나 그 사이에 늘어 1단계가 상한 + 1 행을 읽으면, 읽기를 멈추고 원복 스크립트 없이 실행한다(「만들지 않음 · 상한 넘음」). 이미 건 잠금은 사용자 문장이 어차피 건다. 기본값은 성능 시험(RB-90)의 결과로 다시 정한다.

### 6.6 값 읽기와 리터럴 **[설계자 결정 RD-4]**

원복 문장은 사람이 읽고 고칠 수 있게 바인드 대신 리터럴로 쓴다. 리터럴의 모양(`TO_DATE`·`TO_TIMESTAMP`·`HEXTORAW`·`CHARTOROWID`, 작은따옴표 두 번)은 지금 플래시백(`FlashbackTrackSql.Literal`)과 「INSERT 문으로 복사」(`CellValueFormatter.ToSqlLiteral`)가 쓰는 규칙을 그대로 따른다 **[사용자]**. 다만 두 함수는 .NET 값으로 바꾼 뒤의 값을 받으므로 정밀도가 줄 수 있다(TIMESTAMP 는 6자리, NUMBER 는 28자리 남짓에서 `decimal` 이 넘친다). 원복 문장의 조건은 값이 한 자리만 달라도 0행이 되므로, 값은 서버에서 고정 형식의 글자로 읽고 리터럴은 그 글자를 형식 함수로 감싼다. 이 일은 공급자 방언(`IRollbackDialect.ReadExpression`·`Literal`)이 한다.

| 형식 | 읽는 식(1·3단계 SELECT 목록) | 리터럴 |
|---|---|---|
| VARCHAR2·CHAR | 열 그대로 | `'…'` (4000바이트를 넘으면 불완전) |
| NVARCHAR2·NCHAR | 열 그대로 | `UNISTR('…')` (ASCII 밖 글자는 `\XXXX`). DB 문자셋 밖 글자가 SQL 글을 지나며 깨지지 않게 한다 |
| NUMBER·FLOAT | `TO_CHAR(c, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')` | 따옴표 없는 숫자 |
| BINARY_FLOAT·BINARY_DOUBLE | 열 그대로(.NET float·double) | `TO_BINARY_DOUBLE('R 형식')`, NaN·무한대는 `BINARY_DOUBLE_NAN`·`BINARY_DOUBLE_INFINITY`(`-` 붙임) |
| DATE | `TO_CHAR(c, 'SYYYY-MM-DD HH24:MI:SS')` | `TO_DATE('…', 'SYYYY-MM-DD HH24:MI:SS')` |
| TIMESTAMP(n) | `TO_CHAR(c, 'SYYYY-MM-DD HH24:MI:SS.FF9')` | `TO_TIMESTAMP('…', 'SYYYY-MM-DD HH24:MI:SS.FF9')` |
| TIMESTAMP WITH TIME ZONE | `TO_CHAR(c, 'SYYYY-MM-DD HH24:MI:SS.FF9 TZR')` | `TO_TIMESTAMP_TZ('…', 'SYYYY-MM-DD HH24:MI:SS.FF9 TZR')` |
| TIMESTAMP WITH LOCAL TIME ZONE | `TO_CHAR(SYS_EXTRACT_UTC(CAST(c AS TIMESTAMP WITH TIME ZONE)), '…FF9')` | `CAST(FROM_TZ(TO_TIMESTAMP('…', '…FF9'), 'UTC') AS TIMESTAMP WITH LOCAL TIME ZONE)` |
| INTERVAL DAY TO SECOND·YEAR TO MONTH | `TO_CHAR(c)` | `TO_DSINTERVAL('…')`·`TO_YMINTERVAL('…')` |
| RAW | `RAWTOHEX(c)` | `HEXTORAW('…')` |
| ROWID (조건용) | `ROWIDTOCHAR(ROWID)` | `CHARTOROWID('…')` |
| 큰 열 | 읽지 않음(NULL 여부만 `CASE WHEN c IS NULL THEN 1 ELSE 0 END`) | 쓰지 않음 |

- 조건에서 값이 NULL 이면 `= NULL` 이 아니라 `IS NULL` 로 쓴다 **[설계]**.
- 형식 마스크가 Tibero 에서 같은지(`FF9`, `TZR`, `TM9`, `SYYYY`, `UNISTR`)는 RB-13 에서 실측한다. 다르면 Tibero 방언이 식을 바꾼다.
- 시험: 형식마다 「값 → 읽는 식 → 리터럴 → `WHERE c = 리터럴` 이 정확히 그 한 행」 왕복 시험을 Oracle 통합 시험과 Tibero 실DB 시험에 둔다(16장).

### 6.7 원복 스크립트 파일 (커밋 단위, 역순)

- 커밋 한 번이 파일 하나다. 같은 트랜잭션의 문장은 함께 커밋되므로 되돌리기 단위도 커밋 묶음이다. 문장 하나만 되돌리려면 스크립트에서 그 묶음만 골라 실행한다 **[설계]**.
- 원복 문장은 실행한 문장의 역순으로 쓴다. 그래서 지운 부모 행이 자식 행보다 먼저 되살아나고, UPDATE 뒤 DELETE 한 행은 먼저 되살아난 뒤 이전 값으로 돌아간다 **[설계]**.
- 머리 주석과 묶음 주석은 시안 화면 2 의 글을 그대로 쓴다.

```sql
-- Oranda 원복 스크립트 #318 · 접속 prod-example · 상태: 확정
-- 커밋 2026-11-12 14:05:10 (SCN 48214022) · 실행한 문장 3개 · 원복 문장 16개
-- 실행한 순서의 역순입니다. 그 뒤 다른 사람이 바꾼 행은 0행으로 끝납니다.
-- [3/3] 14:04:40 INSERT INTO SAMPLE.PRODUCTS … VALUES (2001, …) · 1행
DELETE FROM SAMPLE.PRODUCTS
 WHERE PRODUCT_ID = 2001 AND PRODUCT_NAME = '유자차 스틱' AND CATEGORY_ID = 3
   AND UNIT_PRICE = 6200 AND DISCONTINUED = 'N' AND MEMO IS NULL;
-- [2/3] 14:04:02 DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y' · 3행
INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, CATEGORY_ID, UNIT_PRICE, DISCONTINUED, MEMO)
 VALUES (1040, '보리차 티백', 3, 4730, 'Y', NULL);
-- [1/3] 14:03:21 UPDATE … SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = 3 · 12행
UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300
 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730;
```

- 「불완전」 문장은 만든 원복 문장 위에 `-- 불완전: 이유` 줄을 둔다. 무효가 된 문장(저장점으로 되돌림)은 파일에 남기되 `-- 무효: ROLLBACK TO SP1 로 되돌림` 주석만 쓰고 원복 문장을 넣지 않는다.
- 파일 글은 저장하지 않고 열 때마다 원장 문서(`RollbackScriptDocument`, 12.2)에서 만든다. 머리 주석의 상태가 늘 지금 상태와 맞게 하려는 것이다.

### 6.8 안전장치 한눈에

시안의 안전장치 일곱 가지와 이 문서가 더한 것이 어디서 정해지는지 모았다.

| 안전장치 | 출처 | 정한 곳 |
|---|---|---|
| 1 먼저 잠그고 읽기(`SELECT … FOR UPDATE`, 같은 WHERE·바인드) | 시안 | 6.2 의 1단계 |
| 2 실행 직후 값을 원복 조건에 넣기(그 뒤 바뀐 행은 0행) | 시안 | 6.2 의 3단계, 6.3 |
| 3 커밋 단위 파일, 역순, 롤백하면 무효, 저장점까지 롤백하면 그 뒤만 무효 | 시안 | 6.7, 7.3 |
| 4 암호화 보관(OS 보안 저장소의 키) | 시안 | 12.2 |
| 4 문장당 상한 100,000행 | 시안 | 6.5 |
| 4 보관 기간 30일 | 시안 | 12.3 |
| + 행 수 대조(다르면 불완전, 커밋 전에 알림) | 시안 보완 | 6.4, 10.3 |
| + PK 우선, ROWID 는 PK 없는 표·PK 를 바꾼 UPDATE 에서만 | 시안 보완 | 6.3 |
| + NULL 은 `IS NULL`, 리터럴은 기존 규칙 모양 | 시안 보완 | 6.6 |
| 내부 저장점으로 실패 시 잠금 풀기 | RD-5 | 6.2 |
| 정밀도를 잃지 않는 값 읽기·리터럴 | RD-4 | 6.6 |
| 모르는 끝은 판정하고, 못 하면 「확인 필요」 | RD-6 | 7.3 |
| 끝낼 수 있는 문장 앞에서 열린 묶음 저장 | RD-7 | 7.4 |

## 7. 층 B: 실행 지점 기록 (모든 접속)

### 7.1 무엇을 언제 읽나

- 쓰기 문장(실효 분류 DML, PL/SQL, 분류 불확실 문장)을 보내기 직전에 시점 번호 하나를 읽는다. 읽기 순서는 데이터 복사와 같다: `DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER` → `TIMESTAMP_TO_SCN(SYSTIMESTAMP)`(Tibero 는 `TIMESTAMP_TO_TSN`, 그다음 `V$DATABASE.CURRENT_TSN`) **[설계]**. 세션마다 처음 성공한 방법을 기억해 다음부터 그것만 쓴다(실패할 때마다 왕복이 늘지 않게) **[구현]**.
- 둘 다 실패하면 기록만 건너뛰고 실행은 막지 않는다 **[설계]**.
- 트랜잭션이 끝나면(COMMIT 또는 끝 판정이 「커밋」) 끝난 직후 시점을 한 번 더 읽어 커밋 시점으로 둔다.
- 묶음의 기준 시점은 그 트랜잭션의 첫 기록 문장 직전 시점이다. 화면 3 의 기본 기준이 이것이다.
- 트랜잭션 id(`DBMS_TRANSACTION.LOCAL_TRANSACTION_ID`)는 지금도 문장마다 읽으므로(트랜잭션 추적), 그 값을 묶음에 함께 남긴다. 시점 비교가 VERSIONS 질의를 내 트랜잭션으로 거를 때 쓴다(8.2).
- 읽기 전용 프로필, 에이전트 출처, `ExecutionOptions.Recording = None` 인 실행은 기록하지 않는다. 설정 「쓰기 문장을 실행할 때 직전 시점(SCN) 기록」을 끄면 층 B 를 하지 않는다. 운영 접속에서 층 B 를 끄면 원복 스크립트는 만들지만 묶음 기준 시점이 비어 시점 비교가 시각 직접 지정으로만 된다.

### 7.2 개발·테스트 접속에서 남는 것

원복 스크립트는 없고, 묶음마다 시점(기준·커밋), 트랜잭션 id, 문장 글과 바인드 값이 남는다. 문장 글과 바인드 값은 행 값처럼 민감할 수 있으므로 원복 스크립트와 같은 암호화 파일에 둔다. 시점 비교가 바뀐 행을 좁힐 때 문장의 WHERE 를 다시 쓰려면 바인드 값이 필요하기 때문이다 **[설계자 결정 RD-10]**.

### 7.3 원장: 커밋 묶음과 트랜잭션 끝 **[설계자 결정 RD-6]**

원장(`IRollbackLedger`)은 세션마다 열린 묶음 하나를 들고, 문장 기록·저장점·트랜잭션 끝을 받아 상태를 바꾼다. 커밋·롤백을 하는 곳은 앱에 여럿이다(실행 컨트롤러, SQL*Plus 처리기, PL/SQL 실행, LOB 쓰기, 데이터 가져오기, 접속 끊기, 자동 커밋, 튜닝 서비스). 이 호출처들을 하나씩 고치지 않고, 끝을 알 수 있는 세 자리에서만 알린다.

| 끝을 아는 자리 | 끝 종류 | 출처(`TransactionEndSource`) |
|---|---|---|
| `OracleSession`·`TiberoSession` 의 `CommitAsync`·`RollbackAsync` | 커밋·롤백 | `SessionApi` |
| 실행 서비스가 보낸 트랜잭션 제어 문장: `COMMIT [WORK] [COMMENT …] [WRITE …]`, `ROLLBACK [WORK]`, `ROLLBACK [WORK] TO [SAVEPOINT] 이름`, `SAVEPOINT 이름` | 커밋·롤백·저장점까지 롤백·저장점 표시 | `Statement` |
| 실행 서비스가 보낸 DDL(성공·실패 모두. Oracle 은 DDL 앞에서 암시적으로 커밋한다) | 모름 → 판정 | `Ddl` |
| 실행 서비스가 보낸 PL/SQL·불확실 문장 뒤 미커밋 표시가 참 → 거짓 | 모름 → 판정 | `PlSql` |
| `SessionBroken` | 롤백(서버가 되돌림) | `SessionBroken` |
| `SessionClosed` 때 열린 묶음이 있음 | 모름(판정 못 함) | `SessionClosed` |
| 앱 시작 때 「열림」으로 저장된 묶음 | 모름 | `AppRestart` |

- 끝 판정(`IRollbackCapture.ProbeEndAsync`): 「모름」 끝이 나면 공급자가 명령 잠금을 쥔 채 판정 질의를 보낸다. 원장은 문장마다 표본 행(최대 5행)의 판정 질의(`RollbackProbe`)를 들고 있다. 우리 트랜잭션이 이 행들을 잠그고 있었으므로 끝난 직후의 값은 실행 직후 값(커밋됨)이거나 이전 값(롤백됨) 둘 중 하나다. UPDATE 는 「키 그리고 실행 직후 값」 행이 1이면 커밋, 「키 그리고 이전 값」이 1이면 롤백이다. DELETE 는 키로 찾아 0행이면 커밋, INSERT 는 1행이면 커밋이다. 표본이 서로 다르거나 셀 수 없으면 「확인 필요」다.
- 원복 스크립트가 없는 묶음(개발·테스트 접속)의 「모름」 끝은 판정하지 않고 「커밋(추정)」으로 둔다. 시점 비교는 실제 차이만 보이므로, 롤백한 묶음이면 차이가 없어 빈 결과가 된다 **[설계자 결정 RD-16]**.
- 상태가 「확인 필요」인 원복 스크립트는 열 때 경고를 보인다: 「트랜잭션이 Oranda 가 모르는 방법으로 끝났습니다(PL/SQL 안 COMMIT·ROLLBACK, 접속 닫힘). 롤백된 트랜잭션이면 원복 실행이 지우지 않은 행을 다시 넣을 수 있습니다.」 이것이 시안의 세 상태에 더한 다섯 번째 상태다(11.2).
- 저장점: `SAVEPOINT 이름` 이 성공하면 원장이 그 자리(문장 순번)를 기억한다. `ROLLBACK TO 이름` 이 성공하면 그 뒤 문장을 무효로 둔다. 원장이 모르는 저장점(묶음이 열리기 전에 건 것)이면 묶음 전체를 무효로 둔다. 같은 이름을 다시 걸면 자리를 옮긴다(Oracle 과 같다). 내부 저장점 `ORANDA$RB` 는 원장에 알리지 않는다.
- 순서 보장: 공급자는 원장 메서드를 이벤트가 아니라 직접 호출해 기다린다(`Record`, `EndAsync`). 다음 문장의 기록이 앞 트랜잭션의 끝보다 먼저 처리되면 문장이 끝난 묶음에 붙기 때문이다. 화면 알림용 이벤트(`TransactionEnded`, `RollbackGroupSaved`)는 그 뒤에 낸다.
- 한 곳에서만 판단한다: 트랜잭션 제어 문장 해석(`COMMIT [WORK]`, `ROLLBACK [WORK] TO [SAVEPOINT] 이름`, `SAVEPOINT 이름`), 「이 문장이 트랜잭션을 끝낼 수 있는가」(COMMIT·DDL·PL/SQL·불확실 문장), 「끝 판정이 필요한가」(DDL·PL/SQL 뒤 미커밋 참 → 거짓), 문장의 기록 방식(`ChangeRecording`)은 DBMS 와 상관없으므로 Core 도우미 `RollbackFlow`(`Core/Rollback/Capture/`, RB-02)가 정한다. 공급자(Oracle·Tibero)는 문장 보내기와 통로·방언만 대고 이 판단을 따로 구현하지 않는다(13.5).

### 7.4 앱이 죽을 때 **[설계자 결정 RD-7]**

열린 묶음은 메모리에 있다. 커밋하지 않은 트랜잭션은 앱이 죽으면 서버가 되돌리므로 잃을 것이 없다. 위험한 것은 COMMIT 이 서버에서 끝난 직후, 묶음을 저장하기 전에 앱이 죽는 경우다. 그래서 공급자는 트랜잭션을 끝낼 수 있는 문장(COMMIT·DDL·PL/SQL·`CommitAsync`)을 보내기 **전에** `IRollbackLedger.BeforeEndAsync` 를 부르고, 원장은 바뀐 것이 있을 때만 열린 묶음을 「열림」 상태로 저장한다. 끝난 뒤 상태를 고친다. 다음 시작 때 「열림」으로 남은 묶음은 「확인 필요 · 앱이 끝나 결과를 모름」이 된다. 롤백(`RollbackAsync`, `ROLLBACK`) 앞에서는 저장하지 않는다.

### 7.5 앱 안 호출처별 기록 방식 **[설계자 결정 RD-21]**

`IExecutionService` 로 쓰기를 보내는 곳은 사용자 편집기 말고도 있다. 운영 접속에서 이들이 원복 스크립트를 만들면 쓸모없는 「불완전」 묶음이 생기거나 느려지므로, 호출처마다 `ExecutionOptions.Recording` 을 정한다.

| 호출처 | 출처(`HistorySource`) | 기록 방식 | 이유 | 고치는 항목 |
|---|---|---|---|---|
| SQL 편집기 실행·스크립트 실행 | Editor·Script | `Normal` | 이 기능의 대상 | — |
| 그리드 편집 반영(`RowEditor`) | GridEdit | `Normal` | 한 행 키 문장, `RowDmlBuilder` 가 모양을 준다 | RB-30 |
| LOB 쓰기(`LobWriter`) | GridEdit | `PointOnly` | LOB 값은 리터럴로 담지 못한다(19장) | RB-41 |
| 데이터 가져오기(`DataTransferService`) | Editor | `PointOnly` | INSERT … SELECT … UNION ALL 묶음이라 어차피 불완전 | RB-02 |
| PL/SQL 실행(`PlsqlRunViewModel`) | Editor | `Normal`(PL/SQL 이라 결과적으로 SCN 만) | — | — |
| 스키마 브라우저·확장 | SchemaBrowser·Extension | `Normal` | 사용자 동작 | — |
| 원복 실행·비교 반영 | Editor | `None`(스스로 반대 문장 기록, RD-8) | 11.3 | RB-43·RB-32 |
| 플래시백·도우미 질의 | Editor(`SkipHistory`) | 질의라 해당 없음 | — | — |
| 에이전트 | Agent | 정책이 쓰기를 거부 | — | — |
| 데이터 복사 | DataCopy(대량 경로) | 기록 안 함(RD-19) | — | — |

## 8. 층 C: 시점 비교 되돌리기 (화면 3)

### 8.1 쓰는 곳

원복 스크립트를 만들지 않는 개발·테스트 접속, 불완전한 스크립트, Oranda 밖에서 실행한 변경을 되돌린다. 변경 일지의 「이 실행 전과 비교…」와 그리드의 「플래시백 ▸ 이전 값으로 되돌리기…」(8.5)가 연다 **[설계]**.

### 8.2 바뀐 행 찾기 **[설계자 결정 RD-15]**

세 시점의 값을 키로 견준다: 기준(B, 실행 직전), 커밋 직후(C), 지금(N). 표마다 다음 순서로 범위를 좁힌다.

1. 내 트랜잭션이 바꾼 행: `SELECT DISTINCT <키> FROM <표> VERSIONS BETWEEN SCN :B AND :C WHERE VERSIONS_XID = HEXTORAW(:xid)`. `:xid` 는 7.1 에서 남긴 트랜잭션 id(`usn.slot.sqn`)를 RAW XID 로 바꾼 값이다. 바꾸는 규칙(바이트 순서)은 실측이 필요하다(16장). DELETE 한 행도 `VERSIONS_OPERATION = 'D'` 행으로 키가 나온다.
2. 위가 안 되면(카탈로그에 VERSIONS 문장이 없음 — 지금 Tibero, XID 변환 실패, ORA-30052): 묶음 문장의 WHERE 를 바인드 값을 넣어 다시 쓰고, `AS OF SCN :B` 와 `AS OF SCN :C` 각각에서 맞는 키를 모아 합친다. 한 행 INSERT 는 원장에 남은 넣은 값의 키를 쓴다.
3. 위도 안 되면 표 전체를 견준다. 다만 표 행 수가 100,000을 넘으면 하지 않고 「범위를 좁힐 수 없습니다. 시각과 조건을 직접 지정하세요」를 보인다.

키는 PK, 없으면 ROWID 다. ROWID 로 견주면 지웠다 되살린 행이 다른 행으로 보이므로, PK 없는 표에는 경고를 보인다.

### 8.3 분류

| 구분 | 조건 | 만드는 DML |
|---|---|---|
| 되돌림 | B ≠ C, C = N | `UPDATE … SET <바뀐 열> = <B 값> WHERE <키> AND <바뀐 열> = <N 값>` |
| 되살림 | B 에 있고 C 에 없음, N 에 없음 | `INSERT … VALUES (<B 값>)` |
| 지움 | B 에 없고 C 에 있음, N = C | `DELETE … WHERE <키> AND <모든 열> = <N 값>` |
| 충돌 | C ≠ N (커밋 뒤 다른 세션이 다시 바꿈) | 기본 제외. 「충돌 행도 되돌리기」를 켜면 위 규칙에 N 값 조건을 넣어 만든다 |
| 같음 | B = C | 없음 |

- 커밋 시점이 없으면(시각 직접 지정, 커밋 SCN 못 읽음) B 와 N 두 시점만 견주고, 「그 사이 다른 사람의 변경까지 차이로 잡힙니다」 경고를 보인다 **[설계]**.
- UPDATE 의 WHERE 에 미리 볼 때 읽은 지금 값을 넣으므로, 미리 본 뒤 반영하기 전에 바뀐 행은 0행으로 끝난다(원복 스크립트와 같은 규칙) **[설계]**.
- 값 읽기와 리터럴은 6.6 의 방언 규칙을 그대로 쓴다.
- 반영 순서는 부모 → 자식 FK 순서를 모르므로 원복 스크립트와 같이 「되살림 → 되돌림 → 지움」 순서로 쓴다 **[구현]**.

### 8.4 undo 보존 기간 안내

`dbinfo.flashback_env` 로 `undo_retention` 을 읽어 커밋 뒤 지난 시간과 견준다. 「undo_retention 900초(15분)이고 커밋 뒤 11분이 지났습니다. 시간이 더 지나면 ORA-01555 로 기준 시점을 읽지 못할 수 있습니다.」 `undo_retention` 은 보장이 아니라 목표값이므로 「읽을 수 있다」고 단정하지 않는다 **[설계]**. 기준 시점 뒤 그 표에 DDL 이 있으면(`dbinfo.flashback_ddl`) 읽기 전에 「기준 시점 뒤 테이블 정의가 바뀌어 읽을 수 없습니다(ORA-01466)」를 보인다.

### 8.5 그리드 「이전 값으로 되돌리기」를 시점 비교로 옮기기 **[설계자 결정 RD-3]**

지금 동작(TSK-13-05)은 다음과 같다. 편집 모드 결과에서만 쓰고, 반영하지 않은 편집이 있으면 막고, 고른 행 최대 200개를 「N분 전」 값과 견주어 UPDATE 를 미리 본 뒤 커밋 없이 반영하고, 끝나면 결과를 다시 조회한다. 이 동작을 깨지 않도록 세 단계로 옮긴다.

| 단계 | 바꾸는 것 | 그대로 두는 것 |
|---|---|---|
| G1 (RB-32 와 함께) | 화면 3 을 변경 일지에서만 연다. 그리드 명령은 손대지 않는다. `FlashbackPlanner.PlanRestore` 를 `PointComparePlanner` 위의 얇은 어댑터로 바꾸고, 기존 단위 시험이 그대로 통과해야 한다 | 그리드 명령·문구·대화상자 |
| G2 (RB-33) | 그리드 명령 `oranda.grid.flashback.restore` 가 분 입력 대신 화면 3 을 연다. 범위는 고른 행의 키(최대 200), 시점 목록 맨 위는 「10분 전」(지금 기본값), 그 아래 그 표를 건드린 변경 일지 커밋 묶음(최신순), 맨 아래 「시각 직접 지정」. 범위가 고른 행이므로 분류는 「되돌림」만 나온다(그때 없던 행은 지금처럼 건너뜀으로 센다). 커밋 묶음을 고르면 「이 커밋 묶음 전체 보기」 단추로 범위를 묶음 전체로 넓혀 지운 행까지 되살릴 수 있다 | 명령 id, 메뉴 자리, 편집 모드·미반영 편집 검사, 200행 상한, 커밋 없이 반영, 반영 뒤 다시 조회, 결과 안내 문구(「n 행을 10분 전 값으로 되돌렸습니다(커밋 안 함)…」) |
| G3 (G2 를 한 판 쓴 뒤) | 분 입력 대화상자(`AskMinutesAsync` 의 되돌리기 쓰임)와 옛 경로 코드를 지운다. 「N분 전 값 보기」·「이 행 변경 이력」은 그대로 둔다 | — |

G2 에서 운영 접속이면 그리드 반영도 DML 이므로 원복 스크립트가 함께 만들어진다(6장 경로 그대로).

## 9. 데이터가 사라지는 DDL 앞 백업 테이블 권유

- 대상: `TRUNCATE TABLE`, `ALTER TABLE … DROP COLUMN`(`DROP (…)` 포함), `DROP TABLE … PURGE`. 이 DDL 뒤에는 AS OF 로도 이전 데이터를 읽지 못한다. 원복 스크립트로 담기에는 양이 많을 수 있어 서버 백업 테이블(CTAS)을 권한다 **[설계]**.
- 확인 창이 이미 나오는 곳에서만 권한다. 개발 접속은 D-065 로 확인을 묻지 않으므로 권하지 않는다 **[설계자 결정 RD-17]**.
- 확인 창에 「먼저 백업 테이블 만들기」 체크와 이름 칸(고칠 수 있음)을 둔다. 기본 이름은 `ORB_<표 이름>_<MMddHHmm>`, 서버 이름 한도(11.2·12.1 은 30바이트, 그 위는 128바이트)에 맞춰 표 이름을 자른다. 기본 위치는 세션 사용자 스키마다(표 소유자 스키마에 CREATE 권한이 없을 수 있다).
- 문장: TRUNCATE·DROP … PURGE 는 `CREATE TABLE <이름> AS SELECT * FROM <표>`, DROP COLUMN 은 `CREATE TABLE <이름> AS SELECT <PK 열>, <지울 열> FROM <표>`(PK 가 없으면 `ROWIDTOCHAR(ROWID) AS ORB_ROWID` 를 더한다).
- CTAS 는 DDL 이라 열린 트랜잭션을 커밋한다. 뒤이은 TRUNCATE·DROP 도 어차피 커밋하므로 순서만 앞당겨진다. 확인 창이 이 사실을 한 줄로 알린다.
- CTAS 가 실패하면(권한·할당량) 원래 DDL 을 보내지 않고 「백업 테이블을 만들지 못했습니다: 이유. 백업 없이 실행하려면 체크를 끄고 다시 실행하세요」로 끝낸다.
- 변경 일지는 TRUNCATE 도 보관 대상에 넣는다(`ChangeOperation.Truncate`, 표 DDL 보관, 요약에 백업 테이블 이름). 마이그레이션은 필요 없다(`cause` 는 `Ddl` 그대로).

## 10. 확인 창·실행 로그·커밋 전 경고

### 10.1 운영 확인 창의 원복 줄 (화면 4)

운영 접속에서 이미 나오는 확인 창(SAFE-03)에 줄을 더한다.

- `☑ 원복 스크립트 만들기 (예상 1,240행)` — 기본값은 설정 「운영 접속에서 원복 스크립트 만들기」. 끌 수 있다. 상한을 넘으면 체크가 꺼지고 흐려지며 6.5 의 문구가 보인다. 예상 행 수를 모르면 「(예상 행 수 알 수 없음)」.
- 그 아래 회색 글: 「같은 조건으로 먼저 잠그고 읽으므로 실행 시간이 늘어납니다. 이 PC 에 암호화해 30일 보관합니다.」(보관 일수는 설정 값)
- 경고 줄(있을 때만): 「대상 테이블에 트리거 2개가 있습니다. 트리거가 바꾼 것은 원복 스크립트에 들어가지 않습니다」, 「ON DELETE CASCADE 로 자식 테이블 SAMPLE.ORDER_ITEMS 가 함께 지워집니다. 원복 스크립트에 들어가지 않습니다」, 「큰 열(CLOB)이 있어 일부 행은 원복 문장을 만들 수 없습니다」.
- 맨 아래: 「실행 직전 SCN 은 원복 스크립트와 상관없이 기록합니다.」(층 B 가 켜져 있을 때)
- 원복 대상이 아닌 문장(MERGE 등)이면 원복 줄 대신 「이 문장은 원복 스크립트를 만들지 않습니다(MERGE). 실행 직전 SCN 만 기록합니다.」

확인을 묻지 않는 실행(`PreConfirmed`, 개발 접속 확인 생략, Allow)은 설정 기본값대로 한다(`ConfirmAnswer.Default`).

### 10.2 실행 로그

문장마다 한 줄: 「원복 스크립트 12문장 (커밋하면 확정)」, 「원복 스크립트 불완전: 행 수 다름(잠금 12, 실행 13) — 커밋 전에 확인하세요」(경고색), 「원복 스크립트를 만들지 않았습니다: 상한 넘음(123,456행)」, 「원복 스크립트를 만들려고 문장 끝에 RETURNING ROWID INTO 를 붙여 실행했습니다」, 원복 스크립트 없이 SCN 만 기록한 문장은 줄을 남기지 않는다(개발 접속 로그가 늘지 않게). 문구 세부는 **[구현]**.

### 10.3 커밋 전 경고

설정 「잠근 행 수와 실행한 행 수가 다르면 커밋 전에 알리기」가 켜져 있고 열린 묶음에 행 수가 다른 문장이 있으면, 사용자가 커밋할 때(툴바·메뉴·세션 창 커밋, 편집기에서 실행하는 `COMMIT` 문장) 「이 트랜잭션의 원복 스크립트는 불완전합니다: 행 수 다름 1문장. 커밋할까요?」를 묻는다. 묻는 곳은 앱의 커밋 경로(`ExecutionController.ConfirmedCommitAsync` 와 편집기 실행 앞의 `COMMIT` 검사)이고, 원장의 `Open(sessionId).HasMismatch` 를 읽는다. 자동 커밋은 묻지 않는다.

### 10.4 설정 「되돌리기」 탭 (화면 5)

- 자리: 설정 창 탭 줄의 「실행」과 「기능」 사이(일반 · 편집기 · 결과 그리드 · 실행 · **되돌리기** · 기능 …) **[설계]**. `AppSettings.Rollback`(JSON 섹션 `rollback`)을 읽고 쓰며, 저장하면 `SettingsChanged("rollback")` 을 낸다. 새 섹션이라 설정 스키마 버전은 올리지 않는다(키가 없는 옛 파일 = 모두 기본값).
- 항목:

| 항목 | 설정 키 | 기본 | 범위 | 끄거나 바꾸면 |
|---|---|---|---|---|
| 쓰기 문장을 실행할 때 직전 시점(SCN) 기록 | `recordPoint` | 켬 | — | 층 B 를 하지 않는다(7.1) |
| 운영 접속에서 원복 스크립트 만들기 | `scriptOnProduction` | 켬 | — | 확인 창 체크의 기본값이 꺼짐이 되고, 묻지 않는 실행은 만들지 않는다 |
| 문장당 상한 n 행, 넘으면 묻기 | `maxRowsPerStatement` | 100000 | 1~1,000,000 | 6.5 |
| 보관 기간 n 일 | `retentionDays` | 30 | 1~365 | 다음 정리부터 적용(12.3). 줄이면 「지난 묶음 n개가 다음 시작 때 지워집니다」 안내 |
| 잠근 행 수와 실행한 행 수가 다르면 커밋 전에 알리기 | `warnRowCountMismatch` | 켬 | — | 10.3 을 묻지 않는다(실행 로그 경고는 남음) |
| TRUNCATE·DROP COLUMN·DROP … PURGE 앞에서 백업 테이블 만들기 권하기 | `suggestBackupBeforeDataLossDdl` | 켬 | — | 9장의 체크를 보이지 않는다 |

- 「운영 접속에서 원복 스크립트 만들기」 아래 설명 줄: 「원복 스크립트는 운영 접속에서만 만듭니다. 운영 접속에서도 끌 수만 있고, 개발·테스트 접속에는 켜는 항목이 없습니다. 개발·테스트 접속은 SCN 만 기록하고, 되돌릴 때는 변경 일지의 「이 실행 전과 비교」를 씁니다.」 **[사용자]**
- 이 PC 에 OS 보안 저장소가 없으면(`IRollbackStore.CanStoreScripts = false`) 둘째 항목을 흐리게 하고 「이 PC 에는 안전한 키 저장소가 없어 원복 스크립트를 만들 수 없습니다」를 보인다.
- 탭 아래쪽에 지금 보관 중인 묶음 수와 크기(「묶음 42개 · 3.1 MB」)와 「모두 지우기…」 단추를 둔다(확인 뒤 행과 파일을 모두 지운다) **[구현]**.

## 11. 변경 일지와 원복 스크립트 화면

### 11.1 변경 일지 (화면 1)

지금의 도구 > 변경 일지 창을 넓혀 DDL 보관본 옆에 DML 실행 기록(커밋 묶음)을 함께 보인다 **[설계]**.

- 열: 시간 | 객체 | 원인 | 요약 | 영향 행 | 시점 | 원복 스크립트.
- 원인: 「커밋」·「롤백」·「모름」(묶음), 「DML 실행」(묶음 안 문장), 「DDL 실행」·「컴파일」(지금 행).
- 묶음 행은 접고 펼친다. 요약은 「문장 3개 · prod-example」, 시점은 「SCN 48213907 → 48214022」(기준 → 커밋), 롤백 묶음은 기준만. 객체는 첫 표, 둘 이상이면 「SAMPLE.PRODUCTS 외 2」.
- 펼친 문장 행: 시각, 표, 「DML 실행」, 문장 요약(가린 글, 한 줄), 영향 행, 직전 시점, 원복 문장 수(「12문장」, 「만들지 않음」, 「무효」).
- 도구 모음: 지금 단추에 「원복 스크립트 열기」(묶음을 고르고 스크립트가 있을 때), 「이 실행 전과 비교…」(묶음 또는 문장을 고를 때)를 더한다. 「원인」 거르기에 「DML」을 더한다.
- 상태 줄: 「6건 · 원복 스크립트는 이 PC 에 암호화해 보관하며 30일 뒤 지웁니다.」
- 목록은 두 저장소(`IChangeJournalStore`, `IRollbackStore`)를 각각 시간 역순으로 읽어 합친다. 한 화면 300행 한도는 두 목록을 합친 뒤 자른다 **[구현]**.

### 11.2 상태 표시

| 표시 | 뜻 | 상태 값 |
|---|---|---|
| 확정 · 16문장 | 커밋했고 모든 문장의 원복 문장을 만들었다 | `Confirmed` |
| 불완전 · 이유 | 커밋했지만 일부 행·문장의 원복 문장을 만들지 못했다. 이유를 줄에 적는다 | `Incomplete` |
| 무효 · 롤백함 | 롤백했다. 원복 스크립트를 열 수 없다(묶음 정보만 남는다) | `Invalid` |
| 만들지 않음 · 이유 | 개발·테스트 접속, 설정 끔, 확인 창에서 끔, 상한 넘음, 대상 아님, 안전한 저장소 없음 | `NotCreated` |
| 확인 필요 · 이유 | 트랜잭션이 어떻게 끝났는지 모른다(7.3·7.4). 스크립트는 열 수 있고 경고가 보인다 **[설계자 결정 RD-6]** | `Unverified` |

묶음의 상태는 문장 상태를 모아 정한다: 하나라도 무효가 아닌 문장의 원복이 있고 모두 완전하면 확정, 하나라도 불완전이면 불완전, 모든 문장이 무효면 무효, 원복 문장을 만든 문장이 없으면 만들지 않음.

### 11.3 원복 스크립트 탭 (화면 2)

- 「원복 스크립트 열기」는 새 SQL 탭을 묶음의 접속으로 열고(지금 「이 DDL 을 새 편집기에 열기」와 같은 접속 찾기), 6.7 의 글을 넣는다. 탭은 읽기 전용이다. 고치려면 「일반 SQL 탭으로 복사」를 쓴다 **[설계]**.
- 자동으로 실행하지 않는다.
- 도구 모음: 「원복 실행 (커밋 안 함)」, 「일반 SQL 탭으로 복사」, 「파일로 내보내기…」. 내보내기는 평문 `.sql` 이므로 「이 파일에는 실제 데이터가 평문으로 들어갑니다」를 묻는다.
- 상태 줄: 「원복 문장 16개 · 암호화 보관 · 2026-12-12 에 지움 · 읽기 전용」.
- 「원복 실행」: 접속이 운영이면 확인 창 하나(「prod-example(운영)에서 원복 문장 16개를 실행합니다. 커밋하지 않습니다」)를 띄운 뒤, 모든 문장을 `PreConfirmed` 로 넘겨 문장마다 다시 묻지 않는다. 문장마다 영향 행 수가 1인지 확인한다. 0행은 「그 뒤 다른 사람이 바꾼 행」, ORA-00001(`ConstraintViolation`)은 「충돌: 같은 키가 이미 있음」으로 표시하고 나머지는 계속한다. 끝나면 요약(되돌림 n, 0행 n, 충돌 n, 오류 n)을 보이고, 커밋이나 롤백은 사용자가 한다 **[설계]**.
- 되돌리기의 되돌리기 **[설계자 결정 RD-8]**: 원복 실행은 `ExecutionOptions.Recording = None` 으로 보내 문장마다 잠금 읽기를 하지 않는다. 대신 실행기가 1행을 바꾼 원복 문장만 골라 그 반대 문장(원복 문장이 쓴 이전 값과 실행 직후 값을 맞바꾼 것)으로 새 묶음을 원장에 기록한다(`RollbackOrigin.RollbackRun`, 원래 묶음 번호 함께). 원복 문장 자체가 키와 값 조건으로 한 행만 바꾸므로 잠금 읽기 없이도 반대 문장이 정확하다. 화면 3 의 「반영」도 같은 방식이다(`RollbackOrigin.CompareApply`).

## 12. 저장 형식

### 12.1 `oranda.db` 표 (마이그레이션 `0005_rollback.sql`)

`change_journal` 을 넓히지 않고 새 표 둘을 쓴다 **[설계자 결정 RD-10]**. `change_journal.cause` 의 CHECK 제약은 SQLite 에서 표를 다시 만들지 않고는 바꿀 수 없고, 보존 기간(365일과 30일)도 다르다. 마이그레이션 번호 0005 는 이 기능이 쓴다(`0004_agent.sql` 다음).

```sql
CREATE TABLE IF NOT EXISTS rollback_group (
  id                 INTEGER PRIMARY KEY,
  started_at         TEXT NOT NULL,
  ended_at           TEXT,
  profile_id         TEXT NOT NULL,
  profile_name       TEXT NOT NULL,
  db_key             TEXT NOT NULL,
  server_name        TEXT,
  environment        TEXT NOT NULL CHECK (environment IN ('Development','Test','Production')),
  dbms               TEXT NOT NULL,
  point_name         TEXT,                 -- SCN | TSN
  base_point         INTEGER,
  commit_point       INTEGER,
  transaction_id     TEXT,
  end_kind           TEXT,                 -- Commit | Rollback | Unknown | NULL(열림)
  end_source         TEXT,
  state              TEXT NOT NULL,        -- RollbackScriptState
  reason             TEXT,                 -- NotCreatedReason 또는 불완전·확인 필요 이유(가린 글)
  statement_count    INTEGER NOT NULL,
  step_count         INTEGER NOT NULL,
  rows_total         INTEGER NOT NULL,
  tables             TEXT NOT NULL,        -- 'OWNER.NAME' 을 줄바꿈으로 이은 것
  origin             TEXT NOT NULL DEFAULT 'Execution',
  origin_group_id    INTEGER,
  script_file        TEXT,                 -- {data}/rollback 아래 상대 경로, 없으면 NULL
  expires_at         TEXT
);
CREATE TABLE IF NOT EXISTS rollback_statement (
  group_id           INTEGER NOT NULL REFERENCES rollback_group(id) ON DELETE CASCADE,
  seq                INTEGER NOT NULL,
  executed_at        TEXT NOT NULL,
  source             TEXT NOT NULL,        -- HistorySource
  verb               TEXT NOT NULL,        -- DmlVerb
  owner              TEXT,
  name               TEXT,
  summary            TEXT,                 -- 가린 문장 요약 200자
  rows_affected      INTEGER,
  locked_rows        INTEGER,
  before_point       INTEGER,
  state              TEXT NOT NULL,
  reason             TEXT,
  step_count         INTEGER NOT NULL,
  invalidated        INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (group_id, seq)
);
CREATE INDEX IF NOT EXISTS ix_rollback_group_time  ON rollback_group(started_at DESC);
CREATE INDEX IF NOT EXISTS ix_rollback_group_db    ON rollback_group(db_key, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_rollback_stmt_object ON rollback_statement(owner, name);
```

- 요약과 이유는 저장소가 `ISecretRedactor` 로 가린 뒤 쓴다(변경 일지와 같다). 행 값은 이 표에 들어가지 않는다.
- `docs/design/data-model.md` 에 3.2.2 절(이 표), 1.2 파일 목록 줄, 3.4 보존 정책 줄, 5.2 비밀 없음 확인 표 줄을 더한다(RB-00).

### 12.2 암호화 파일

- 경로: `{data}/rollback/{yyyy-MM}/{id:D8}.orb`. 묶음마다 하나. 개발·테스트 묶음도 문장 글과 바인드가 있으면 만든다(7.2).
- 내용: `ORB1`(4바이트) · 판 번호(1바이트, 1) · nonce(12바이트) · tag(16바이트) · 암호문. 평문은 UTF-8 JSON `RollbackScriptDocument`(13장 계약). AES-256-GCM 이고, 덧붙인 데이터(AAD)는 `orb1:{id}:{db_key}` 다. 파일을 다른 묶음 자리로 옮기면 풀리지 않는다 **[설계자 결정 RD-9]**.
- 키: 처음 쓸 때 무작위 32바이트를 만들어 `ICredentialStore` 에 `oranda:rollback:key:v1` 로 둔다. Windows 는 DPAPI 파일, macOS 는 키체인이다. 접속 비밀번호와 같은 OS 보안 저장소다 **[설계]**. 키를 지우거나 잃으면 기존 파일은 열 수 없다(일지에 「열 수 없음 · 키가 없습니다」).
- OS 보안 저장소가 없는 플랫폼(`VolatileCredentialStore`, 정식 지원 밖)에서는 재시작하면 키가 사라지므로 원복 스크립트를 만들지 않는다(「만들지 않음 · 안전한 저장소 없음」). `IRollbackVault.IsDurable` 이 이것을 알린다.
- 저장은 임시 파일에 쓴 뒤 이름을 바꾼다(`DpapiCredentialStore` 와 같은 방식).

### 12.3 보관

- 보관 기간 기본 30일(설정, 1~365). 묶음이 끝난 시각(`ended_at`, 열림이면 `started_at`)에서 센다. 앱 시작 직후 한 번, 그 뒤 묶음 100개 저장마다 지난 묶음의 행과 파일을 지운다.
- 시작 직후 정리 작업은 「열림」 묶음을 「확인 필요」로 바꾸고(7.4), 표에 없는 `.orb` 파일을 지운다.
- 용량 상한은 두지 않는다. 성능 시험(RB-90)의 파일 크기를 보고 다시 정한다.

## 13. 모듈 경계와 공개 계약

### 13.1 배치

| 위치 | 내용 | 담당 항목 |
|---|---|---|
| `src/Oranda.Core/Rollback/` | 공유 계약(레코드·열거·인터페이스·이벤트). RB-00 이 만들고 그 뒤에는 조정자 승인으로만 바꾼다 | RB-00 |
| `src/Oranda.Core/Rollback/Literals/` | Oracle 계열 공통 리터럴·읽는 식 도우미(방언이 씀) | RB-01 |
| `src/Oranda.Core/Rollback/Capture/` | `RollbackCaptureEngine`(`IRollbackCapture` 구현), 잠금 읽기·직후 값 읽기·원복 문장 만들기, 공급자 공통 판단 `RollbackFlow`(7.3) | RB-02 |
| `src/Oranda.Core/Rollback/Script/` | 원복 스크립트 글 만들기(6.7), 반대 문장 만들기(RD-8) | RB-03 |
| `src/Oranda.Core/Rollback/Ledger/` | `RollbackLedger`(`IRollbackLedger` 구현) | RB-04 |
| `src/Oranda.Core/Rollback/Compare/` | `PointComparePlanner`, 비교 질의 글 | RB-31 |
| `src/Oranda.Core/Rollback/Backup/` | 백업 테이블 제안 `BackupPlanner`·CTAS 글(9장) | RB-44 |
| `src/Oranda.Language/Editing/DmlShapeAnalyzer.cs` | `IDmlAnalyzer` 구현(토큰 기반). Language 어셈블리는 `[OrandaService]` 스캔 대상이므로 여기서 스스로 등록한다(`RollbackComposition.cs` 를 고치지 않는다) | RB-30 |
| `src/Oranda.Oracle/Rollback/` | Oracle `IRollbackChannel`·`IRollbackDialect` 구현, 카탈로그 `rollback/*.sql` | RB-10 |
| `src/Oranda.Tibero/Rollback/` | Tibero 같은 것, 오류 분류 보강(`TiberoErrorClassifier`), 백업 권유·TRUNCATE 일지 연결 | RB-11·RB-13·RB-14 |
| `src/Oranda.Platform/Rollback/` | `RollbackVault`, `SqliteRollbackStore`, 시작 정리 작업 | RB-20~22 |
| `src/Oranda.App/Shell/Composition/RollbackComposition.cs` | 앱 조립: 엔진·원장·분석기 등록(Core 는 스캔 대상이 아니다) | RB-00(빈 구현) → RB-02·RB-04 가 실구현으로 바꿈 |
| `src/Oranda.App/Features/Rollback/RollbackCommandIds.cs` | 명령 id 와 인자 레코드(레인 사이 화면 연결) | RB-00, 그 뒤 동결 |
| `src/Oranda.App/Features/Rollback/` (위 파일 빼고) | 원복 스크립트 탭·원복 실행 | RB-43 |
| `src/Oranda.App/Features/PointCompare/` | 화면 3 | RB-32 |

### 13.2 Core 계약 (RB-00)

이름 공간은 `Oranda.Core.Rollback` 이다. 모든 레코드는 불변이고, 목록 속성의 기본값은 빈 목록이다.

```csharp
namespace Oranda.Core.Rollback;

// ---------- 문장 모양 (RollbackShape.cs) ----------
public enum DmlVerb { Update, Delete, InsertValues, InsertSelect, Merge, PlSql, Other }

/// <summary>치환 변수를 처리한 뒤 글로 만든 쓰기 문장의 모양. 이름의 따옴표 규칙은 ChangeTarget 과 같다.</summary>
public sealed record DmlShape(DmlVerb Verb)
{
    public string? Owner { get; init; }            // 문장에 스키마가 없으면 null
    public string? Name { get; init; }
    public string? DbLink { get; init; }
    public string? TableText { get; init; }        // 문장에 적힌 표 참조 그대로(파티션 절·별칭 포함)
    public string? Alias { get; init; }
    public string? WhereText { get; init; }        // WHERE 뒤 글(바인드 포함). WHERE 가 없으면 null
    public IReadOnlyList<string> SetColumns { get; init; } = [];
    public IReadOnlyList<string> InsertColumns { get; init; } = [];
    public string? NotTarget { get; init; }        // 원복 대상이 아닌 이유 한 줄. null = 대상
}

public interface IDmlAnalyzer
{
    /// <summary>쓰기 문장이 아니면 null. 예외를 던지지 않는다.</summary>
    DmlShape? Analyze(string text);
}

// ---------- 시점·상태 (RollbackTypes.cs) ----------
public sealed record ChangePoint(long Number, string Name, DateTimeOffset ReadAt, string Method)
{
    public string Text => Name + " " + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public enum RollbackScriptState { NotCreated, Open, Confirmed, Incomplete, Invalid, Unverified }
public enum NotCreatedReason { None, NotProduction, SettingOff, Declined, OverLimit, NotTarget, NoSecureStore, Failed, Suppressed }
public enum ChangeRecording { Normal, PointOnly, None }
public enum TransactionEndKind { Commit, Rollback, RollbackToSavepoint, Unknown }
public enum TransactionEndSource { SessionApi, Statement, Ddl, PlSql, SessionBroken, SessionClosed, AppRestart }
public enum RollbackOrigin { Execution, RollbackRun, CompareApply }

// ---------- 표 정보 (RollbackTable.cs) ----------
public enum RollbackValueKind
{
    Text, NationalText, Number, BinaryFloat, BinaryDouble, Date, Timestamp, TimestampTz, TimestampLtz,
    IntervalDaySecond, IntervalYearMonth, Raw, RowId, Large,
}

public sealed record RollbackColumn(string Name, string DataType, RollbackValueKind Kind)
{
    public int? Scale { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsIdentity { get; init; }
    public bool IsInvisible { get; init; }
}

/// <summary>시노님을 푼 실제 표. KeyColumns 가 비면 ROWID 로 행을 찾는다.</summary>
public sealed record RollbackTable(Oranda.Core.Metadata.DbObjectRef Table, IReadOnlyList<RollbackColumn> Columns, IReadOnlyList<string> KeyColumns)
{
    public IReadOnlyList<string> SideEffects { get; init; } = [];   // 트리거·CASCADE FK 안내 글
}

// ---------- 확인 창 (RollbackConfirm.cs) ----------
public sealed record RollbackOffer(bool Applicable, NotCreatedReason Reason)
{
    public static RollbackOffer NotApplicable(NotCreatedReason reason, string? message = null) => new(false, reason) { Message = message };
    public DmlShape? Shape { get; init; }
    public RollbackTable? Table { get; init; }
    public long? EstimatedRows { get; init; }      // null = 모름
    public bool OverLimit { get; init; }
    public bool DefaultOn { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Message { get; init; }
}

public sealed record BackupOffer(Oranda.Core.Metadata.DbObjectRef Table, string BackupName, string CreateSql, string Reason);

public sealed record ConfirmExtras(RollbackOffer? Rollback, BackupOffer? Backup)
{
    public static ConfirmExtras None { get; } = new(null, null);
}

public sealed record ConfirmAnswer(bool Proceed, bool CreateScript, string? BackupSql)
{
    public static ConfirmAnswer Declined { get; } = new(false, false, null);
    /// <summary>묻지 않고 진행할 때: 원복은 제안의 기본값, 백업은 만들지 않는다.</summary>
    public static ConfirmAnswer Default(ConfirmExtras? extras) => new(true, extras?.Rollback is { Applicable: true, OverLimit: false, DefaultOn: true }, null);
}

// ---------- 공급자가 구현 (IRollbackChannel.cs, IRollbackDialect.cs) ----------
public sealed record RollbackRows(IReadOnlyList<object?[]> Rows, bool Truncated);

/// <summary>공급자 연결 위의 통로. Begin·Complete·Probe 단계에서는 호출한 쪽이 명령 잠금을 쥐고 있다.
/// Prepare 단계에 넘기는 통로는 메서드마다 스스로 잠금을 잡고 놓는다. 리더는 끝까지 읽고 닫은 뒤 돌려준다.</summary>
public interface IRollbackChannel
{
    Task<RollbackRows> QueryAsync(string sql, IReadOnlyDictionary<string, Oranda.Core.Execution.BindValue> binds, int maxRows, CancellationToken ct);
    Task ExecuteAsync(string sql, CancellationToken ct);   // SAVEPOINT·ROLLBACK TO 내부 문장만
}

public interface IRollbackDialect
{
    Oranda.Core.Connection.Dbms Dbms { get; }
    string PointName { get; }                                // "SCN" | "TSN"
    IReadOnlyList<string> PointQueries { get; }             // 차례로 시도, 행 하나·열 하나
    string SavepointName { get; }                           // "ORANDA$RB"
    bool SupportsReturningRowId { get; }
    string AsOf(long point);                                // "AS OF SCN 48213907"
    RollbackValueKind KindOf(Oranda.Core.Metadata.ColumnInfo column);
    string ReadExpression(RollbackColumn column, string qualifiedColumn);   // 6.6 표
    /// <summary>값 → 리터럴. NULL 은 "NULL". 리터럴로 쓸 수 없으면 null(불완전 이유는 reason).</summary>
    string? Literal(RollbackColumn column, object? value, out string? reason);
    string RowIdLiteral(string rowId);                      // CHARTOROWID('…')
    string? SideEffectsSql { get; }                         // 바인드 :owner, :name → (KIND, NAME) 행. null = 찾지 않음
    int MaxIdentifierBytes(Oranda.Core.Connection.ServerInfo server);
}

// ---------- 엔진 (IRollbackCapture.cs) — 구현: Capture/RollbackCaptureEngine ----------
public sealed record RollbackRequest(Oranda.Core.Sessions.ISession Session, Oranda.Core.Execution.SqlStatement Statement,
                                     IReadOnlyDictionary<string, Oranda.Core.Execution.BindValue> Binds, Oranda.Core.Execution.ExecutionOptions Options)
{
    public DmlShape? Shape { get; init; }   // 공급자가 IDmlAnalyzer 로 채운다(RowDmlBuilder 가 준 Statement.Dml 이 있으면 그것)
}

public interface IRollbackCapture
{
    /// <summary>정책 관문 앞, 명령 잠금 밖. 실패해도 예외 없이 NotApplicable(Failed). 사용자 취소만 전파.</summary>
    Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct);

    /// <summary>명령 잠금 안, 사용자 문장 직전. 층 B 시점 읽기와 0·1단계.</summary>
    Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer,
                                       IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct);

    /// <summary>「모름」 끝 판정(7.3). 명령 잠금 안. 표본이 없으면 Unknown.</summary>
    Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct);

    /// <summary>끝난 직후 시점 읽기(커밋 시점). 실패하면 null.</summary>
    Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct);
}

public interface IStatementCapture
{
    Oranda.Core.Execution.SqlStatement StatementToSend { get; }
    IReadOnlyDictionary<string, Oranda.Core.Execution.BindValue> BindsToSend { get; }

    /// <summary>사용자 문장 성공 뒤(2~3단계와 원장 기록). 내부 실패는 Incomplete 로 담고 던지지 않는다.</summary>
    Task<RollbackStatementSummary> CompleteAsync(long? rowsAffected, IReadOnlyDictionary<string, object?> outValues,
                                                 string? transactionId, CancellationToken ct);

    /// <summary>사용자 문장 실패·취소 뒤. 내부 저장점으로 되돌려 잠금을 푼다. ct 는 None 으로 부른다.</summary>
    Task<RollbackStatementSummary> AbandonAsync(Oranda.Core.Execution.DbError? error, CancellationToken ct);
}

public sealed record RollbackStatementSummary(RollbackScriptState State, NotCreatedReason Reason, int StepCount)
{
    public static RollbackStatementSummary None { get; } = new(RollbackScriptState.NotCreated, NotCreatedReason.None, 0);
    public ChangePoint? BeforePoint { get; init; }
    public string? LogLine { get; init; }
    public string? Warning { get; init; }
    public bool ReturningAppended { get; init; }
}

// ---------- 원장 기록 (RollbackRecords.cs) ----------
public sealed record RollbackStep(string Sql)
{
    public string? Note { get; init; }
    public bool ExpectOneRow { get; init; } = true;
}

/// <summary>끝 판정 질의 두 개(SELECT COUNT(*) …). 앞이 1 이면 커밋, 뒤가 1 이면 롤백.</summary>
public sealed record RollbackProbe(string CommittedIfOne, string RolledBackIfOne);

public sealed record RollbackBind(string Name, Oranda.Core.Execution.BindDataType Type, string? Value);

public sealed record RollbackStatementRecord
{
    public required Guid SessionId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string ProfileName { get; init; }
    public required string DbKey { get; init; }
    public string? ServerName { get; init; }
    public Oranda.Core.Connection.ConnectionEnvironment Environment { get; init; }
    public Oranda.Core.Connection.Dbms Dbms { get; init; }
    public Oranda.Core.History.HistorySource Source { get; init; }
    public DateTimeOffset ExecutedAt { get; init; }
    public required string Text { get; init; }                 // 사용자가 쓴 글(치환 뒤). 파일에만 저장
    public IReadOnlyList<RollbackBind> Binds { get; init; } = [];
    public DmlVerb Verb { get; init; }
    public Oranda.Core.Metadata.DbObjectRef? Table { get; init; }
    public long? RowsAffected { get; init; }
    public long? LockedRows { get; init; }
    public ChangePoint? BeforePoint { get; init; }
    public string? TransactionId { get; init; }
    public RollbackScriptState State { get; init; }             // NotCreated | Open(완전) | Incomplete
    public NotCreatedReason Reason { get; init; }
    public string? IncompleteReason { get; init; }
    public IReadOnlyList<RollbackStep> Steps { get; init; } = [];   // 이 문장 안 순서. 파일은 문장 역순으로 쓴다
    public IReadOnlyList<RollbackProbe> Probes { get; init; } = [];
    public IReadOnlyList<string> TouchedRowIds { get; init; } = [];  // PK 없는 표의 겹침 검사용(RD-13). 파일에 저장하지 않음
    public bool ReturningAppended { get; init; }
}

public sealed record RollbackStatementInfo(int Seq, DateTimeOffset ExecutedAt, DmlVerb Verb, Oranda.Core.Metadata.DbObjectRef? Table,
                                           string Summary, long? RowsAffected, ChangePoint? BeforePoint,
                                           RollbackScriptState State, string? Reason, int StepCount, bool Invalidated);

public sealed record RollbackGroup
{
    public long Id { get; init; }
    public required Guid ProfileId { get; init; }
    public required string ProfileName { get; init; }
    public required string DbKey { get; init; }
    public string? ServerName { get; init; }
    public Oranda.Core.Connection.ConnectionEnvironment Environment { get; init; }
    public Oranda.Core.Connection.Dbms Dbms { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public ChangePoint? BasePoint { get; init; }
    public ChangePoint? CommitPoint { get; init; }
    public string? TransactionId { get; init; }
    public TransactionEndKind? EndKind { get; init; }
    public TransactionEndSource? EndSource { get; init; }
    public RollbackScriptState State { get; init; }
    public NotCreatedReason Reason { get; init; }
    public string? StateReason { get; init; }
    public int StepCount { get; init; }
    public long RowsTotal { get; init; }
    public IReadOnlyList<Oranda.Core.Metadata.DbObjectRef> Tables { get; init; } = [];
    public RollbackOrigin Origin { get; init; }
    public long? OriginGroupId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool HasScriptFile { get; init; }
    public IReadOnlyList<RollbackStatementInfo> Statements { get; init; } = [];
}

/// <summary>암호화 파일의 평문(JSON, camelCase). SchemaVersion = 1.</summary>
public sealed record RollbackScriptDocument(int SchemaVersion, long GroupId, IReadOnlyList<RollbackScriptStatement> Statements);
public sealed record RollbackScriptStatement(int Seq, DateTimeOffset ExecutedAt, string Text, IReadOnlyList<RollbackBind> Binds,
                                             IReadOnlyList<RollbackStep> Steps, IReadOnlyList<RollbackProbe> Probes,
                                             bool Invalidated, string? IncompleteReason, long? RowsAffected);

public sealed record RollbackQuery
{
    public string? Text { get; init; }
    public string? DbKey { get; init; }
    public Oranda.Core.Metadata.DbObjectRef? Table { get; init; }
    public int Limit { get; init; } = 300;
}

// ---------- 원장 (IRollbackLedger.cs) — 구현: Ledger/RollbackLedger ----------
public sealed record RollbackOpenState(int Statements, int Steps, bool HasMismatch, bool HasIncomplete);

public interface IRollbackLedger
{
    void Record(RollbackStatementRecord record);
    void MarkSavepoint(Guid sessionId, string name);
    RollbackOpenState? Open(Guid sessionId);
    IReadOnlyList<RollbackProbe> Probes(Guid sessionId, int max = 5);
    /// <summary>끝낼 수 있는 문장 앞(7.4). 바뀐 것이 있으면 열린 묶음을 Open 으로 저장한다. 저장 실패는 삼킨다.</summary>
    Task BeforeEndAsync(Guid sessionId, CancellationToken ct);
    /// <summary>끝 처리. 저장한 뒤 RollbackGroupSaved 를 낸다. 같은 세션의 Record 와 순서가 보장되게 공급자가 기다린다.</summary>
    Task EndAsync(TransactionEnded ended, CancellationToken ct);
    /// <summary>원복 실행·비교 반영이 만든 반대 문장 묶음을 바로 확정 전 묶음으로 둔다(RD-8). 그 세션의 다음 끝을 따른다.</summary>
    void RecordDerived(Guid sessionId, RollbackOrigin origin, long originGroupId, RollbackStatementRecord record);
}

// ---------- 저장 (IRollbackStore.cs, IRollbackVault.cs) — 구현: Platform ----------
public interface IRollbackVault
{
    bool IsDurable { get; }
    Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plain, ReadOnlyMemory<byte> associated, CancellationToken ct);
    /// <summary>키가 없거나 변조되면 System.Security.Cryptography.CryptographicException.</summary>
    Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> cipher, ReadOnlyMemory<byte> associated, CancellationToken ct);
}

public interface IRollbackStore
{
    bool CanStoreScripts { get; }   // = vault.IsDurable 이고 DB 를 쓸 수 있음
    /// <summary>Id = 0 이면 새로 만들고 Id 를 돌려준다. 있으면 덮어쓴다. document 가 null 이면 파일을 만들지 않는다.</summary>
    Task<long> SaveAsync(RollbackGroup group, RollbackScriptDocument? document, CancellationToken ct);
    Task<IReadOnlyList<RollbackGroup>> SearchAsync(RollbackQuery query, CancellationToken ct);   // 최근 순
    Task<RollbackGroup?> GetAsync(long id, CancellationToken ct);
    Task<RollbackScriptDocument?> LoadDocumentAsync(long id, CancellationToken ct);
    Task<int> MarkOrphansAsync(CancellationToken ct);
    Task PruneAsync(int retentionDays, CancellationToken ct);
}

// ---------- 이벤트 (RollbackEvents.cs) ----------
public sealed record TransactionEnded(Guid SessionId, TransactionEndKind Kind, TransactionEndSource Source) : Oranda.Core.Events.OrandaEvent
{
    public string? Savepoint { get; init; }
    public ChangePoint? CommitPoint { get; init; }
}
public sealed record RollbackStatementCaptured(RollbackStatementRecord Record) : Oranda.Core.Events.OrandaEvent;
public sealed record RollbackGroupSaved(long GroupId, RollbackScriptState State) : Oranda.Core.Events.OrandaEvent;

// ---------- 설정 (RollbackSettings.cs, AppSettings.Rollback, JSON 섹션 "rollback") ----------
public sealed record RollbackSettings
{
    public const int DefaultMaxRows = 100_000, MinMaxRows = 1, MaxMaxRows = 1_000_000;
    public const int DefaultRetentionDays = 30, MinRetentionDays = 1, MaxRetentionDays = 365;
    public bool RecordPoint { get; init; } = true;
    public bool ScriptOnProduction { get; init; } = true;
    public int MaxRowsPerStatement { get; init; } = DefaultMaxRows;
    public int RetentionDays { get; init; } = DefaultRetentionDays;
    public bool WarnRowCountMismatch { get; init; } = true;
    public bool SuggestBackupBeforeDataLossDdl { get; init; } = true;
}

// ---------- 끔 구현 (RollbackCapture.Disabled 등) ----------
// RB-00 이 넣는 기본값: 아무것도 하지 않는 IRollbackCapture(PrepareAsync = ConfirmExtras.None, BeginAsync = 받은 문장을 그대로 보내는 캡처),
// IRollbackLedger(아무것도 저장하지 않음), IDmlAnalyzer(null). 앱 조립은 처음에 이것을 등록한다.
```

### 13.3 기존 형식에 더하는 것 (RB-00)

| 파일 | 더하는 것 |
|---|---|
| `Oranda.Core/Execution/SqlStatement.cs` | `public Oranda.Core.Rollback.DmlShape? Dml { get; init; }` — `RowDmlBuilder` 처럼 모양을 이미 아는 만든 쪽이 채운다. 공급자는 치환 변수를 처리한 문장이면 버리고 다시 분석한다 |
| `Oranda.Core/Execution/IExecutionService.cs` (`ExecutionOptions`) | `public ChangeRecording Recording { get; init; } = ChangeRecording.Normal;` |
| `Oranda.Core/Execution/ExecutionResult.cs` | `public RollbackStatementSummary? Rollback { get; init; }` |
| `Oranda.Core/Execution/IExecutionInteraction.cs` | 기본 구현이 있는 메서드: `Task<ConfirmAnswer> ConfirmChangeAsync(SqlStatement stmt, PolicyDecision decision, ConfirmExtras extras, CancellationToken ct)` — 기본은 `ConfirmAsync` 를 불러 참이면 `ConfirmAnswer.Default(extras)`, 거짓이면 `Declined`. 지금의 가짜 구현들이 그대로 컴파일된다 |
| `Oranda.Core/Execution/Providers/PolicyGate.cs` | 덧붙인 판: `DecideAsync(…, ExecutionOptions options, ConfirmExtras extras, CancellationToken ct)`. 확인을 물을 때 `ConfirmChangeAsync` 를 부른다. `PolicyGateResult` 에 `public ConfirmAnswer Answer { get; init; }`(Allow·개발 생략·미리 확인 = `ConfirmAnswer.Default(extras)`). 지금 판은 `ConfirmExtras.None` 으로 넘긴다 |
| `Oranda.Core/Settings/AppSettings.cs` | `public RollbackSettings Rollback { get => _rollback; init => _rollback = value ?? new(); }` — 새 섹션이라 스키마 버전은 그대로(`Features` 와 같은 방식) |
| `Oranda.Core/Storage/IAppDataPaths.cs` | `string RollbackDirectory => Path.Combine(DataDirectory, "rollback");`(기본 구현) |
| `docs/design/data-model.md` | 12장의 표·파일·보존·비밀 없음 줄, 마이그레이션 0005 예약 |
| `src/Oranda.App/Features/Rollback/RollbackCommandIds.cs` | 아래 13.4 |
| `src/Oranda.App/Shell/Composition/RollbackComposition.cs` | 끔 구현 등록. 실구현이 들어오면 RB-02·RB-04 가 바꾼다 |
| `tests/Oranda.Testing/Rollback/` | `FakeRollbackChannel`(보낸 SQL 기록·준비한 결과 반환), `InMemoryRollbackStore`, `FakeRollbackVault`, `FakeRollbackDialect`(Oracle 규칙) — 레인들이 서로를 기다리지 않고 시험하게 한다 |

### 13.4 앱 명령 계약 (RB-00, 동결)

```csharp
namespace Oranda.App.Features.Rollback;

public static class RollbackCommandIds
{
    public const string OpenScript   = "oranda.rollback.openScript";    // 인자: RollbackScriptArgs. 구현 RB-43
    public const string RunScript    = "oranda.rollback.runScript";     // 활성 원복 스크립트 탭. 구현 RB-43
    public const string ComparePoint = "oranda.rollback.compare";       // 인자: PointCompareArgs. 구현 RB-32
}

public sealed record RollbackScriptArgs(long GroupId);

/// <summary>화면 3 을 여는 인자. GroupId 가 있으면 그 묶음의 기준·커밋 시점과 표, 없으면 Table·Keys(그리드)와 시점 목록.</summary>
public sealed record PointCompareArgs(Guid ProfileId, long? GroupId, Oranda.Core.Metadata.DbObjectRef? Table,
                                      IReadOnlyList<string>? KeyColumns, IReadOnlyList<object?[]>? Keys, int? DefaultMinutesAgo);
```

변경 일지(RB-42)는 이 id 로 명령을 부르기만 하고, 원복 스크립트 탭(RB-43)과 화면 3(RB-32)을 직접 참조하지 않는다.

### 13.5 공급자 쪽 순서 (RB-10·RB-11 이 따르는 의사 코드)

```text
RunAsync(s, stmt, binds, options):
  stmt = Substitute(stmt)                                   # 치환하면 stmt.Dml = null
  shape = stmt.Dml ?? analyzer.Analyze(stmt.Text)            # 쓰기 문장일 때만
  extras = Recording == None ? None : await capture.PrepareAsync(req, gatedChannel, dialect)
  extras = extras with { Backup = BackupPlanner.Offer(...) } # RB-44, 데이터가 사라지는 DDL
  gate = PolicyGate.DecideAsync(..., extras)                 # 거부·취소면 끝
  enter CommandGate
    if 끝낼 수 있는 문장(COMMIT·DDL·PL/SQL): await ledger.BeforeEndAsync(s.Id)
    if gate.Answer.BackupSql: 실행(실패면 원래 문장 보내지 않고 끝)
    cap = await capture.BeginAsync(req, extras, gate.Answer, channel, dialect)
    try   send cap.StatementToSend / cap.BindsToSend
    catch 실패·취소: summary = await cap.AbandonAsync(err, None); 지금처럼 결과
    txId = TrackTransaction()                                # 지금 코드가 읽는 id
    summary = await cap.CompleteAsync(rows, outValues, txId)
    끝 판정: 트랜잭션 제어 문장이면 종류대로, DDL·PL/SQL 이고 미커밋이 거짓이 되면 ProbeEndAsync
             끝이 커밋·모름이면 point = ReadPointAsync
             await ledger.EndAsync(new TransactionEnded(...) { CommitPoint = point }); publish 같은 이벤트
  release CommandGate
  return result with { Statement = 원래 stmt, Rollback = summary }
```

위 판단(트랜잭션 제어 해석, 끝낼 수 있는 문장, 판정 필요 여부, 기록 방식)은 Core `RollbackFlow` 가 한다. 공급자는 이 의사 코드의 보내기·통로·방언 부분만 구현한다.

`OracleSession.CommitAsync`·`RollbackAsync`(Tibero 도 같다) 는 같은 일을 줄여서 한다: 커밋 앞 `BeforeEndAsync`, 커밋·롤백 뒤 시점 읽기와 `EndAsync`.

## 14. 오류 처리

판단은 `DbError.Kind` 와 방언의 번호표로 한다(D-046). Oracle 번호는 이해를 돕는 예다.

| 오류 | 어디서 | 처리 |
|---|---|---|
| ORA-01555·ORA-08180 (`SnapshotTooOld`, Tibero 는 `CopyReadPointSupport.SnapshotTooOldMessages` 글 조각) | 시점 비교 읽기 | 「기준 시점의 값을 undo 에서 잃었습니다(undo_retention n초). 원복 스크립트가 있으면 그것을 쓰세요」. 원복 스크립트가 있는 묶음이면 「원복 스크립트 열기」 단추를 함께 보인다 |
| ORA-01466 (`DefinitionChangedNumbers`) | 시점 비교 읽기 | 「기준 시점 뒤 테이블 정의가 바뀌어(DDL) 그 시점으로 읽을 수 없습니다」. 미리 `dbinfo.flashback_ddl` 로 알면 읽기 전에 막는다 |
| ORA-08181 (`InvalidPointNumbers`) | 시점 직접 지정 | 「시점 번호가 올바르지 않습니다」 |
| ORA-30052 | VERSIONS 질의 | 8.2 의 다음 방법으로 넘어간다 |
| ORA-01031 (`InsufficientPrivileges`) | 잠금 읽기·`COUNT(*)`·메타데이터 | 원복 스크립트를 만들지 않고(「만들지 않음 · 권한 없음」) 사용자 문장은 그대로 실행한다 |
| ORA-01031·ORA-01039 | 다른 스키마 표의 시점 비교 | 「다른 스키마 테이블은 FLASHBACK 권한이 있어야 합니다」(지금 플래시백 문구와 같다) |
| ORA-00001 (`ConstraintViolation`) | 원복 실행의 INSERT | 「충돌: 같은 키가 이미 있음」, 다음 문장 계속 |
| 0행 | 원복 실행·비교 반영 | 「그 뒤 다른 사람이 바꾼 행」, 계속 |
| ORA-32795 | 원복 실행의 INSERT(ID 열) | 「ID 열이 GENERATED ALWAYS 라 넣을 수 없습니다」, 계속. 그 문장은 이미 불완전으로 표시돼 있다 |
| 시점 읽기 실패(모든 방법) | 층 B | 기록만 건너뛴다. 묶음 시점 칸은 「없음」 |
| 사용자 문장 실패·취소 | 2단계 | 내부 저장점으로 되돌리고 그 문장은 기록하지 않는다(SCN 만 남긴 기록도 버린다) |
| 3단계 실패, 원장 저장 실패 | 마무리 | 사용자 문장은 되돌리지 않는다. 문장을 「불완전 · 실행 직후 값을 읽지 못함」으로, 저장 실패는 실행 로그에 「원복 스크립트를 저장하지 못했습니다: 이유」 |
| 파일 복호화 실패(키 없음·변조) | 원복 스크립트 열기 | 「원복 스크립트를 열 수 없습니다(키가 없거나 파일이 손상됨)」. 묶음 정보와 시점 비교는 그대로 쓸 수 있다 |

## 15. Tibero 차이

목표는 Oracle 과 같은 기능 수준이다. 차이는 Tibero 방언(`TiberoRollbackDialect`)과 실측(RB-13)으로만 다룬다.

| 항목 | Oracle | Tibero | 상태 |
|---|---|---|---|
| 시점 이름 | SCN | TSN | — |
| 시점 읽기 | `GET_SYSTEM_CHANGE_NUMBER` → `TIMESTAMP_TO_SCN` | `GET_SYSTEM_CHANGE_NUMBER` → `TIMESTAMP_TO_TSN` → `V$DATABASE.CURRENT_TSN` | 6.2.15 실측 확인(#187) |
| 시점 지정 읽기 | `AS OF SCN n` | `AS OF SCN n`·`AS OF TSN n` 둘 다 됨 | 실측 확인(#187) |
| `SELECT … FOR UPDATE` | 됨 | 됨(읽기 전용 트랜잭션에서도 잠근다). 리더를 끝까지 읽고 닫은 뒤 다음 명령 | 동작 확인, 행 수 대조는 RB-13 |
| `SAVEPOINT`·`ROLLBACK TO` | 됨 | 미측정 | RB-13 |
| `RETURNING ROWID INTO` 출력 바인드 | ODP.NET 실측 필요 | 미측정. 확인 전 `SupportsReturningRowId = false`(한 행 INSERT 는 불완전) | RB-12·RB-13 |
| ROWID 글·비교 | `ROWIDTOCHAR`·`CHARTOROWID` | 18글자 글, `ROWID = :R`·`CHARTOROWID(:R)` 둘 다 됨 | 실측 확인(driver-values 5장) |
| 형식 마스크 `FF9`·`TZR`·`TM9`·`SYYYY`, `UNISTR` | 됨 | 미측정 | RB-13 |
| VERSIONS 질의 | `dbinfo.flashback_versions` | 카탈로그 없음 → 8.2 의 2번 방법(WHERE)부터 | 미측정 |
| 오류 종류 | `OracleErrorClassifier` | `TiberoErrorClassifier`, 시점 오류 번호는 미측정이라 글 조각·`UnmappedErrorsFailTable` 규칙 | 데이터 복사와 같음 |
| 트리거·CASCADE 찾기 | `ALL_TRIGGERS`·`ALL_CONSTRAINTS.DELETE_RULE` | 같은 사전 이름인지 확인(catalog-map) | RB-11 |
| 읽기 전용 프로필 | 정책 관문이 쓰기 거부 | 같다(서버가 막지 않으므로 정책 관문뿐) | 바뀜 없음 |

미측정 항목이 끝나기 전에는 Tibero 운영 접속의 원복 스크립트가 해당 문장에서 「불완전 · Tibero 미확인 기능」으로 남을 수 있다. 실측이 끝나면 방언 값만 바꾼다.

## 16. 시험

### 16.1 단위 시험 (레인이 로컬에서 돌림, 바뀐 시험 프로젝트만)

- Language: `DmlShapeAnalyzer` — UPDATE(별칭, 스키마, 따옴표 이름, 파티션 절, 여러 열 SET `(a, b) = (SELECT …)`), DELETE(`FROM` 생략), INSERT VALUES(열 목록 있음·없음), INSERT SELECT, INSERT ALL, 여러 행 VALUES, MERGE, DB 링크, 인라인 뷰 대상, 이미 있는 RETURNING·LOG ERRORS, 주석·문자열 안의 WHERE, 서브쿼리 안의 WHERE(최상위만). `RowDmlBuilder` 가 `Dml` 을 채우는지.
- Core 엔진(`FakeRollbackChannel`): 보낸 SQL 순서(저장점 → 잠금 읽기 → 사용자 문장 → 직후 값 → 원장), 상한 + 1, 행 수 다름, 실패 시 `ROLLBACK TO SAVEPOINT ORANDA$RB`, PK 없음·PK 바꿈·ROWID, 겹침(RD-13), 가상·숨김·ID·큰 열, RETURNING 붙이기와 바인드 이름 충돌, 개발 접속은 시점만, `Recording` 셋.
- 원복 스크립트 글: 시안 화면 2 의 #318 을 그대로 재현하는 기준 글 시험(역순, 머리 주석, NULL → IS NULL, 1040 행).
- 원장: 커밋·롤백·저장점(여러 개, 같은 이름 다시 걸기, 모르는 저장점), DDL·PL/SQL 의 모름 → 판정 결과 셋, 세션 끊김·닫힘, 시작 때 열림 → 확인 필요, `BeforeEndAsync` 는 바뀐 것이 있을 때만 저장, 반대 문장 묶음(RD-8), 묶음 상태 모으기(11.2).
- 시점 비교 계획기: 되돌림·되살림·지움·충돌·같음, 커밋 시점 없음, 충돌 포함, PK 없는 표 경고, `FlashbackPlanner.PlanRestore` 어댑터가 기존 시험을 통과.
- 리터럴(글 수준): 형식별 기대 리터럴, NaN·무한대, 4000바이트 넘는 글, 국가 문자 `UNISTR`.
- Platform: 금고(암복호화, AAD 다르면 실패, 변조 실패, 키 없음, 휘발 저장소면 `IsDurable = false`), 저장소(마이그레이션 0005, 저장·검색·파일 경로, 고아 표시, 보존 정리와 파일 지우기, 가림).
- 정책 관문: `ConfirmChangeAsync` 기본 구현, `PolicyGateResult.Answer` 의 경우 넷(Allow, 개발 생략, 미리 확인, 확인함).
- App(헤드리스): 확인 창 원복 줄(상한 넘음·모름·대상 아님·경고), 실행 로그 줄, 커밋 전 경고, 설정 탭 저장·범위·개발 접속 설명, 변경 일지 묶음 접기·펼치기·단추 켜짐 조건, 원복 스크립트 탭 읽기 전용·원복 실행 요약(0행·충돌), 화면 3 표·충돌 체크·만들어진 DML, 그리드 명령 G2 회귀(편집 모드·미반영 편집·200행·안내 문구).

### 16.2 Oracle 통합 시험 (Testcontainers, CI 에서만)

로컬에서 돌리지 않는다. 컨테이너 하나 안에서 시험용 사용자 두 명(소유자·조회 계정)을 쓴다.

- 동사마다 끝까지: 실행 → 커밋 → 다른 세션이 일부 행을 바꿈 → 원복 실행 → 바꾸지 않은 행만 되돌아오고 바꾼 행은 0행.
- 리터럴 왕복 행렬: 6.6 표의 형식마다 값 → 리터럴 → `WHERE c = 리터럴` 이 정확히 1행(NUMBER 38자리, TIMESTAMP(9), TZ 지역 이름, LTZ, NCHAR 비 ASCII, BINARY_DOUBLE NaN).
- 사용자 문장이 실패한 뒤 다른 세션이 그 행을 `NOWAIT` 로 잠글 수 있다(잠금이 풀렸다).
- `ROLLBACK TO SAVEPOINT` 뒤 그 뒤 문장만 무효.
- `RETURNING ROWID INTO` 출력 바인드(ODP.NET).
- PL/SQL 블록 안 COMMIT·ROLLBACK 뒤 끝 판정.
- DDL 앞 암시적 커밋(성공·실패 DDL) 뒤 판정.
- VERSIONS_XID 로 내 트랜잭션 행 고르기와 트랜잭션 id 변환(11.2·19c·26ai 중 CI 에 있는 판).
- 시점 비교: 되돌림·되살림·지움·충돌, ORA-01466(사이에 DDL).
- CTAS 권유: 백업 테이블 생성, 실패하면 원래 DDL 을 보내지 않음.

### 16.3 Tibero 실DB 시험 (win30, Tibero 시험 서버)

`ORANDA_IT_TIBERO_WRITE=1` 로 켜는 쓰기 IT 에 15장의 미측정 항목과 16.2 의 리터럴 왕복 행렬을 더한다. 결과는 `docs/design/tibero-driver-exec.md` 에 적는다. Tibero 시험 서버에 닿는 win30 레인이 돌린다(15장의 Tibero 경로 구현과 같은 레인). 접속 정보는 시험 코드·문서에 쓰지 않는다.

### 16.4 성능 시험 (마지막 항목 하나, RB-90)

기능 항목은 수치를 재지 않는다. RB-90 이 CI 의 Oracle 컨테이너에서 잰다.

- 10만 행 UPDATE·DELETE: 원복 스크립트 있음·없음의 실행 시간 비, 원복 문서 크기(암호화 뒤), 저장 시간.
- 그리드 한 행 반영(운영 접속): 시점 읽기 + 저장점 + 잠금 읽기 + 직후 값 읽기로 늘어나는 왕복(3~4회)의 시간.
- 층 B 만(개발 접속) 문장당 늘어나는 시간.
- 결과로 상한 기본값(100,000)과 「RETURNING·직후 값 1000개씩」 묶음 크기를 확정하거나 고친다.

## 17. 매뉴얼(site/) 갱신

- 새 쪽 `site/src/content/docs/run/rollback.md` 「되돌리기 지점」: 무엇이 남는가(운영 접속 원복 스크립트, 모든 접속 SCN), 확인 창 줄, 변경 일지에서 묶음 보기, 원복 스크립트 열기·실행, 이 실행 전과 비교, 상태 다섯 가지, 한계(3장 목록과 6.3 의 대상 아닌 문장), 오류 안내(14장). 사이드바(`site/astro.config.mjs`)에 「실행」 아래로 넣는다.
- `site/src/content/docs/tools/db-tools.md` 「변경 일지」: DML 실행 기록, 묶음, 새 단추, 원인 거르기.
- `site/src/content/docs/run/grid-edit.md` 「이전 값으로 되돌리기」: G2 의 새 흐름(시점 목록, 커밋 묶음 전체 보기). G3 에서 분 입력 설명을 지운다.
- `site/src/content/docs/run/execute.md` 「운영 접속 확인」: 원복 줄과 백업 테이블 권유.
- `site/src/content/docs/settings/options.md`: 「되돌리기」 탭.
- 그림: `ManualRollbackScreens`(헤드리스 시험)이 변경 일지, 원복 스크립트 탭, 화면 3, 확인 창, 설정 탭을 찍는다. 예시 접속 이름·표만 쓴다. Windows 그림은 win144 에서 같은 시험으로 만든다.

## 18. 단계

| 단계 | 내용 | 항목 |
|---|---|---|
| 1 원복 스크립트 | UPDATE·DELETE·한 행 INSERT VALUES, 저장점·잠금 읽기·직후 값 조건·행 수 대조, 커밋 단위 역순 파일·롤백 무효·암호화 보관, 변경 일지 표시, 원복 실행(커밋 안 함), SCN 기록, 확인 창·설정 | RB-00~04, RB-10~13, RB-20~22, RB-30, RB-40~43 |
| 2 시점 비교 | 화면 3, 시각 직접 지정, 그리드 통합 G1·G2 | RB-31~33 |
| 3 넓히기 | 백업 테이블 권유, 변경 일지 TRUNCATE(Oracle, Tibero) | RB-44, RB-14 |
| 3 넓히기(이번 분해 밖) | INSERT … SELECT 키 찾기, LOB 열 원복, 그리드 G3 | 19장 |
| 마무리 | 매뉴얼, 그림, 성능 시험 | RB-45, RB-46, RB-23, RB-90 |

## 19. 남은 일

- INSERT … SELECT 의 키 찾기: SELECT 목록에 키 열이 그대로 있으면 그 SELECT 를 먼저 실행해 키를 얻는다(시안). 시퀀스·트리거·식이면 지금처럼 불완전.
- LOB 열 원복: `DBMS_LOB` 없이 리터럴로 쓸 수 없으므로, 원복 스크립트 대신 「값 파일」(암호화)과 바인드 실행을 쓰는 방법을 따로 설계한다.
- 그리드 G3(옛 경로 지우기)는 G2 를 한 판 쓴 뒤 사용자 확인을 받고 한다.
- 결정 기록: 20장 목록을 조정자가 `docs/decisions.md` 에 D-068 부터 옮긴다. 이 문서는 decisions.md 를 고치지 않는다.
- WBS 반영: 분해 항목을 조정자가 `docs/wbs.md` 에 올린다.

## 20. 설계자 결정 목록

사용자가 설계자에게 맡긴 것과(RD-1~3), 시안이 정하지 않아 이 문서에서 정한 것이다. 모두 사용자가 뒤집을 수 있다.

| 번호 | 결정 | 근거 | 되돌리기 |
|---|---|---|---|
| RD-1 | (정하실 것 2) 한 행 INSERT … VALUES 는 `RETURNING ROWID INTO :ORANDA_RID` 를 붙여 넣은 행을 찾고, 실행 로그에 그 사실을 남긴다. 원복 DELETE 조건은 PK = 값 그리고 나머지 열 = 값(PK 가 없으면 모든 열 = 값 그리고 `ROWNUM = 1`), NULL 은 `IS NULL`, 1행이 아니면 보고. INSERT … SELECT 와 키를 알 수 없는 경우는 「불완전」 | 사용자 지시. `ROWNUM = 1` 은 PK 없는 표에서 같은 값의 다른 행을 함께 지우지 않으려는 것 | 쉬움 |
| RD-2 | (정하실 것 3) FLASHBACK TABLE 은 범위에 넣지 않는다 | 사용자 지시. 다른 사람의 변경까지 지우고 ROW MOVEMENT DDL 이 필요 | — |
| RD-3 | (정하실 것 4) 그리드 「이전 값으로 되돌리기」는 화면 3 으로 통합하되 G1(어댑터)·G2(명령이 화면 3 을 엶, 겉 동작 유지)·G3(옛 경로 삭제) 세 단계로 옮긴다 | 사용자 지시. 기존 동작과 시험을 깨지 않기 위해 | G2 에서 명령 처리기를 옛 경로로 돌리면 됨 |
| RD-4 | 값은 서버에서 고정 형식 글자로 읽고, 리터럴은 공급자 방언이 만든다(TIMESTAMP FF9, NUMBER TM9, 국가 문자 UNISTR, BINARY_DOUBLE R·NaN). 기존 리터럴 모양 규칙을 따르되 정밀도를 넓힌다 | .NET 값으로 바꾸면 소수 자리·시간대·큰 숫자가 줄어 조건이 0행이 된다 | 방언 안에서 바뀜 |
| RD-5 | 잠금 읽기 앞에 내부 저장점 `ORANDA$RB` 를 걸고, 사용자 문장이 실패·취소되면 그리로 되돌려 잠금을 푼다 | 별개 문장인 FOR UPDATE 의 잠금이 문장 단위 롤백으로 풀리지 않는다 | 저장점을 빼면 됨(잠금 남는 위험 돌아옴) |
| RD-6 | 트랜잭션 끝은 세션 API·트랜잭션 제어 문장·DDL·PL/SQL 뒤 미커밋 변화·세션 끊김/닫힘·앱 재시작에서만 알고, 모를 때는 표본 행으로 판정한다. 판정 못 하면 다섯 번째 상태 「확인 필요」 | 커밋 호출처가 여럿이라 하나씩 고치면 빠뜨린다. 롤백된 묶음이 「확정」으로 남는 것이 가장 위험하다 | 상태 하나 더함 |
| RD-7 | 끝낼 수 있는 문장 앞에서 열린 묶음을 「열림」으로 저장하고, 재시작 때 남은 「열림」은 「확인 필요」 | COMMIT 직후 앱이 죽어도 스크립트를 잃지 않게 | 쉬움 |
| RD-8 | 원복 실행·비교 반영은 잠금 읽기 없이(`Recording = None`) 보내고, 1행을 바꾼 문장만 골라 반대 문장 묶음을 따로 기록한다 | 원복 문장은 한 행만 바꾸므로 반대 문장이 정확하고, 10만 문장에 잠금 읽기를 하면 왕복이 수십만 번 는다 | 보통 경로로 보내면 됨(느려짐) |
| RD-9 | 암호화는 AES-256-GCM, 키는 무작위 32바이트를 `ICredentialStore` 의 `oranda:rollback:key:v1` 에 둔다. AAD 는 `orb1:{id}:{db_key}`. 휘발 저장소에서는 만들지 않는다 | 접속 비밀번호와 같은 OS 보안 저장소를 쓰면서, 파일 형식은 OS 와 상관없이 같게 | 판 번호로 바꿀 수 있음 |
| RD-10 | 메타데이터는 `oranda.db` 새 표 둘(마이그레이션 0005), 행 값·문장 글·바인드는 암호화 파일에만. 개발·테스트 묶음도 문장 글·바인드는 암호화 파일에 둔다 | `change_journal` CHECK 를 바꿀 수 없고 보존 기간이 다름. 시점 비교가 WHERE 를 다시 쓰려면 바인드가 필요 | 마이그레이션이라 되돌리기 어려움 |
| RD-11 | 확인 창의 예상 행 수는 정책 관문 앞에서 같은 WHERE 로 `COUNT(*)`, 10초 제한, 모르면 「알 수 없음」. 상한 넘음도 같은 창에서 알린다 | 명령 잠금 안에서 묻지 않기 위해, 잠금을 쥔 채 사용자에게 묻지 않기 위해 | 쉬움 |
| RD-12 | DELETE 원복은 가상 열 빼고 숨김 열 이름 적고, ID 열은 문장을 불완전으로, 큰 열은 값이 NULL 이 아닌 행만 불완전으로 둔다 | ORA-32795, 리터럴 한도 | 쉬움 |
| RD-13 | PK 없는 표에서 한 묶음이 같은 행을 두 번 바꾸면 불완전 | 되살린 행의 ROWID 가 바뀌어 앞 원복이 행을 못 찾는다 | — |
| RD-14 | 원복 스크립트 대상은 `IDmlAnalyzer` 가 단일 표로 알아본 문장뿐이다. 시노님은 풀고(TSK-13-44 규칙), 뷰는 대상 아님 | 표 참조를 그대로 잘라 쓰는 방식이 단일 표에서만 맞다 | 범위 넓히기는 3단계 |
| RD-15 | 시점 비교 범위: VERSIONS_XID → 문장 WHERE(기준·커밋 두 시점 합집합) → 표 전체(10만 행 이하) | 정확도 순서. Tibero 는 VERSIONS 가 미측정 | 순서 바꿈 |
| RD-16 | 원복 스크립트가 없는 묶음의 모름 끝은 「커밋(추정)」 | 시점 비교는 실제 차이만 보이므로 롤백 묶음이어도 해가 없다 | 쉬움 |
| RD-17 | 백업 테이블 권유는 확인 창이 나오는 곳(시험·운영)만, 이름 `ORB_<표>_<MMddHHmm>`, 세션 사용자 스키마, CTAS 실패면 원래 DDL 을 보내지 않음 | D-065 로 개발 접속은 확인이 없다. 표 소유자 스키마에는 CREATE 권한이 없을 수 있다 | 쉬움 |
| RD-18 | 행 수 다름은 실행 직후 로그 경고, 그리고 커밋할 때(툴바·편집기 COMMIT) 다시 묻는다. 자동 커밋은 묻지 않는다 | 시안 「커밋 전에 알리기」를 지키되 자동 커밋 흐름은 막지 않음 | 설정으로 끔 |
| RD-19 | 접속 간 데이터 복사(대량 경로)는 기록하지 않는다 | 운영 대상이 될 수 없고 다시 실행하면 같은 결과 | — |
| RD-20 | 원복 대상이 아닌 문장도 PL/SQL·MERGE·불확실 문장은 SCN 을 기록한다 | 시안 「대상이 아닌 문장은 SCN 만 기록」 | — |
| RD-21 | 앱 안 호출처마다 기록 방식을 정한다: LOB 쓰기·데이터 가져오기는 SCN 만, 원복 실행·비교 반영은 스스로 기록(7.5) | 운영 접속에서 쓸모없는 불완전 묶음과 지연을 막는다 | 호출처의 옵션 한 줄 |
