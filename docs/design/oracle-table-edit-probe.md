# Oracle 테이블 편집 실측 (T3a)

작성일: 2026-11-17. 대상: 테이블 편집기 설계(`docs/superpowers/specs/2026-11-17-table-editor-design.md`) 8.2·8.4·12장·18장의 Oracle 쪽 확인 항목. Tibero 쪽 정본은 `docs/design/tibero-table-edit-probe.md`.

서버:

| 이름 | 판 | 비고 |
|---|---|---|
| 26ai | Oracle AI Database 26ai Free 23.26.3.0.0 (`oracle-26ai-free`, FREEPDB1) | Free 는 EE 기능(파티션·비트맵) 포함 |
| 11g | Oracle Database 11g XE 11.2.0.2.0 (`oranda-ora11g`, XE) | XE 는 파티션·비트맵 없음 |

방법: 두 서버에 임시 계정(`ORT_TE_PROBE`, 실행 끝에 `DROP USER … CASCADE`, 남은 계정 0 확인)을 만들고 그 계정으로 SQL*Plus 스크립트를 돌렸다. 세션 `NLS_LENGTH_SEMANTICS = BYTE`, DB 문자 집합 AL32UTF8. 남의 계정은 건드리지 않았다(SYSTEM 은 계정 만들기·지우기와 「다른 계정 테이블 이름 바꾸기」 확인에만 씀).

## 1. 1차 항목

