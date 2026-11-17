# Tibero 테이블 편집 실측 (서버 6, TDP.NET 1.1.35)

te-w30-probe, 2026-11-17. 테이블 편집기 설계([2026-11-17-table-editor-design.md](../superpowers/specs/2026-11-17-table-editor-design.md)) 12장 실측 목록 1~10 과 2차 항목 11~18.
실측은 Tibero 통합 시험 `tests/Oranda.Tibero.Tests/Integration/TableEditProbeIt.cs`(+ `.Second.cs`, 도구 `TableEditProbe.cs`)이고, Tibero 방언의 회귀 시험으로 남는다.
시험은 문장마다 기대 결과(`OK` 또는 `TBR-n`)와 카탈로그 표기를 단언하고, `DbErrorKind`(기존 `TiberoErrorClassifier`)는 출력에만 남긴다.

- 실행: `ORANDA_IT_TIBERO_*` + `ORANDA_TIBERO_DRIVER` + `ORANDA_IT_TIBERO_WRITE=1` + `ORANDA_IT_REQUIRE=1`, `dotnet test tests/Oranda.Tibero.Tests -c Debug --filter "FullyQualifiedName~TableEditProbeIt"`.
- 시험 서버: 공유 계정, `ORT_TE_` 객체만 만들고 끝에 지운다. 출력에서 계정·DB·테이블스페이스 이름은 `<owner>`·`<db>`·`<ts>` 로 가린다.
- 서버 환경: 한글 1자 = 3바이트, `NLS_LENGTH_SEMANTICS = BYTE`, `USE_RECYCLEBIN = NO`(기본).
- 아래 표의 실패 오류는 **모두 `DbErrorKind.Other`** 로 분류된다(이 회차 분류기 기준).
- 「Oracle」 칸은 Oracle 문서·기존 실측 기준이다(이 레인은 Oracle 을 재지 않았다, 설계서 15장 — Oracle 실측은 win144).

## 0. 요약 — 방언(`ITableEditDialect` Tibero)에 주는 영향

| # | 영향 | 근거 |
|---|---|---|
| A | 가상 식 변경은 `MODIFY (c AS (e))` 불가 → **DROP COLUMN + ADD 와 ⚠** | 1 (TBR-8081) |
| B | 열 순서: `*_TAB_COLS.INTERNAL_COLUMN_ID` 가 늘 NULL. `meta.columns` 의 `ORDER BY c.internal_column_id` 는 순서를 정하지 못한다 → **`ORDER BY c.column_id`** 로 고쳐야 한다(기존 결함, 이 레인은 고치지 않음) | 1 |
| C | 이름 한도 = **128바이트**(열·제약·인덱스·테이블). 한글 1자 3바이트 | 6 |
| D | BYTE/CHAR: Tibero 는 `10 CHAR` 열도 `DATA_LENGTH = 10` → `ColumnInfo.Length`·`ByteLength` 로 구분 불가. **`CHAR_USED` 를 읽어야** 거짓 차이·BYTE↔CHAR 뒤바뀜이 없다 | 7 |
| E | 형식 이름 정규화: `VARCHAR`=VARCHAR2, `NVARCHAR`=NVARCHAR2, `TIMESTAMP(6)`=TIMESTAMP, FLOAT→`NUMBER`, `NUMBER(*,0)`·INTEGER→정밀도 38 눈금 0 | 7·8 |
| F | 기본값: 쓴 대로 두되 앞뒤 공백·줄바꿈은 서버가 자른다. `DEFAULT NULL` 뒤 사전에 글자 `NULL` → 「기본값 없음」과 같게 본다 | 3·7 |
| G | 가상 식은 서버가 다시 쓴다(따옴표·대문자·괄호, `\|\|` → `CONCAT(…)`). 원본 비교는 서버 표기끼리 하거나, 편집 줄을 서버에 보내 다시 읽은 표기와 견준다 | 7 |
| H | CHECK 조건은 쓴 그대로(대소문자·공백·CRLF) | 7 |
| I | 서버 이름 제약 = `<계정>_CON<숫자>`(Oracle `SYS_C…`). TE-8 ⚠ 판정에 이 꼴을 더한다. 서버 이름 LOB 세그먼트 `<계정>_LOB<n>`, 해시 파티션 `<계정>_P<n>` | 7·11·13 |
| J | DESC 인덱스는 NORMAL·실제 열 이름·`DESCEND = DESC` 로 온다(Oracle 처럼 함수 기반으로 보이지 않는다). 공급자 `IndexInfo` 는 DESC 를 버린다 → DESCEND 를 따로 읽는다 | 5 |
| K | 함수 기반 인덱스 열 이름 = `- EXPRESSION COLUMN -`, 식은 `USER_IND_EXPRESSIONS.COLUMN_EXPRESSION`(서버 표기) | 16 |
| L | `MODIFY (c NULL)` 을 이미 NULL 허용인 열에 보내면 실패(7157) → 바뀐 속성만 쓴다(설계 8.2 그대로) | 3 |
| M | 정밀도·눈금 줄이기는 값이 맞아도 열에 데이터가 있으면 실패(7238) → 실행 전 검사 ✖ 조건은 「넘치는 행」이 아니라 「NULL 아닌 행이 있음」 | 3 |
| N | 없는 기능(문법 7001): 신원 열·INVISIBLE·DOMAIN·SET UNUSED·BFILE·`MOVE`(절 없음)·`MODIFY LOB`·해시 `ADD PARTITION`(7002)·`COALESCE PARTITION`·`MODIFY PARTITION … ADD VALUES`·`EXCHANGE PARTITION`(7002)·`SUBPARTITIONS n` 줄임꼴 | 8·11·12·13·15·18 |
| O | 휴지통 꺼짐(기본): DROP TABLE 영구, FLASHBACK = 7071. DROP COLUMN 뒤 그 앞 시점 읽기는 오류 없이 0행 → 되돌림 수단 없음. DROP 계열 ⚠ 문구에 반영 | 18 |
| P | DDL 암묵 커밋은 Oracle 과 같다(구문 오류 DDL 만 커밋 안 함) | 10 |