| # | 항목 | 26ai | 11g | 편집기에 주는 뜻 |
|---|---|---|---|---|
| 1 | 가상 식 바꾸기 `ALTER TABLE t MODIFY (c AS (expr))` | 됨. 형식 생략 가능, `MODIFY (c NUMBER AS (expr))` 도 됨. 데이터 있어도 됨 | 됨(같음) | `SupportsVirtualColumnModify = true`. DROP+ADD 대체 불필요. 설계 8.2 「[구현: 실측]」 → 「Oracle 11g·26ai 모두 허용」으로 고침 |
| 1a | 가상 식 결과가 지정 길이를 넘음 `MODIFY (vu VARCHAR2(10) AS (…))` | ORA-12899 | ORA-12899 | 실행 전 검사 「가상 식 계산 실패」와 같은 묶음(서버 오류 그대로) |
| 1b | 형식 생략 가상 열의 서버 형식 | `UPPER(s)` → VARCHAR2(20 BYTE), `CASE … 'P' …` → CHAR(1), 식 바꾸면 길이도 다시 정함(15) | 같음 | 형식 칸이 빈 가상 열은 원본 형식과 비교하지 않음(서버가 정한 값) |
| 2 | 가상 식 서버 표기(`DATA_DEFAULT`) | 대문자·따옴표·공백 제거: `a + 1↵` → `"A"+1`, `upper(s)` → `UPPER("S")`, `nvl(s,'-')` → `NVL("S",'-')`, CASE 는 `CASE WHEN "A">0 THEN 'P' ELSE 'N' END`(CASE 뒤 공백 수는 서버가 정함) | 같음 | 원본·편집 모두 서버 표기에서 오므로 「안 고치면 0문장」은 앞뒤 공백·끝 줄바꿈만 다듬으면 됨. 사용자가 친 식은 서버 표기와 다르므로 실행 뒤 다시 읽어야 같은 꼴(TE-5 다시 계산 때 원본은 서버 표기) |
| 3 | `NUMBER` 계열 카탈로그 | `NUMBER` → prec NULL·scale NULL. `NUMBER(*,0)`·`INTEGER` → prec NULL·scale 0. `NUMBER(10)` → 10·0. `NUMBER(*,2)` → NULL·2. `FLOAT` → FLOAT prec 126, `FLOAT(10)` → 10 | 같음 | 정규화: prec NULL·scale 0 = `NUMBER(*,0)`(INTEGER 와 같은 것). `NUMBER` 와 `NUMBER(*,0)` 은 **다른 형식**(scale NULL vs 0) — 같게 보면 안 됨. `MODIFY (n NUMBER)` 는 `NUMBER(*,0)` 을 `NUMBER` 로 되돌림 |
| 4 | `CHAR_USED`(길이 기준) | `VARCHAR2(10 CHAR)` → C, `(10 BYTE)`·`(10)`(세션 BYTE) → B, `NVARCHAR2`·`NCHAR` → C, `CHAR(3 CHAR)` → C. `CHAR_LENGTH` = 글자 수, `DATA_LENGTH` = 바이트(CHAR 기준이면 ×4) | 같음 | `CharSemantics` 는 `CHAR_USED` 로 읽음(N 계열은 기준 없음 = null 로 둠) |
| 4a | 기준을 안 쓴 MODIFY | `VARCHAR2(10 CHAR)` 열에 `MODIFY (c VARCHAR2(20))` → **BYTE 로 바뀜**(세션 기본값). `MODIFY (c VARCHAR2(20 CHAR))` 는 C 유지 | 같음 | 기존 열 길이 MODIFY 는 원래 기준을 **반드시 써야** 함: CHAR 이면 `n CHAR`, BYTE 이면 `n BYTE`(세션 기본값이 CHAR 일 수 있음). 새 열은 TE-6 대로 생략 |
| 5 | 기본값 서버 표기(`DATA_DEFAULT`, LONG) | `DEFAULT` 뒤 원문을 다음 `,`/`)` 직전까지 그대로 저장: `'A'↵␠␠`, `'x'␠`, `5↵`, `'y'↵`, `SYSDATE`. `MODIFY (c DEFAULT NULL)` → 텍스트 `NULL`(4자, 실제 NULL 아님) | 같음 | 정규화: 앞뒤 공백·줄바꿈 다듬기, 다듬은 결과가 `NULL`(대소문자 무시)이면 기본값 없음과 같게. 그러면 기본값 없앤 열이 「기본값 NULL」로 거짓 차이가 나지 않음 |
| 5a | 신원 열의 `DATA_DEFAULT` | `"OWNER"."ISEQ$$_124062".nextval` | (신원 없음) | 신원 열의 `DATA_DEFAULT` 는 기본값이 아님 — 방언이 기본값으로 넘기지 않아야 거짓 `MODIFY DEFAULT` 가 안 남 |
| 5b | `DEFAULT ON NULL` | `DEFAULT_ON_NULL = YES`, `NULLABLE = N` 자동 | (12c+) | 첫판 범위 밖 표시용. 거짓 NOT NULL 차이 주의(ON NULL 이면 NULL 칸 비교 제외) |
| 6 | 주석 빈 문자열 | `COMMENT … IS ''` → `COMMENTS` NULL | 같음 | 주석 NULL = '' 정규화 확인 |
| 7 | CHECK 조건 서버 표기(`SEARCH_CONDITION`, LONG) | 사용자 원문 그대로(소문자·줄바꿈·탭 보존): `pc IN ('a','b') and p2↵\tbetween 1 and 9`. 시스템 NOT NULL 은 `"P2" IS NOT NULL`(GENERATED NAME) | 같음 | 조건은 다듬기만, 대소문자 바꾸지 않음(리터럴 포함). `SEARCH_CONDITION_VC` 는 26ai 에 있음, 11g 는 LONG 만 |
| 8 | FK 삭제 규칙 `DELETE_RULE` | `NO ACTION`·`CASCADE`·`SET NULL`. `R_OWNER`·`R_CONSTRAINT_NAME` 으로 참조 키(PK 또는 UK) | 같음 | 「없음」 = `NO ACTION` |
| 9 | ENABLE/DISABLE | `DISABLE CONSTRAINT` → DISABLED·NOT VALIDATED, `ENABLE` → ENABLED·VALIDATED | 같음 | 문장 꼴 그대로 |
| 10 | DESC 인덱스 카탈로그 | `INDEX_TYPE = FUNCTION-BASED NORMAL`, `ALL_IND_COLUMNS.COLUMN_NAME = SYS_NC00006$`, `DESCEND = DESC`, `ALL_IND_EXPRESSIONS.COLUMN_EXPRESSION = "P1"`(따옴표 붙은 열 이름). ASC 열은 원래 이름 | 같음 | DESC 열 = 식이 `"열"` 꼴뿐인 표현식 → 일반 열 + DESC 로 되돌려 읽음. 이것만 있는 인덱스는 「NORMAL」(함수 기반 아님)으로 봄 |
| 10a | DESC 유일 인덱스로 UK 강제 | `ADD CONSTRAINT … UNIQUE (pc) USING INDEX ix(pc DESC)` → ORA-14196 | 같음 | 참고만(편집기는 USING INDEX 를 쓰지 않음) |
| 10b | 제약이 만든 인덱스 | `ALL_CONSTRAINTS.INDEX_NAME`(P·U), 이름은 제약 이름과 같음(SYS_C… 포함) | 같음 | `IndexInfo.ConstraintName` 은 이 열로 짝지음 |
| 11 | 이름 길이 한도 | 128 바이트(38자 테이블 이름 됨, 한글 10자 = 30바이트·11자 = 33바이트 모두 됨) | **30 바이트**(31자 → ORA-00972, 한글 10자 = 30바이트 됨, 11자 = 33바이트 거부) | `MaxIdentifierBytes`: 12.2 이상 128, 그 밖 30. 바이트는 AL32UTF8 기준(한글 3바이트) |
| 12 | 길이 줄이기 실패 | ORA-01441 | ORA-01441 | 실행 전 검사 ✖(넘치는 행) |
| 12a | 데이터 있는 열 형식 바꾸기 | ORA-01439 | ORA-01439 | 실행 전 검사 ✖(데이터 있음) |
| 12b | NULL 행 있는데 NOT NULL | ORA-02296 | ORA-02296 | 실행 전 검사 ✖(TE-18) |
| 12c | NUMBER 정밀도·소수 줄이기 `NUMBER(10,2)` → `(8,2)`·`(10,1)`, `NUMBER` → `NUMBER(5,2)` | **값이 새 정의에 들어가도 ORA-01440**(NULL 아닌 값이 하나라도 있으면). 모두 NULL 이면 됨. 넓히기(`(5)` → `(10,2)`, `(10,2)` → `(12,4)`)는 됨 | 같음 | 숫자 「넘침」 검사 = 줄이면 NULL 아닌 행 모두 ✖(정수 자리 계산이 아님). `OracleTableEditChecks.OverflowCountAsync` 가 지금 정의를 읽어 가른다 |
| 12d | `CHAR(10)` → `CHAR(5)`(값 `'ab'`) | ORA-01441(빈칸 채움까지 길이) | 같음 | CHAR 줄이기 = NULL 아닌 행 모두(LENGTH 가 빈칸 포함) |
| 12e | `VARCHAR2(10 CHAR)` 값 `'가나다'` → `(3 CHAR)` 됨, → `(5 BYTE)` ORA-01441 / `RAW(8)` 2바이트 → `RAW(2)` 됨, `RAW(1)` ORA-01441 | 같음 | 같음 | 새 기준대로 `LENGTH`/`LENGTHB`/`UTL_RAW.LENGTH` |
| 12f | 복합 UK 의 NULL | `(1,NULL)` 두 줄 → ORA-02299, `(NULL,NULL)` 두 줄은 됨 | 같음 | 겹침 검사 = 모두 NULL 인 키만 빼고 `GROUP BY`(NULL 끼리 같게 묶음) |
| 12g | 빈 행 위 식 확인 `SELECT 식 FROM (SELECT CAST(NULL AS VARCHAR2(10)) AS a FROM DUAL) WHERE 1 = 0` | 없는 열이면 ORA-00904, 맞으면 행 0 | 같음 | 새 테이블 가상 식 문법 확인(설계 7.6) |
| 12i | 소수만 늘리기: `NUMBER(10,2)` → `(10,4)`, `NUMBER(5)` → `(5,1)` / `(10,2)` → `(12,4)`, `(10,2)` → `(11,2)`, `NUMBER(*,0)` → `(*,2)` | 앞 둘 ORA-01440(정수 자리가 줄어서), 뒤 셋은 됨(정밀도를 안 쓴 `(*,s)` 는 정수 자리 규칙 없음) | 같음 | 줄이기 판정 = 정밀도·소수·(정밀도를 쓴 열의) 정수 자리 중 하나라도 줄면 |
| 12h | 바인드 이름 `:rows` | ORA-01745(예약어) | — | 카탈로그 바인드는 `:max_rows` |
| 13 | `ALTER INDEX … RENAME TO`, `RENAME CONSTRAINT` | 됨 | 됨 | 문장 꼴 그대로 |