## 1. 가상 열

| 문장 | 결과 | 오류·DbErrorKind | 카탈로그 표기 | Oracle 과 다른 점 | 방언 영향 |
|---|---|---|---|---|---|
| `ALTER TABLE t ADD (V1 AS (A * 2) VIRTUAL)` | OK | — | `DATA_DEFAULT = ("A" * 2)`, `VIRTUAL_COLUMN = Y` | 같음 | — |
| `ADD (V2 NUMBER AS (A + 1) VIRTUAL)` | OK | — | `("A" + 1)` | 같음 | — |
| `ADD (V3 AS (UPPER(B)))` (VIRTUAL 생략) | OK | — | `UPPER("B")`, 형식 `VARCHAR` 길이 10(식에서) | 같음 | 형식 생략 가능 |
| `ADD (V4 NUMBER GENERATED ALWAYS AS (A - 1) VIRTUAL)` | OK | — | | 같음 | — |
| `MODIFY (V1 AS (A * 3))` | 실패 | TBR-8081 Other 「Column cannot be used in this clause」 | 식 그대로 | Oracle 은 허용(문서) | **DROP + ADD 와 ⚠**(열 위치가 끝으로 감) |
| `MODIFY (V2 NUMBER AS (A + 2) VIRTUAL)` | 실패 | TBR-8081 Other | | | 위와 같음 |
| `ADD (V5 AS (NO_SUCH_COL + 1) VIRTUAL)` | 실패 | TBR-8026 Other 「Invalid identifier」 | | ORA-00904 | 검사기 「가상 식 실패」 메시지 |
| `INSERT INTO t (ID, V1) …` | 실패 | TBR-8143 Other | | ORA-54013 | — |
| `DROP COLUMN V4` (가상 열) | OK | — | | 같음 | — |

열 순서: `USER/ALL/DBA_TAB_COLS.INTERNAL_COLUMN_ID` 는 모든 열에서 NULL, `COLUMN_ID` 는 0부터 셈. `ORDER BY INTERNAL_COLUMN_ID` 로 읽으면 실측에서 가상 열 V1·V2 가 ID 보다 앞에 왔다. 기존 공급자(`meta.columns`)가 이 순서를 쓴다 → 요약 B.

## 2. 이름 바꾸기 (열·인덱스)

| 문장 | 결과 | 오류 | 카탈로그 | Oracle 과 다른 점 | 방언 영향 |
|---|---|---|---|---|---|
| `ALTER TABLE o.t RENAME COLUMN A TO A2` | OK | — | 인덱스 열 이름도 A2 로 따라감 | 같음 | 8.2 그대로 |
| `RENAME COLUMN A2 TO B` (B 있음) | 실패 | TBR-7103 Other | | ORA-00957 | 검사기 「이름이 이미 있다」 |
| `ALTER INDEX o.i1 RENAME TO i2` | OK | — | | 같음 | 8.2 그대로 |
| `ALTER INDEX o.i2 RENAME TO o.i3` | 실패 | TBR-7001 Other(구문) | | Oracle 도 새 이름에 소유자 불가 | 새 이름엔 소유자를 붙이지 않는다 |

## 3. MODIFY 실패와 분류 (행이 있는 테이블)

| 문장 | 결과 | 오류 | Oracle | 방언·검사기 영향 |
|---|---|---|---|---|
| `MODIFY (S VARCHAR2(5))` — 10자 행 있음 | 실패 | **TBR-7413** Other | ORA-01441 | 검사기 ✖ 「넘치는 행 N건」 그대로 |
| `MODIFY (S VARCHAR2(10 CHAR))` · `VARCHAR2(20)` | OK | — | 같음 | — |
| `MODIFY (NL NOT NULL)` — NULL 행 있음 | 실패 | **TBR-7045** Other | ORA-02296 | 검사기 ✖ 「NULL 행 N건」 |
| `MODIFY (S NULL)` — 이미 NULL 허용 | 실패 | **TBR-7157** Other | ORA-01451 | 바뀐 속성만 쓴다(요약 L) |
| `MODIFY (S NUMBER)` VARCHAR2→NUMBER, 데이터 있음 | 실패 | **TBR-7080** Other 「must be empty before changing datatypes」 | ORA-01439 | 검사기 ✖ 형식 변경 |
| `MODIFY (N VARCHAR2(20))` NUMBER→VARCHAR2 | 실패 | TBR-7080 Other | ORA-01439 | 같음 |
| `MODIFY (D TIMESTAMP)` DATE→TIMESTAMP, 데이터 있음 | OK | — | 같음 | 허용 목록에 넣음 |
| `MODIFY (C VARCHAR2(5))` CHAR→VARCHAR2, 데이터 있음 | OK | — | 같음 | 허용 목록에 넣음 |
| `MODIFY (E NUMBER)` 빈 열 형식 변경 | OK | — | 같음 | — |
| `MODIFY (N NUMBER(3))` 값 넘침 | 실패 | **TBR-7238** Other 「must be empty to decrease precision or scale」 | ORA-01440 | |
| `MODIFY (N NUMBER(5))` 값은 맞음 | 실패 | **TBR-7238** Other | ORA-01440(같음) | 검사기 ✖ 조건 = 데이터 있음(요약 M) |
| `MODIFY (NL DEFAULT 'z')` · `DEFAULT NULL` | OK | — | | `DEFAULT NULL` 뒤 공급자 `DefaultValue = "NULL"`(요약 F) |
| `MODIFY (S VARCHAR2(30) DEFAULT 'q' NOT NULL)` | OK | — | 같음 | 한 열 여러 속성 한 문장(TE-11) 그대로 |

## 4. 제약

| 문장 | 결과 | 오류 | 카탈로그 | Oracle 과 다른 점 | 방언 영향 |
|---|---|---|---|---|---|
| `ADD CONSTRAINT n PRIMARY KEY (ID)` 중복 키 | 실패 | **TBR-7161** Other 「Unable to validate (테이블.제약): constraint violated」 | | Oracle 은 종류마다 번호가 다르다(02437·02299·02293·02298). 괄호 안이 **소유자가 아니라 테이블 이름** | 오류 번호로 종류를 가를 수 없음 → 검사기 쪽 사전 검사가 중요 |
| UNIQUE 중복 / CHECK 어김 / FK 짝 없음 | 실패 | TBR-7161 Other(모두) | | 위와 같음 | |
| 같은 문장(어기는 행 없음) PK·UK·CHECK | OK | — | `CONSTRAINT_TYPE` P/U/C, CHECK `SEARCH_CONDITION = QTY > 0` | 같음 | — |
| FK `… ENABLE NOVALIDATE` (짝 없는 행 있음) | OK | — | | 같음 | — |
| FK `ON DELETE CASCADE` · `ON DELETE SET NULL` | OK | — | `DELETE_RULE = CASCADE` · `SET NULL`, 부모 삭제 시 동작 확인 | 같음 | 8.2 그대로 |
| `DISABLE CONSTRAINT` · `ENABLE CONSTRAINT` | OK | — | `STATUS`, `VALIDATED` | 같음 | 8.2 그대로 |
| `ENABLE CONSTRAINT` 어기는 행 있음 | 실패 | TBR-7161 Other | | ORA-02293 | |
| `ENABLE NOVALIDATE CONSTRAINT` | OK | — | `ENABLED` / `NOT VALIDATED` | 같음 | |
| 참조되는 PK `DISABLE` | 실패 | TBR-7217 Other 「Dependencies exist」 | | ORA-02297 | 순서 8.3(FK 먼저) 필요 |
| 참조되는 PK `DROP CONSTRAINT` | 실패 | TBR-7156 Other 「Referenced by foreign keys」 | | ORA-02273 | 순서 8.3 그대로 |
| 없는 제약 `DROP CONSTRAINT` | 실패 | TBR-7076 Other | | ORA-02443 | |
| 다른 테이블에 같은 이름 제약 | 실패 | TBR-7104 Other | | ORA-02264 | 검사기 「이름이 이미 있다」(스키마 범위) |

## 5. DESC·UNIQUE 인덱스

| 문장 | 결과 | 카탈로그 | Oracle 과 다른 점 | 방언 영향 |
|---|---|---|---|---|
| `CREATE INDEX o.i ON o.t (A DESC, B)` | OK | `INDEX_TYPE = NORMAL`, `IND_COLUMNS`: `A` DESC, `B` ASC. `USER_IND_EXPRESSIONS` 비어 있음 | Oracle = `FUNCTION-BASED NORMAL`, 열 이름 `SYS_NC…$`, 식 `"A"` | DESC 는 `DESCEND` 로 읽는다. 공급자 `IndexInfo.Columns = [A, B]`(DESC 없음, 요약 J) |
| `CREATE UNIQUE INDEX o.i ON o.t (ID)` · `(B DESC)` | OK | `UNIQUENESS = UNIQUE`, `NORMAL` | | |
| `(B ASC, A)` · `DROP INDEX o.i` | OK | `DESCEND = ASC` | | |

## 6. 식별자 길이

| 대상 | 30바이트 | 31바이트 | 128바이트 | 129바이트 |
|---|---|---|---|---|
| 열 이름 | OK | OK | OK | TBR-23007 Other 「Identifier is too long」 |
| 제약 이름 | OK | OK | OK | TBR-23007 |
| 인덱스 이름 | OK | OK | OK | TBR-23007 |
| 테이블 이름 | OK | OK | OK | TBR-23007 |

한글(따옴표 이름, 1자 = 3바이트): 열 42자(126B) OK, 43자(129B) 실패. 제약 `ORT_TE_` + 40자(127B) OK, + 41자(130B) 실패. 한도는 글자 수가 아니라 **바이트 128**. Oracle 12.2+ 와 같고 11g(30바이트)보다 크다 → 방언 한도 = 128바이트(서버 문자 집합 바이트로 센다).

## 7. 서버 표기 (거짓 차이 정규화 입력, 설계 8.4)

열 정의(쓴 문장 → 사전 → 기존 공급자 `ColumnInfo`):

| 쓴 정의 | DATA_TYPE | 정밀도/눈금 | CHAR_LENGTH / DATA_LENGTH / CHAR_USED | `ColumnInfo` | Oracle 과 다른 점 |
|---|---|---|---|---|---|
| `NUMBER` | NUMBER | NULL/NULL | | prec·scale null | 같음 |
| `NUMBER(*,0)` · `INTEGER` | NUMBER | **38/0** | | 38/0 | Oracle = NULL/0 |
| `NUMBER(*,2)` | NUMBER | 38/2 | | 38/2 | Oracle = NULL/2 |
| `NUMBER(10)` · `NUMBER(10,2)` | NUMBER | 10/0 · 10/2 | | | 같음 |
| `FLOAT` | **NUMBER** | NULL/NULL | | NUMBER | Oracle = FLOAT 126 |
| `FLOAT(10)` | **NUMBER** | **3**/NULL | | NUMBER prec 3 scale null | Oracle = FLOAT 10 (2진 정밀도) |
| `VARCHAR2(10)` · `VARCHAR2(10 BYTE)` | **VARCHAR** | | 10 / 10 / B | VARCHAR len 10 byteLen 10 | Oracle = VARCHAR2 |
| `VARCHAR2(10 CHAR)` | VARCHAR | | 10 / **10** / C | VARCHAR len 10 byteLen **10** | Oracle DATA_LENGTH = 10×최대 바이트. **BYTE 와 구분 불가**(요약 D) |
| `CHAR(5)` · `CHAR(5 CHAR)` | CHAR | | 5 / 5 / B · 5 / 5 / C | 같게 보임 | 위와 같음 |
| `NVARCHAR2(10)` | **NVARCHAR** | | 10 / 20 / C | NVARCHAR len 10 byteLen null | Oracle = NVARCHAR2 |
| `TIMESTAMP` | **TIMESTAMP(6)** | 6/6 | | TIMESTAMP(6) | Oracle DATA_TYPE = TIMESTAMP(6) 도 같음 |