## 2. 2차 항목 (설계 18장)

| # | 항목 | 26ai | 11g | 편집기에 주는 뜻 |
|---|---|---|---|---|
| 18.1 | 테이블 이름 바꾸기 `ALTER TABLE o.t RENAME TO n` | 됨(다른 계정 테이블도 소유자 붙여 됨). `RENAME TO o.n` 은 ORA-14047. PK 제약·인덱스 이름은 그대로(`PK_TE_RN` 유지) | 같음 | 설계 18.1 문장 꼴 확인. 규칙 이름 따라 바꾸기는 TE-38 체크 |
| 18.5 신원 | 새 열 `GENERATED ALWAYS AS IDENTITY (START WITH 10 INCREMENT BY 5)`, `ADD (c … GENERATED … AS IDENTITY)` | 됨. **한 테이블에 신원 열 하나만**(둘째 ORA-30669) | 문법 없음(ORA-02000) | 신원 열이 이미 있으면 새 신원 ✖ |
| 18.5 신원 | 신원 열 옵션 바꾸기 `MODIFY (id GENERATED BY DEFAULT AS IDENTITY (START WITH 100 …))` | 됨 | — | 문장 꼴 그대로 |
| 18.5 신원 | `MODIFY (c DROP IDENTITY)` | 됨 | — | 문장 꼴 그대로 |
| 18.5 신원 | 일반 열 → 신원 `MODIFY (c GENERATED … AS IDENTITY)` | **ORA-30673, 빈 테이블에서도**(신원 아닌 열은 신원으로 못 바꿈. 신원을 지운 열도 다시 못 바꿈) | — | 설계 18.5 「데이터 있는 일반 열을 신원으로 바꾸기는 ✖」 → 「일반 열을 신원으로 바꾸기는 데이터와 상관없이 ✖(새 열로 추가만)」로 고침 |
| 18.5 신원 | 신원 카탈로그 | `IDENTITY_COLUMN = YES`, `ALL_TAB_IDENTITY_COLS.GENERATION_TYPE`(ALWAYS·BY DEFAULT), `IDENTITY_OPTIONS` 문자열(`START WITH: 100, INCREMENT BY: 1, …`), ON NULL 은 `DEFAULT_ON_NULL = YES` | `IDENTITY_COLUMN` 열 없음 | 시작·증가 값은 `IDENTITY_OPTIONS` 를 나눠 읽음 |
| 18.5 숨김 | `MODIFY (c INVISIBLE)` / `(c VISIBLE)` | 됨. 숨기면 `COLUMN_ID` NULL·`HIDDEN_COLUMN = YES`(`USER_GENERATED = YES`), 다시 보이면 **맨 뒤 번호**로 감 | 문법 없음 | ⚠「열 순서가 맨 뒤로」 문구 확인 |
| 18.5 도메인 | `MODIFY (c DOMAIN d)` | 됨. 이미 도메인이 있으면 ORA-11512(먼저 DROP), 형식이 다르면 ORA-11517 | 문법 없음 | 도메인 바꾸기 = DROP 뒤 DOMAIN 두 문장 |
| 18.5 도메인 | 도메인 지우기 | **`ALTER TABLE t MODIFY (c) DROP DOMAIN [PRESERVE CONSTRAINTS]`** — 괄호 안에 열 이름만. `MODIFY (c DROP DOMAIN)`·`MODIFY c DROP DOMAIN` 은 ORA-02000 | — | 설계 18.5 문장 `MODIFY (c DROP DOMAIN)` → `MODIFY (c) DROP DOMAIN` 으로 고침 |
| 18.5 도메인 | 도메인 CHECK | 도메인에 CHECK 가 있으면 열마다 **시스템 이름 CHECK 제약**(`SYS_C…`, 조건 `"E"<>'zz'`)이 테이블에 생김. `DROP DOMAIN` 은 그 제약도 지우고, `PRESERVE CONSTRAINTS` 는 남김 | — | 제약 페이지에서 도메인이 만든 CHECK 를 사용자 CHECK 로 보이면 지우기·⚠ 가 꼬임 — T3b 에서 구분 방법 확인(남은 일) |
| 18.6 | 함수 기반 인덱스 `CREATE INDEX ix ON t (UPPER(pc))`, 식+DESC 섞임 | 됨. `INDEX_TYPE = FUNCTION-BASED NORMAL`, 식은 `UPPER("PC")`(서버 표기), 열 이름 `SYS_NC…` | 같음 | 식 정규화 = 가상 식과 같은 서버 표기 |
| 18.6 | 비트맵 `CREATE BITMAP INDEX` | 됨(`INDEX_TYPE = BITMAP`) | **ORA-00439 feature not enabled: Bit-mapped indexes**(XE) | Edition 판정: XE·SE 는 비트맵 ✖/흐리게. 파티션도 XE 에서 ORA-00439 |
| 18.10 GTT | `CREATE GLOBAL TEMPORARY TABLE … ON COMMIT PRESERVE/DELETE ROWS` | 됨. `TEMPORARY = Y`, `DURATION = SYS$SESSION`(PRESERVE)·`SYS$TRANSACTION`(DELETE), `TABLESPACE_NAME` NULL | 같음 | `TableInfo.Kind = Temporary` 판정 열 |
| 18.10 GTT | 기존 GTT 의 열 추가·지우기·기본값·주석·이름 바꾸기·PK·인덱스 | 다 됨 — **단, 어떤 세션이든 그 GTT 에 행이 묶여 있으면 ORA-14450**(PRESERVE ROWS 는 COMMIT 해도 묶임, TRUNCATE 뒤 됨) | 같음 | 18.10 「서버가 허용하는 열 추가·지우기까지」 → 열·제약·인덱스 편집 모두 허용, 실행 오류 ORA-14450 은 「다른 세션이 쓰는 임시 테이블」로 안내 |
| 18.10 IOT | `CREATE TABLE … (… PRIMARY KEY …) ORGANIZATION INDEX` | 됨. `IOT_TYPE = IOT`, PK 인덱스 `INDEX_TYPE = IOT - TOP`, `TABLESPACE_NAME` NULL | 같음 | `TableInfo.Kind = Iot` 판정 열 |
| 18.10 IOT | 기존 IOT 의 열 추가·지우기·MODIFY·이름 바꾸기·보조 인덱스 | 됨 | 됨 | 허용 |
| 18.10 IOT | IOT 의 PK 지우기 | ORA-25188 | ORA-25188 | IOT 의 PK 지우기·사용 안 함 = ✖(흐리게) |
| 18.4 | 파티션 테이블 만들기 | 됨(`PARTITIONED = YES`) | ORA-00439(XE) | 파티션 기능은 edition 판정 필요(T14) |
| — | 테이블 종류 열 | `ALL_TABLES.TEMPORARY`·`DURATION`·`IOT_TYPE`·`PARTITIONED`·`CLUSTER_NAME`·`EXTERNAL`·`TABLESPACE_NAME` | `EXTERNAL` 열 없음(ORA-00904) — 외부 테이블은 `ALL_EXTERNAL_TABLES` 로 | `.11.sql` 변형은 `EXTERNAL` 대신 `ALL_EXTERNAL_TABLES` 존재로 판정 |