기본값(`DATA_DEFAULT`):

| 쓴 기본값 | 사전 | Oracle 과 다른 점 |
|---|---|---|
| `default 'x'` | `'x'` | 같음 |
| `DEFAULT 0` · `DEFAULT (1 + 2)` | `0` · `(1 + 2)` | 같음 |
| `default sysdate` | `sysdate`(소문자 그대로) | 같음 |
| `DEFAULT⏎   'abc'  ,` | `'abc'` — **앞뒤 공백·줄바꿈 잘림** | Oracle 은 공백까지 그대로 남긴다 |
| `DEFAULT 'It''s'` | `'It''s'` | 같음 |
| `MODIFY (c DEFAULT NULL)` 뒤 | `NULL`(글자) | 같음(Oracle 도 `NULL` 남김) |

가상 식: `n2*2 + nvl(n3,0)` → `(("N2" * 2) + NVL("N3",0))`, `upper(s0) || 'x'` → **`CONCAT(UPPER("S0"),'x')`**(Oracle 은 `UPPER("S0")||'x'`).

CHECK 조건(`SEARCH_CONDITION`, 공급자 `ConstraintInfo.Condition` 같음): **쓴 그대로** — `n2 > 0 and s0 in ('a','b')`, ` "N3" IS NOT NULL `(앞뒤 공백 유지), `s1 like 'A%'⏎     OR s1 IS NULL`(줄바꿈·들여쓰기 유지). Oracle 도 쓴 그대로.

서버 이름 제약: PK·NOT NULL 이 `<계정>_CON<숫자>`. NOT NULL 은 `CONSTRAINT_TYPE = C`, `"N0" IS NOT NULL` 로 사전에 있고 공급자는 뺀다. PK 인덱스 이름 = 제약 이름. Oracle = `SYS_C<숫자>`.

## 8. 형식 목록 (CREATE TABLE … (C 형식))

| 형식 | 결과 | DATA_TYPE |
|---|---|---|
| VARCHAR2(20) · VARCHAR(20) | OK | VARCHAR |
| NVARCHAR2(20) | OK | NVARCHAR (DATA_LENGTH 40) |
| CHAR(5) · NCHAR(5) | OK | CHAR · NCHAR (10) |
| NUMBER · NUMBER(12,3) | OK | NUMBER |
| DATE | OK | DATE |
| TIMESTAMP · TIMESTAMP(3) | OK | TIMESTAMP(6) · TIMESTAMP(3) |
| TIMESTAMP WITH TIME ZONE | OK | TIMESTAMP(6) WITH TIME ZONE |
| TIMESTAMP WITH LOCAL TIME ZONE | OK | TIMESTAMP(6) WITH LOCAL TIME ZONE |
| CLOB · NCLOB · BLOB | OK | 같은 이름 (DATA_LENGTH 4000) |
| RAW(16) | OK | RAW |
| LONG · LONG RAW | OK | LONG · LONG RAW |
| INTERVAL YEAR TO MONTH · DAY TO SECOND | OK | INTERVAL YEAR(2) TO MONTH · INTERVAL DAY(2) TO SECOND(6) |
| BINARY_FLOAT · BINARY_DOUBLE · ROWID · XMLTYPE | OK | 같은 이름 |
| **BFILE** | 실패 TBR-7001 Other | — (Oracle 은 됨) |
| LONG 열 두 개 | 실패 TBR-7041 Other | Oracle 도 안 됨(ORA-01754) |

방언 영향: 형식 고르기 목록에서 BFILE 을 뺀다. 정규화 = VARCHAR↔VARCHAR2, NVARCHAR↔NVARCHAR2, TIMESTAMP↔TIMESTAMP(6), INTERVAL 기본 정밀도.

## 9. 의뢰 스크립트 꼴 (`;` + 단독 줄 `/`)

- tbSQL 은 win30 에 없다 → **tbSQL 실측은 확인 못 함**(사용자 확인 항목).
- 갈음: 머리 주석 + `CREATE TABLE …;` · `ALTER TABLE … ADD (V AS (…) VIRTUAL);` · `COMMENT … IS 'semi;colon';` · `CREATE OR REPLACE TRIGGER … BEGIN … 'auto;' … END;` + 줄 `/` · `CREATE INDEX …;` 를 Oranda `StatementSplitter`(기본 분할 규칙)로 나누면 5문장, 모두 `Ddl`. 문자열 안 `;` 와 PL/SQL 블록 안 `;` 를 끝으로 보지 않는다. 각 문장을 서버에 보내면 모두 OK, 트리거도 동작(`'auto;'`).
- 방언 영향: 없음(9.4 의 꼴 그대로). tbSQL 에서의 `/` 처리는 사용자 확인 뒤 확정.

## 10. DDL 암묵 커밋

세션 A 에서 INSERT(미커밋) 뒤 문장, 세션 B 에서 행 수:

| A 의 문장 | B 가 본 행 | 뜻 |
|---|---|---|
| (DDL 전) | 0 | |
| `CREATE TABLE …` 성공 | 1 | 커밋됨 |
| `ALTER TABLE … DROP COLUMN 없는열` 실행 단계 실패(7072) | 2 | **실패해도 커밋됨** |
| `ALTER TABLE … ADD (` 구문 오류(7001) | 2 | 커밋 안 됨 |
| `COMMENT ON TABLE …` | 4 | 커밋됨 |

Oracle 과 같다. 방언 영향 없음. 편집기 실행 전에 미커밋 트랜잭션이 있으면 「DDL 이 먼저 커밋한다」는 기존 알림을 그대로 쓴다.

## 11. 파티션 (2차)

| 문장 | 결과 | 오류 | Oracle 과 다른 점 |
|---|---|---|---|
| RANGE `PARTITION BY RANGE (ID) (PARTITION P1 VALUES LESS THAN (100), …)` | OK | | 같음 |
| `ADD PARTITION P3 VALUES LESS THAN (300)` · `… (MAXVALUE)` | OK | | 같음 |
| `SPLIT PARTITION P3 AT (250) INTO (PARTITION P3A, PARTITION P3B)` | OK | | 같음 |
| `MERGE PARTITIONS P3A, P3B INTO PARTITION P3` | OK | | 같음 |
| `RENAME PARTITION` · `TRUNCATE PARTITION` · `DROP PARTITION` | OK | | 같음 |
| LIST `… VALUES ('A','B'), … VALUES (DEFAULT)` · `SPLIT PARTITION PD VALUES ('C') INTO (…)` | OK | | 같음 |
| `MODIFY PARTITION PA ADD VALUES ('Z')` | 실패 | TBR-7001 | Oracle 은 됨 |
| HASH `PARTITION BY HASH (ID) PARTITIONS 4` | OK | | 같음 |
| 해시 `ADD PARTITION` · `ADD PARTITION P5` | 실패 | **TBR-7002** 「Unsupported DDL」 | Oracle 은 됨 |
| `COALESCE PARTITION` | 실패 | TBR-7001 | Oracle 은 됨 |
| INTERVAL `… INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION P0 …)` + 범위 밖 INSERT | OK, 파티션 자동 생성 | | 같음 |
| 복합 `SUBPARTITION BY HASH (K) SUBPARTITIONS 2` (줄임꼴) | 실패 | TBR-7001 | Oracle 은 됨 |
| 복합 RANGE-HASH, 하위 파티션을 하나씩 적은 꼴 | OK | | 같음 |
| `EXCHANGE PARTITION P1 WITH TABLE t` | 실패 | TBR-7002 | Oracle 은 됨 |

카탈로그 뷰(모두 있음, 열 이름이 다르다):

| 뷰 | 열 | Oracle 과 다른 점 |
|---|---|---|
| USER_PART_TABLES | TABLE_NAME, PARTITIONING_TYPE, SUBPARTITIONING_TYPE, PARTITION_COUNT, DEF_SUBPARTITION_COUNT, PARTITIONING_KEY_COUNT, SUBPARTITIONING_KEY_COUNT, DEF_TABLESPACE_NAME, DEF_PCT_FREE, DEF_INI_TRANS, DEF_NEXT_EXTENT, DEF_MAX_EXTENTS, DEF_BUFFER_POOL, MAX_TRANS, INITIAL_EXTENT, MIN_EXTENT, LOGGING, INTERVAL | INTERVAL 값 = 쓴 식 그대로(`NUMTOYMINTERVAL(1, 'MONTH')`) |
| USER_TAB_PARTITIONS | TABLE_NAME, COMPOSITE, PARTITION_NAME, SUBPARTITION_COUNT, **BOUND**, **PARTITION_NO**, TABLESPACE_NAME, PCT_FREE, INI_TRANS, NEXT_EXTENT, MAX_EXTENTS, COMPRESSION, BUFFER_POOL, NUM_ROWS, BLOCKS, LAST_ANALYZED, LOGGING | `HIGH_VALUE` → `BOUND`, `PARTITION_POSITION` → `PARTITION_NO`. BOUND 예: `100`, `MAXVALUE`, `'A','B'`, `DEFAULT`, 자동 생성 INTERVAL 파티션 `TO_DATE('01-04-2026 00:00:00', 'DD-MM-YYYY HH24:mi:ss')` |
| USER_PART_KEY_COLUMNS · USER_SUBPART_KEY_COLUMNS | NAME, OBJECT_TYPE, COLUMN_NAME, COLUMN_POSITION | 같음 |
| USER_TAB_SUBPARTITIONS | TABLE_NAME, PARTITION_NAME, SUBPARTITION_NAME, BOUND, SUBPARTITION_NO, … | 해시 하위 파티션 BOUND = NULL |
| USER_PART_INDEXES · USER_IND_PARTITIONS | (LOCALITY·ALIGNMENT·BOUND·PARTITION_NO 등) | 같은 규칙 |

서버 이름 파티션 = `<계정>_P<숫자>`(해시·INTERVAL 자동 생성). 방언 영향: 파티션 장을 쓰면 Tibero 판은 해시 추가·합치기·LIST 값 추가·교환을 막고, 카탈로그 읽기는 BOUND·PARTITION_NO 를 쓴다.

## 12. 저장 속성 (2차)