## 3. 카탈로그 함정 (T3b 메타데이터 보강용)

- `DATA_DEFAULT`·`SEARCH_CONDITION` 은 LONG 이라 SQL 안에서 `||`·`REPLACE` 를 못 쓴다(ORA-00932). 지금처럼 드라이버로 읽는다(`InitialLONGFetchSize = -1`). 26ai 는 `DATA_DEFAULT_VC`·`SEARCH_CONDITION_VC` 가 있으나 11g 에는 없다.
- 26ai 에서 기본값 있는 열을 `ADD` 하면 숨은 `SYS_NC000nn$ RAW(513)` 열이 생긴다(`USER_GENERATED = NO`). 지금 `meta.columns` 는 `user_generated = 'YES'`(11g 는 `hidden_column = 'NO'`)로 걸러서 보이지 않는다 — 그대로 둔다.
- 함수 기반·DESC 인덱스의 숨은 `SYS_NC…` 열도 `USER_GENERATED = NO`(11g `HIDDEN_COLUMN = YES`)라 열 목록에 안 나온다. 인덱스 열은 `ALL_IND_COLUMNS` + `ALL_IND_EXPRESSIONS`(같은 `COLUMN_POSITION`)를 합쳐 읽는다.
- `CHAR_USED` 는 `VARCHAR2`·`CHAR` 에만 뜻이 있다(`NVARCHAR2`·`NCHAR` 는 늘 C, 그 밖은 NULL).