| 문장 | 결과 | 카탈로그(USER_TABLES) | Oracle 과 다른 점 |
|---|---|---|---|
| `CREATE TABLE … PCTFREE 20 INITRANS 4 STORAGE (INITIAL 64K NEXT 64K) TABLESPACE ts` | OK | PCT_FREE 20, INI_TRANS 4, INITIAL_EXTENT·NEXT_EXTENT **131072**(64K 가 최소 익스텐트로 올라감) | |
| `CREATE TABLE … COMPRESS` | OK | COMPRESSION YES | |
| `ALTER TABLE … PCTUSED 50` | OK | (보이는 열 없음) | USER_TABLES 에 PCT_USED·MAX_TRANS 열이 없다 |
| `ALTER TABLE … PCTFREE 30` · `INITRANS 6` · `STORAGE (NEXT 128K)` | OK | 30 · 6 | |
| `ALTER TABLE … MOVE` (절 없음) | 실패 TBR-7001 | | Oracle 은 됨 |
| `MOVE TABLESPACE ts` | OK | 인덱스 `STATUS = UNUSABLE` → `ALTER INDEX … REBUILD` 로 VALID | 같음 |
| `MOVE TABLESPACE ts PCTFREE 25` | OK | 25 | |
| `MOVE COMPRESS` · `NOCOMPRESS` · `NOLOGGING` | OK | 이 셋 뒤 PCT_FREE 0(어느 문장이 바꿨는지는 가르지 않음), COMPRESSION NO, LOGGING NO | |

USER_TABLES 열: TABLE_NAME, TABLESPACE_NAME, PCT_FREE, INI_TRANS, LOGGING, NUM_ROWS, BLOCKS, AVG_ROW_LEN, DEGREE, SAMPLE_SIZE, LAST_ANALYZED, PARTITIONED, BUFFER_POOL, ROW_MOVEMENT, DURATION, COMPRESSION, DROPPED, READ_ONLY, TEMPORARY, MAX_EXTENTS, IOT_TYPE, INITIAL_EXTENT, NEXT_EXTENT, MIN_EXTENTS, IS_VIRTUAL. USER_SEGMENTS 에 BYTES·INITIAL_EXTENT·NEXT_EXTENT 있음.

## 13. LOB 저장 절 (2차)

| 문장 | 결과 | USER_LOBS | Oracle 과 다른 점 |
|---|---|---|---|
| `LOB (C) STORE AS (TABLESPACE ts ENABLE STORAGE IN ROW CHUNK 8192)` | OK | SEGMENT_NAME `<계정>_LOB<n>`, IN_ROW YES | CHUNK 는 받지만 읽을 열 없음 |
| `LOB (C) STORE AS seg (DISABLE STORAGE IN ROW)` | OK | SEGMENT_NAME = seg, IN_ROW NO | 같음 |
| `LOB (C) STORE AS SECUREFILE (…)` · `BASICFILE (…)` | OK | **SEGMENT_NAME = `SECUREFILE` · `BASICFILE`** | 저장 방식이 아니라 세그먼트 이름으로 읽힌다 → 방언은 이 낱말을 쓰지 않는다 |
| `ALTER TABLE … ADD (C CLOB) LOB (C) STORE AS (DISABLE STORAGE IN ROW)` | OK | | 같음 |
| `MODIFY LOB (C) (CACHE)` | 실패 TBR-7001 | | Oracle 은 됨 |
| `MOVE LOB (C) STORE AS (TABLESPACE ts)` | OK | IN_ROW 가 YES(기본)로 돌아감 | |

USER_LOBS 열: TABLE_NAME, COLUMN_NAME, SEGMENT_NAME, TABLESPACE_NAME, INDEX_NAME, IN_ROW, BUFFER_POOL, PARTITIONED, ENCRYPT, COMPRESSION, DEDUPLICATION (CHUNK·CACHE·LOGGING·SECUREFILE 없음).

## 14. 테이블 이름 바꾸기 (2차)

| 문장 | 결과 | Oracle 과 다른 점 |
|---|---|---|
| `ALTER TABLE o.a RENAME TO b` | OK, 제약 이름은 그대로 b 에 붙음 | 같음 |
| `ALTER TABLE o.b RENAME TO o.c` | 실패 TBR-7001 | 같음(새 이름에 소유자 불가) |
| `RENAME b TO c` | OK | 같음 |
| `RENAME o.c TO d` | 실패 TBR-7001 | 같음(ORA-01765) |

방언 영향: `ALTER TABLE 소유자.이름 RENAME TO 새이름`(새 이름엔 소유자 없음)을 쓴다.

## 15. 신원 열·숨김 열·도메인 (2차)

| 문장 | 결과 |
|---|---|
| `GENERATED ALWAYS AS IDENTITY` · `BY DEFAULT AS IDENTITY (START WITH …)` · `BY DEFAULT ON NULL AS IDENTITY` (CREATE·ADD·MODIFY) | 모두 TBR-7001 |
| `H NUMBER INVISIBLE` · `MODIFY (N INVISIBLE)` · `MODIFY (N VISIBLE)` | 모두 TBR-7001(알려진 미지원 재확인, `TiberoColumnReorderScripts` `InvisibleNotSupported` 와 같음) |
| `CREATE DOMAIN … AS VARCHAR2(10)` | TBR-7001 |

사전: USER_TAB_IDENTITY_COLS 뷰 없음, USER_TAB_COLS 에 IDENTITY_COLUMN·USER_GENERATED 없음(HIDDEN_COLUMN 은 있음). 방언 영향: Tibero 판 편집기에서 신원·숨김·도메인 칸을 감춘다(값은 공급자에서 늘 false/null).

## 16. 함수 기반·비트맵 인덱스 (2차)

| 문장 | 결과 | 카탈로그 |
|---|---|---|
| `CREATE INDEX i ON t (UPPER(S))` | OK | `FUNCTION-BASED NORMAL`, IND_COLUMNS `- EXPRESSION COLUMN -`, IND_EXPRESSIONS `UPPER("S")` |
| `(N + 1, ID)` | OK | 열 `- EXPRESSION COLUMN -`, `ID`, 식 `("N" + 1)` |
| `(LOWER(S) DESC)` | OK | DESCEND DESC, 식 `LOWER("S")` |
| `CREATE BITMAP INDEX i ON t (N)` | OK | `BITMAP` |

USER_IND_EXPRESSIONS 열: INDEX_NAME, TABLE_NAME, COLUMN_POSITION, COLUMN_EXPRESSION. Oracle 과 다른 점: 식 자리 열 이름(Oracle `SYS_NC00004$`). 공급자 `IndexInfo.Columns` 에 `- EXPRESSION COLUMN -` 가 그대로 온다 → 편집기는 IND_EXPRESSIONS 로 바꿔 보이고, 이 이름을 열 이름으로 다루지 않는다.

## 17. GTT·IOT (2차)

| 문장 | 결과 | USER_TABLES |
|---|---|---|
| `CREATE GLOBAL TEMPORARY TABLE … ON COMMIT DELETE ROWS` | OK | TEMPORARY YES, DURATION TRANSACTION |
| `… ON COMMIT PRESERVE ROWS` + `ADD (N NUMBER)` | OK | TEMPORARY YES, DURATION SESSION |
| `CREATE TABLE … (…, CONSTRAINT pk PRIMARY KEY (ID)) ORGANIZATION INDEX` + `ADD (M NUMBER)` | OK | IOT_TYPE IOT, PK 인덱스 INDEX_TYPE `IOT - TOP` |

Oracle 과 같다.

## 18. 휴지통 (2차)

| 문장 | 결과 |
|---|---|
| `V$PARAMETERS` `USE_RECYCLEBIN` | `NO`(기본 꺼짐 재확인) |
| `DROP TABLE t`(PURGE 없이) | OK, USER_RECYCLEBIN 0건 |
| `FLASHBACK TABLE t TO BEFORE DROP` | 실패 TBR-7071 「Schema object … was not found」 |
| `ALTER TABLE t DROP COLUMN N` 뒤 `SELECT * FROM t AS OF TIMESTAMP <그 앞>` | **오류 없이 0행**(Oracle 은 ORA-01466) |
| `ALTER TABLE t SET UNUSED (ID)` | 실패 TBR-7001 |

USER_RECYCLEBIN 열: OBJECT_NAME, ORIGINAL_NAME, TYPE, TS_NAME, CREATETIME, DROPTIME, DROPTSN, BASE_OBJECT, SPACE. 방언 영향: DROP TABLE·DROP COLUMN ⚠ 에 「Tibero 휴지통이 꺼져 있어 되살릴 수 없음」, DROP COLUMN 뒤 시점 읽기로 데이터를 찾을 수 없음을 적는다(설계 12장 알려진 차이 확정).

## 19. 파티션 정비 문장 절 (T14 추가 실측, 2026-11-17)

`TableEditProbeIt.P19_partition_maintenance_clauses`. 전역 인덱스(일반 `CREATE INDEX`)와 LOCAL 인덱스가 있는 범위 파티션 테이블에서, 문장마다 인덱스를 VALID/USABLE 로 되돌린 뒤 보내고 상태를 읽었다.

| 문장 | 결과 | 전역 인덱스 | LOCAL 인덱스 | Oracle 과 다른 점 / 방언 영향 |
|---|---|---|---|---|
| `TRUNCATE PARTITION p UPDATE INDEXES` · 절 없이 | OK | VALID · VALID | USABLE | 같음 |
| `SPLIT PARTITION p AT (v) INTO (…) UPDATE INDEXES` | OK | **UNUSABLE** | USABLE | **Oracle 은 VALID**. 방언 `KeepsGlobalIndexesOnSplitMerge = false` → 빌더가 끝에 전역 인덱스 `ALTER INDEX … REBUILD` |
| `MERGE PARTITIONS a, b INTO PARTITION b UPDATE INDEXES` | OK | **UNUSABLE** | USABLE | 위와 같음. INTO 에 원래 이름(b)을 써도 된다 |
| SPLIT · MERGE 절 없이 | OK | UNUSABLE | MERGE 는 합친 파티션 UNUSABLE | 같음(Oracle 도 절 없으면 UNUSABLE) |
| `DROP PARTITION p UPDATE INDEXES` · `UPDATE GLOBAL INDEXES` | OK | VALID | — | 같음 |
| `DROP PARTITION p`(절 없이) | OK | UNUSABLE | — | 같음 → 빌더는 늘 `UPDATE INDEXES`(TE-40) |
| `ADD PARTITION … VALUES LESS THAN (…) UPDATE INDEXES` | OK | VALID | USABLE | 같음 |
| `RENAME PARTITION a TO b` | OK | — | — | 같음 |
| `SET INTERVAL (100)` · `SET INTERVAL ()` | OK | — | — | 같음. USER_PART_TABLES.INTERVAL = `100` · NULL |
| `SPLIT … ONLINE` | TBR-7001 | | | **ONLINE 없음** |
| `MODIFY PARTITION BY RANGE (…) (…) [ONLINE]`(일반 → 파티션) | TBR-7001 | | | **없음**(Oracle 12.2+ 는 됨) → `SupportsOnlinePartitionConversion = false`, 일반 → 파티션은 다시 만들기 방식(TE-41) |
| LIST `ADD PARTITION p VALUES ('C', 'D')` · `VALUES (DEFAULT)` | OK | | | 같음. BOUND = `'C','D'` · `DEFAULT` |
| LIST `MERGE PARTITIONS PA, PB INTO PARTITION PAB UPDATE INDEXES` | OK | | | 같음. BOUND = `'A','B'` |

사전: `ALL_PART_TABLES`(OWNER·PARTITIONING_TYPE·SUBPARTITIONING_TYPE·INTERVAL)·`ALL_PART_KEY_COLUMNS`·`ALL_SUBPART_KEY_COLUMNS`(OWNER·NAME·OBJECT_TYPE·COLUMN_NAME·COLUMN_POSITION)·`ALL_PART_INDEXES`(OWNER·INDEX_NAME·TABLE_NAME·LOCALITY) 는 Oracle 과 열 이름이 같다. 파티션 목록만 BOUND·PARTITION_NO(11장). 경계 채우기의 `DATE 'YYYY-MM-DD'` 는 BOUND 에 쓴 그대로 남는다(`TiberoPartitionEditIt`).