## 4. 설계서 고치는 안 (조정자 몫)

1. 8.2 「가상 식 변경」: `MODIFY (c AS (expr))` — Oracle 11g·26ai 모두 허용(데이터 있어도). DROP+ADD 대체 없음.
2. 8.4 거짓 차이 목록에 더함: 기본값 `NULL` 텍스트 = 없음, 신원 열 `ISEQ$$` 기본값 무시, `NUMBER` ≠ `NUMBER(*,0)`.
3. 7.2·TE-6 에 더함: 기존 문자 열 길이 MODIFY 는 원래 기준(`CHAR`/`BYTE`)을 꼭 쓴다(안 쓰면 세션 기본값으로 바뀜 — 실측 4a).
4. 18.5 신원: 「데이터 있는 일반 열 → 신원 ✖」 → 「일반 열 → 신원은 늘 ✖(ORA-30673), 신원 열은 테이블에 하나만(ORA-30669)」.
5. 18.5 도메인 지우기 문장: `MODIFY (c) DROP DOMAIN [PRESERVE CONSTRAINTS]`. 도메인 바꾸기 = DROP DOMAIN 뒤 `MODIFY (c DOMAIN d)`. 도메인 CHECK 가 만든 시스템 제약 처리 규칙 필요.
6. 18.6 비트맵·18.4 파티션: 11g XE 에서 ORA-00439(SE 도 같다는 것은 Oracle 문서 기준, 실측 안 함) — edition 판정으로 흐리게.
7. 18.10 GTT: 열·제약·인덱스 편집 모두 서버가 허용(다른 세션이 행을 묶고 있으면 ORA-14450). IOT: PK 지우기·사용 안 함만 ✖(ORA-25188).
8. 10장 ✖ 첫 줄 「숫자 정밀도를 줄이는데 넘치는 행」: Oracle 은 데이터가 있으면 정밀도·소수를 줄이는 것 자체를 막는다(ORA-01440, 실측 12c) — 「데이터 있는 열의 정밀도·소수 줄이기」로 고치고 건수 = NULL 아닌 행 수.