## 20. 열 형식 변경 자동 변환 검사 (T17b, 2026-11-17, Tibero 6.2.15, 시험 계정 `ORT_TE_` 객체)

회귀 시험 = `TiberoColumnConversionIt.Probe_conversion_building_blocks`(번호를 단언), 구현 = `TiberoColumnConversionChecks`.

| 항목 | 결과 | 쓰는 곳 |
|---|---|---|
| `VALIDATE_CONVERSION(s AS NUMBER)` | TBR-8007(없음) | 「변환 안 되는 행」은 늘 PL/SQL 행 단위 시험(Oracle 12.2+ 기본식만 집합 질의) |
| `CAST('abcd' AS VARCHAR2(3))` | `abc`(자름, Oracle 같음) | 검사는 CAST 가 아니라 변수 담기로 본다 |
| `CAST(12345.67 AS NUMBER(3,0))` · `CAST('x' AS NUMBER)` · `TO_DATE('x', 'YYYY-MM-DD')` | TBR-5111 · TBR-5074 · TBR-5113 | 첫 오류 문구(✖ 줄에 붙음). 드라이버 문구는 `TBR-` 또는 `ODP-` 로 시작 |
| 변수 담기: `VARCHAR2(3) := 'abcd'`, `NUMBER(3,0) := 12345.67`, `CHAR(2) := 'abc'`, `VARCHAR2(4 BYTE) := '가나'`, `RAISE VALUE_ERROR` | 모두 TBR-15115 | `WHEN OTHERS` 로 센다. SQLCODE = -15115 |
| `NUMBER(5,0) := 12345.4` · `VARCHAR2(2 CHAR) := '가나'` | OK(반올림·글자 기준) | Oracle 과 같음 |
| 글 변수 `OPEN c FOR q` + `FETCH … BULK COLLECT INTO … LIMIT 500`(TABLE OF 새 형식) | 됨, 넘침·숫자 아님은 TBR-15115 | 1차(모든 행) |
| `EXECUTE IMMEDIATE q INTO v USING r.rid`(끝에 `ROWID = :rid`) · `ROWIDTOCHAR`·`CHARTOROWID` | 됨 | 2차(행마다 세고 ROWID 100개) · 「그 행 보기」 |
| 동적 SQL 문법 오류(`SELECT (s +) …`) | OPEN 에서 TBR-8015 | 틀린 변환식 = 모든 행 실패 + 서버 오류 |
| 블록 결과 | OUT 바인드(NUMBER·VARCHAR2 4000)로 받음 | `TiberoBindBuilder` 경로(DBMS_OUTPUT 거두기와 같음) |
| 새 형식이 틀림(`TABLE OF NO_SUCH_TYPE`) | ODP-15146 PSM 컴파일 오류(+ ODP-15044) | 블록 자체 실패 → 「1건 이상」 + 서버 오류 |
| 취소(`DbCommand.Cancel`) | ODP-12040. 행 하나 계산이 가벼우면 0.8초(토큰 시각)에 멈춤. 행마다 사전 전체를 두 번 훑는 무거운 식이면 일반 질의 약 11초, PL/SQL 블록 약 29~56초 — 행 계산이 끝나야 듣는 것으로 보임 | 블록은 SQLCODE -12040(·-1013)을 다시 던짐, 삼켜져 오류 문구로 돌아오면 취소로 되돌림. 무거운 변환식이면 시간 제한(기본 10초)이 늦게 끊길 수 있음 |
| `ADD ("S$1" …)`·`UPDATE … SET S$1`·`RENAME COLUMN S$1 TO S2`·`RENAME COLUMN "S2" TO "S$1"` | 됨(따옴표 있든 없든) | 임시 열 이름 `<열>$N` |
| `DROP COLUMN c CASCADE CONSTRAINTS` | 됨. 일반·DESC 인덱스·UK 는 함께 지워지지만 **그 열을 쓰는 함수 기반 인덱스(`UPPER("C")`)는 남는다** | 남은 인덱스 때문에 4단계 `RENAME COLUMN c$1 TO c` 가 TBR-8026 → 바꿔치기 3단계가 DROP COLUMN 뒤 `ALL_INDEXES` 에 아직 남은 그 열의 인덱스(PK·UK 에 딸리지 않은 것)를 실제 소유자로 지우는 블록을 돈다(다시 돌려도 안전, Oracle 은 이미 지워져 아무것도 안 함). 남은 단계 스크립트는 옛 열 DROP 전이면 확인 블록부터. 5단계가 다시 만든다 |
| `RAISE_APPLICATION_ERROR(-20990, …)` | TBR-20990 | 3단계 확인 블록 |

함정(구현 중 발견): `meta.constraints` 의 `LIKE '%IS NOT NULL'` 이 `ID > 0 OR ORDER_NO IS NOT NULL` 같은 사용자 CHECK 를 빼서 바꿔치기 뒤 사라졌다 → SQL 에서 거르지 않고 `MetadataRows.IsNotNullCheck`(서버 꼴 `"열" IS NOT NULL` 그대로만) 가 뺀다. 사용자가 `CHECK ( "N3" IS NOT NULL )` 로 쓴 것은 앞뒤 공백이 남아(`TableEditProbeIt.P07`) 사용자 CHECK 로 남는다.

IT `TiberoColumnConversionIt`(7개): 실측 조각 / 「변환 안 되는 행」 3건·NOT NULL 이면 NULL 행 +1·길이 줄이기(자르지 않고 셈)·「그 행 보기」 / 틀린 식·틀린 형식 / 변환한 값의 UK 중복·CHECK 위반·계산 오류 / 참조 FK / 취소 / 바꿔치기 1~6 실행 뒤 형식·NOT NULL·기본값·주석·UK·CHECK·인덱스 3개(일반·함수 기반·DESC)·값.