## 5. 구현 결과 (T3b)

- 메타데이터(TE-29): `meta.columns`(char_used·신원 생성 방식·옵션·ON NULL), `meta.constraints`(delete_rule·참조 열·index_name), `meta.indexes`(descend·식·테이블스페이스), 새 `meta.table_props`(종류·테이블스페이스·파티션). 앞 열은 Tibero 와 같은 `MetadataRows` 가 읽고 뒤 열은 `Oranda.Oracle/Metadata/OracleTableEditRows` 가 읽는다.
- DESC 열은 `IndexInfo.Columns` 에 원래 열 이름(`"P1"` → `P1`), `Descending` 에 표시. 함수 식 자리는 숨은 열 이름(`SYS_NC…`)을 그대로 두고 `Expressions` 에 식(데이터 복사의 판정이 숨은 열 이름을 쓴다). `IndexType` 은 사전 값 그대로(DESC 만 있어도 `FUNCTION-BASED NORMAL`) — 편집 모델 팩터리는 `Expressions` 가 비어 있으면 일반 인덱스로 본다.
- 신원 열의 `DefaultValue` 는 지금처럼 `"O"."ISEQ$$_n".nextval` 이 온다(데이터 복사·상세가 그대로 씀). 편집 모델 쪽에서 `Identity` 가 있으면 기본값으로 비교하지 않아야 한다.
- 방언 `OracleTableEditDialect`: 이름 한도 12.2 이상 128 · 그 밖 30, 가상 식 MODIFY 됨, 신원·숨김 12.1+, 도메인 23+, 비트맵·파티션은 Standard·Express 에서 끔(에디션을 모르면 켬), 일반 → 파티션은 12.2+.
- 검사 `OracleTableEditChecks`: 카탈로그 `tableedit.*`(17개, 11g 부터 같은 문장), 이름은 늘 따옴표, 식·조건은 원문. 취소는 서버 명령 취소 뒤 `OperationCanceledException`.
