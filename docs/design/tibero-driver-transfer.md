# Tibero 내보내기·가져오기·복사 실측 (TM0a, 6.2.15)

대상: Tibero 6.2.15(시험 서버 하나, D7 — Tibero 7 은 실측하지 않음), 드라이버 Tibero.DataAccess 1.1.35. 설계 근거 = `docs/design/tibero.md` T5, 결정65(`~/.coord/tibero/t5-decision.md`).
이 문서의 1~2장은 tb-result(TM0a: 내보내기·가져오기), 「복사」 절은 tb-exec(TM0b)가 덧붙인다. 접속 정보는 적지 않는다.

측정 방법: 읽기 전용 조회(scratch catverify `q` 모드)와 통합 시험 `tests/Oranda.Tibero.Tests/Integration/TiberoTransferProbeIt.cs`(쓰기, `ORANDA_IT_TIBERO_WRITE=1`, `ORT_TM0A` 표 하나). IT 는 단계마다 `TM0A …` 줄을 시험 출력에 남긴다.

## 1. 읽기 전용 실측 (SELECT … FROM DUAL, 서버 6.2.15)

### 1.1 세션 NLS (접속 직후 기본값)
| 항목 | 값 |
|---|---|
| NLS_DATE_FORMAT | `YYYY/MM/DD` (시각 부분 없음 — 날짜 문자열 암묵 변환에 의존하면 안 됨) |
| NLS_TIMESTAMP_FORMAT | `YYYY/MM/DD HH24:MI:SSXFF` |
| NLS_TIMESTAMP_TZ_FORMAT | `YYYY/MM/DD HH24:MI:SSXFF TZR` |
| NLS_NUMERIC_CHARACTERS | `.,` (결정41 가정과 같음) |
| NLS_DATE_LANGUAGE | `KOREAN` |
| NLS_SORT / NLS_COMP | `BINARY` / `BINARY` |
| NLS_LENGTH_SEMANTICS | `BYTE` |
DB 파라미터(`nls_database_parameters`)도 같은 값(`NLS_LANG_AT_BOOT` = MSWIN949).
→ 가져오기 리터럴은 **서식 문자열을 명시한 `TO_DATE`·`TO_TIMESTAMP`·`TO_TIMESTAMP_TZ`** 나 `DATE '…'`·`TIMESTAMP '…'` 리터럴을 써서 NLS 와 무관하게 만든다(Oracle 변환기와 같은 접근).

### 1.2 리터럴 문법 (모두 SELECT 로 확인 — 성공)
| 문장 | 결과 |
|---|---|
| `SELECT * FROM DUAL` | 열 `DUMMY` 하나, 값 `X` |
| `TO_DATE('2024-03-05 13:14:15','YYYY-MM-DD HH24:MI:SS')` | 성공(DATE 는 시각 포함) |
| `DATE '2024-03-05'` | 성공(00:00:00) |
| `TO_TIMESTAMP('…','YYYY-MM-DD HH24:MI:SS.FF6')`, `…FF9` | 성공. 소수 없는 문자열에 `FF6` 서식을 줘도 성공 |
| `TIMESTAMP '2024-03-05 13:14:15.123456789'` | 성공 |
| `TO_TIMESTAMP_TZ('2024-03-05 13:14:15.123456 +09:00','YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')` | 성공 |
| `TIMESTAMP '2024-03-05 13:14:15 +09:00'` | 성공 |
| `CAST('2024-03-05' AS DATE)` | 성공(세션 NLS_DATE_FORMAT `YYYY/MM/DD` 인데도 `-` 구분 문자열이 통과 — 관대하게 해석) |
| `INTERVAL '1-2' YEAR TO MONTH` | 성공(드라이버 값 = 월 수 14) |
| `INTERVAL '3 04:05:06.789' DAY TO SECOND`, 음수 `'-3 04:05:06'` | 성공(드라이버 값 = `TimeSpan`) |
| `HEXTORAW('00FF10')` | 성공(드라이버 값 = `sbyte[]`), `RAWTOHEX(…)` = `00FF10` |
| `TO_BLOB(HEXTORAW('00FF10'))` | 성공(`DBMS_LOB.GETLENGTH` = 3) |
| `TO_CLOB('abc')` | 성공 |
| `TO_CLOB(RPAD('a',4000,'a')) \|\| TO_CLOB(RPAD('b',4000,'b'))` | 성공(8000자) |
| 한 문자열 리터럴 4000자 / 4001자 / 8000자 | **모두 성공**(`LENGTH` 가 그대로) — Oracle 의 4000바이트 리터럴 한계가 없다. 그래도 Oracle 과 같이 4000자 단위로 쪼개 `\|\|` 로 잇는 규칙은 안전하다 |
| `N'한글'`, `'it''s'` | 성공 |
| `1E-5`, `1.5E+10`, `.5`, `-0.0` | 성공(`-0.0` 은 0) |
| `TO_BINARY_FLOAT('1.5')` | 성공 |
| `CASE WHEN '' IS NULL …` | `null` — 빈 문자열은 NULL(Oracle 과 같음) |
| `SELECT TRUE FROM DUAL` | **오류 `ODP-8026 Invalid identifier`** — BOOLEAN 리터럴 없음 |
| `SELECT 123456789012345678901234567890.123456789 FROM DUAL` | 서버는 돌려주나 드라이버가 `decimal` 로 못 읽음(`Value was either too large or too small for a Decimal`, 결정40 의 NUMBER 28자리 한계) |

### 1.3 다행 INSERT 의 바탕 (UNION ALL 조합, 읽기로 확인)
- `SELECT … FROM DUAL UNION ALL SELECT … FROM DUAL …` 가 **501 갈래**, **2001 갈래** 한 문장으로 파싱·실행된다(`SELECT COUNT(*) FROM (…)` = 501 / 2001). Oracle 가져오기 배치(`INSERT … SELECT … FROM DUAL UNION ALL …`)와 같은 모양을 그대로 쓸 수 있다.

## 2. 쓰기 실측 (`TiberoTransferProbeIt` + `TiberoTransferIt`, win144 #31, `ORT_TM0A`·`ORT_TI1A`)

| 단계 | 결과 |
|---|---|
| 한 문장 다행 `INSERT … SELECT … FROM DUAL UNION ALL …` | 영향 행 3. 형식별 리터럴(`TO_DATE`·`TO_TIMESTAMP … FF6`·`TO_TIMESTAMP_TZ … TZH:TZM`·`INTERVAL '1-2' YEAR TO MONTH`·`INTERVAL '3 04:05:06.789' DAY TO SECOND`·`HEXTORAW`·`TO_BLOB(HEXTORAW)`(작은 값)·`TO_CLOB`·NUMBER·`N'…'`)과 NULL 행이 한 문장에 섞여 들어가 읽은 값이 맞다(`2024-03-05 13:14:15` / `…15.123456000` / `…15.123456 +09:00` / `+000000001-02` / `+000000003 04:05:06.789000000` / `00FF10` / `abc` / `12345.6789`) |
| 빈 문자열 `''`·`N''` | VARCHAR2·NVARCHAR2 모두 NULL 로 저장 |
| `DATE '…'`·`TIMESTAMP '…'` | `2024-03-05 00:00:00` / `2024-03-05 13:14:15.123456789`(나노초 보존). 날짜 문자열 `'2024-03-05'` → DATE 암묵 변환도 성공(세션 NLS 와 무관하게 서식 명시 리터럴을 쓰는 이유는 1.1장) |
| NUMBER 리터럴 | `1E-5`·`1.5E+10`·`.5`·`1234567890123456789012345678.1234567891`(NUMBER(38,10)) 모두 값 그대로 |
| 유니코드 | VARCHAR2 한글, `N'…'` 한글, **`N` 없는 한글을 NVARCHAR2 에 넣어도** 값이 맞다(변환기는 글자 열 모두 일반 `'…'` 사용) |
| LONG 열 | 9000자 일반 리터럴 성공, `TO_CLOB('abc')` 도 성공 → LONG 은 글자 열(Text)로 분류 |
| CLOB | `TO_CLOB(a) \|\| TO_CLOB(b) \|\| TO_CLOB(c)`(3000자×3)로 길이 9000. 한 문자열 리터럴 4001자도 성공 |
| **BLOB** | **`TO_BLOB(HEXTORAW(…)) \|\| TO_BLOB(HEXTORAW(…))` 불가 — `ODP-11003 Invalid function argument type`**(BLOB 끼리 `\|\|` 자체가 안 됨; BLOB `\|\| EMPTY_BLOB()`·`BLOB \|\| RAW` 도 같음). `TO_BLOB(HEXTORAW('…'))` 한 개는 **RAW 한계 2000바이트까지**(1000·2000바이트 성공, 2001바이트 = `ODP-11025 Data is too long for the column`). `TO_BLOB('hex글자')` 도 같은 한계, `TO_BLOB(UTL_RAW.CONCAT(a,b))` 는 작은 값에서 되지만 결과가 RAW 라 같은 2000바이트 한계로 보인다(큰 값은 실측 안 함). 문자열 리터럴 자체는 8000자까지 확인했고 10만자는 `ODP-23008 String literal is too long`(경계는 실측하지 않음). → Tibero 가져오기에서 **2000바이트를 넘는 BLOB 값은 리터럴로 만들 수 없다**: 변환기가 그 행을 이유(「BLOB 값이 N바이트라 한 리터럴 한계 2000바이트를 넘는다」)와 함께 경고로 돌리고 나머지는 넣는다(`TiberoImportValueConverter.MaxBlobBytes`). 큰 BLOB 은 바인드 경로(복사 쪽 `ExecuteArrayAsync`, TB1)로만 가능 |
| RAW(100) 에 101바이트 | 실측 못 함(BLOB 단계 실패로 건너뜀) |
| SAVEPOINT | `SAVEPOINT` → INSERT → `ROLLBACK TO SAVEPOINT` → COMMIT: 건수 16 → 18 → 16 → 16. Oracle 과 같은 문법·같은 동작 |
| 배치 안 중복 키 | **문장 단위 원자**: `ODP-4008 Duplicate unique key found during multi insert.` 로 실패, 건수 전·뒤 16 그대로. 이미 있는 키 한 행: `ODP-10007 UNIQUE constraint violation ('<사용자>'.'<표>_PK')` |
| 가져오기 오류 문구 | `행 2: TBR-10007: ODP-10007:UNIQUE constraint violation (…)` — 코드가 두 번 붙는다(`TBR-` 접두 + 드라이버 메시지의 `ODP-`). Oracle 판은 드라이버 메시지가 이미 같은 코드로 시작하면 또 붙이지 않는데 Tibero 는 접두가 다르다 → **고침(T5 켜기 전 4)**: `IDataTransferDialect.MessageStartsWithCode`(기본 = 글자 앞부분 비교, Oracle 그대로)를 Tibero 방언이 다시 구현해 맨 앞 `TBR-n`/`ODP-n` 번호가 코드와 같으면 코드를 붙이지 않는다(`행 2: ODP-10007:UNIQUE constraint violation (…)`). 번호가 다르거나 맨 앞이 아니면 그대로 붙는다 |
| 자동 커밋 | 아니다 — INSERT 뒤 `ROLLBACK` 이 되돌린다(id=300 건수 0) |
| 배치 크기 | 한 문장 `INSERT … UNION ALL`: 50행 13 ms(문장 1,958자) / 500행 69 ms(20,408자) / 2000행 528 ms(84,908자) — 모두 성공 |
| 가져오기 IT(`TiberoTransferIt`) | CSV 5행을 배치 2 로 넣어 값(따옴표·쉼표·한글·날짜·`TIMESTAMP(6)`·4500자 CLOB·RAW·NUMBER(12,2))이 맞고, 같은 파일을 다시 넣으면 5행 모두 중복 키 경고로 남고 표는 그대로, 값 오류 행은 보내기 전에 경고로 걸러진다. 2·0·0 통과 |

배치 크기 기본값 500(`ImportOptions.BatchSize`)은 Tibero 에서도 그대로 써도 된다(문장 20 KB, 69 ms).

## 3. 복사 실측 (TM0b, `TiberoBulkMeasureIt`, ORT_TBM_* 표, 쓰기 IT)

근거: win144 #28(1c095842, `TiberoBulkMeasureIt` 12 Fact 통과), #36(b0da912d, Measure_array_bind* 4 통과), #42(675e2a80, 되읽기 예외로 중간 멈춤), #53(c02f1aaf, 1 통과). 서버 6.2.15(BUILD 302172), 드라이버 Tibero.DataAccess 1.1.35. 계정 이름은 `<USER>` 로 가림.

### 3.1 배열 바인드 (ArrayBindCount)

| 모양 | 결과 |
|---|---|
값 모양 표(#36 `array_bind.shape.*`, 열 하나·3행):

| 열 / 매개변수 종류 | 형식 배열 | `object[]` |
|---|---|---|
| NUMBER / Decimal, `decimal[]` | 됨(합 6.5) | InvalidCast Decimal→String |
| NUMBER / Decimal, `string?[]`(NULL 포함) | **됨**(1.5+2, NULL 1행) | InvalidCast DBNull→String |
| NUMBER / Int64, `long[]` | 됨 | InvalidCast Int64→String |
| NUMBER / Double | — | InvalidCast Double→String |
| VARCHAR2, `string?[]`(NULL·한글) | 됨(COUNT 2, NULL 1) | InvalidCast DBNull→String |
| DATE, `DateTime[]`(NULL 없음) | 됨 | InvalidCast DateTime→String |
| CLOB, `string?[]`(5001자·NULL) | 됨(COUNT 2, 최대 5001) | InvalidCast DBNull→String |
| BLOB / Blob, `byte[][]` | **NullReferenceException** | InvalidCast Byte[]→String |
| BLOB / LongRaw, `byte[][]` | InvalidCast Byte[][]→SByte[] | — |
| BLOB / Raw | — | InvalidCast Byte[]→String |
| RAW(2000) / Raw, `byte[][]` | **NullReferenceException** | InvalidCast Byte[]→String |

- **`object[]` 배열 바인드는 값 종류와 관계없이 안 됨**: 드라이버가 원소를 string 으로 캐스트한다(글자만 든 object[] 도 DBNull 에서 실패). 드라이버 동작으로 본다.
- **이진(BLOB·RAW) 배열 바인드 안 됨**: 형식 배열도 NRE / InvalidCast.
NULL 섞인 배열(#42, 675e2a80 `array_bind.null_shape.*`, 3행 중 1행 NULL):

| 열 | 모양 | 결과 |
|---|---|---|
| NUMBER | `decimal?[]` / `long?[]` (Decimal·Int64 종류) | **NullReferenceException** |
| NUMBER | `string?[]` + Decimal 종류 | 됨, `12345678901234567890.123456789`·`-0.000001` 정확(읽기 `-.000001`) |
| NUMBER | `string?[]` + `TO_NUMBER(:V)` | 됨 |
| DATE | `DateTime?[]` | **NRE** |
| DATE | `string?[]` + `TO_DATE(:V, 'YYYY-MM-DD HH24:MI:SS')` | 됨 |
| TIMESTAMP | `DateTime[]`(NULL 없음, TimeStamp 종류) | 됨, 단 **밀리초로 잘림**(.1234567 → .123) |
| TIMESTAMP | `DateTime?[]` | **NRE** |
| TIMESTAMP | `string?[]` + `TO_TIMESTAMP(:V, '… HH24:MI:SS.FF7')` | 됨, .123456(열 정밀도 6) |
| RAW(2000) | 16진 `string?[]` + `HEXTORAW(:V)`(100·2000바이트) | 됨(되읽기 `RAWTOHEX(UTL_RAW.SUBSTR(…))` 는 ODP-15115 — 측정 쪽 문제, 길이로 다시 봄) |

- 결론: **nullable 형식 배열은 전부 NRE**. NULL 이 들어갈 수 있는 열은 글자 배열(`string?[]`) + 변환 함수(또는 Decimal 종류)가 기본.
- 결론: **nullable 형식 배열은 #53 에서도 전부 NRE**(decimal?·long?·DateTime?(DATE·TIMESTAMP)).

한 행씩 스칼라 바인드(#53 `single_bind.*`, ArrayBindCount 없이):

| 열 | 값 | 결과 |
|---|---|---|
| BLOB | `byte[]` 3000·40000바이트 | 됨, 길이·앞 4바이트 같음 |
| BLOB | NULL(DBNull) | 됨, NULL |
| BLOB | 빈 `byte[0]` | 됨, **NULL 로 들어감**(길이 0 BLOB 아님) |
| RAW(2000) | `byte[]` 2000바이트 | 됨 |
| RAW(2000) | NULL | 됨 |
| BLOB | 같은 준비문(`:ID, :BL`)을 값만 바꿔 3번(3000바이트·NULL·10바이트) | 3번 모두 1행, 되읽기 3000·NULL·10 |

묶음 크기(#36, 형식 배열): NUMBER+VARCHAR2 1·100·1000·10000행 됨(10000행 40 ms), NUMBER+CLOB 5000자 10·100행 됨(100행 47 ms), NUMBER+BLOB 3000바이트 10·100행 = NRE.

| 경우 | 결과 |
|---|---|
| ArrayBindCount 3 > 배열 길이 2 | **드라이버 IndexOutOfRangeException**, 행 0(#53) |
| ArrayBindCount 2 < 배열 길이 3 | 됨, 앞 2행만 들어감(반환 2) — 남는 원소는 조용히 버림(#53) |
| 형식 배열 DML 뒤 커밋 전 다른 세션 | 안 보임(0), 커밋 뒤 3 — 배열 DML 은 트랜잭션 안(#53) |

**묶음 안 제약 위반 = 예외 없음** (형식 배열, 3~4행 묶음):

| 경우 | ExecuteNonQuery 반환 | 남은 행 |
|---|---|---|
| PK 중복이 가운데 | 1 | 위반 앞 1행 |
| PK 중복이 맨 뒤 | 2 | 앞 2행 |
| PK 중복이 맨 앞 | 0 | 없음 |
| 값 너무 큼(가운데) | 1 | 앞 1행 |
| 묶음 안끼리 PK 중복 | 2 | 앞 2행 |

- 위반 행부터 뒤는 들어가지 않는다. 오류도 `Errors` 도 없다 → **영향 행 수가 유일한 신호**.
- 트랜잭션은 열린 채(다른 세션엔 안 보임), 다음 문장 정상, ROLLBACK 이 앞 행까지 되돌림.
- Oracle(ODP) 은 OracleException + 행별 오류(ArrayBindErrors)를 주므로 **Tibero 실패 판정은 Oracle 과 다르다**(3.10 TB1-①).

### 3.2 세이브포인트·트랜잭션

- `SAVEPOINT x`·`ROLLBACK TO x`·`ROLLBACK TO SAVEPOINT x`·같은 이름 다시 쓰기·`ORANDA_COPY_BATCH` 이름 됨.
- 중첩: A, B 만든 뒤 A 로 되돌리면 B 는 사라짐(-21008, Oracle 같음). COMMIT 뒤 세이브포인트 없음(-21008).
- 실패 묶음 → ROLLBACK TO → 한 행씩 다시: 성공 행 들어감, 중복 행만 -10007(UNIQUE), 끝난 뒤 정리 ROLLBACK TO 됨.
- 최상위 `SET TRANSACTION READ ONLY` = -7001(문법), `EXECUTE IMMEDIATE` 안도 -7001. PSM 블록 안에서는 됨(E1 이 쓰는 경로).
- READ ONLY 트랜잭션 안에서도 INSERT·DDL·SELECT FOR UPDATE 가 **된다**(쓰기 안 막음). 스냅숏 일관성은 됨(다른 세션 커밋이 안 보임). → 복사 원본 읽기 일관성 용도로만 쓰고, 읽기 전용 보호는 앱 정책 관문(driver-exec 2장, 결정76).

### 3.3 암시적 커밋 (앞서 넣은 행이 다른 세션에 보이는지 / ROLLBACK 뒤 남는지)

| 문장 | 커밋함 |
|---|---|
| SELECT, SAVEPOINT, ALTER SESSION(NLS), LOCK TABLE | 아니오 |
| ALTER TABLE ADD COLUMN(성공·실패 둘 다), CREATE INDEX(성공·실패 둘 다), COMMENT ON TABLE, CREATE/ALTER/DROP SEQUENCE, ALTER TABLE DISABLE ALL TRIGGERS, DROP INDEX | 예 |

Oracle 과 같음. **실패한 DDL 도 커밋한다** → 복사 도중 DDL(트리거·제약 끄기, 시퀀스 조정)은 묶음 트랜잭션 밖, 앞에서 한다.

### 3.4 트리거

- `ALTER TABLE … DISABLE/ENABLE ALL TRIGGERS`, `ALTER TRIGGER [따옴표 이름] DISABLE/ENABLE` 됨, ALL_TRIGGERS.STATUS 반영, 꺼진 동안 트리거 안 돎.
- `DBMS_METADATA.GET_DDL('TRIGGER', …)` = **-14001 Not yet implemented**(TABLE 은 됨) → 트리거 DDL 은 ALL_TRIGGERS + ALL_SOURCE 로 조립(TB4).

### 3.5 외래 키

- 켜진 FK 고아 INSERT = -10008. `DISABLE CONSTRAINT` 뒤 고아 들어감.
- `ENABLE VALIDATE` + 고아 = -7161, `ENABLE NOVALIDATE` 됨(뒤 새 고아는 -10008, 정상), 고아 지운 뒤 VALIDATE 됨.
- ON DELETE CASCADE·SET NULL·DEFERRABLE INITIALLY DEFERRED/IMMEDIATE 문법 됨. 그러나 **ALL_CONSTRAINTS 에 DEFERRABLE·DEFERRED 열 없음**(-8026) → 지연 여부를 카탈로그로 못 읽음.
- 참조되는 부모 DROP·TRUNCATE = -7156.
- ALL_CONS_COLUMNS 조인(자식·부모 열) 됨.

### 3.6 시퀀스·IDENTITY

- NEXTVAL 묶음: `CONNECT BY LEVEL <= :N`(decimal·int 바인드)·LEVEL 하위 질의·리터럴 모두 됨, 따옴표 소유자.시퀀스 됨.
- 1000행 NEXTVAL 질의에서 1행만 읽어도 1000 소비(35 → 1037). NEXTVAL 은 트랜잭션 안 엶, ROLLBACK 으로 안 돌아감.
- ALL_SEQUENCES.LAST_NUMBER 는 캐시 끝(CACHE 20). ALTER SEQUENCE INCREMENT BY 100·-값·1 복원 됨.
- **IDENTITY 없음**: `GENERATED … AS IDENTITY` 4가지 = -7001, ALL_TAB_IDENTITY_COLS = -8103(순환 동의어), ALL_TAB_COLUMNS.IDENTITY_COLUMN 없음. 대체 `DEFAULT seq.NEXTVAL` 됨(DATA_DEFAULT = `ORT_TBM_IDS.NEXTVAL`).

### 3.7 휴지통

- USER_RECYCLEBIN·RECYCLEBIN 있음, 열 = OBJECT_NAME, ORIGINAL_NAME, TYPE, TS_NAME, CREATETIME, DROPTIME, DROPTSN, BASE_OBJECT, SPACE(**OPERATION 없음**).
- DROP(PURGE 없이) → 휴지통에 BIN 이름(`<USER>_TBL…`, 따옴표 없는 `BIN$` 모양 아님) 1행. 그 이름 SELECT = -8052.
- FLASHBACK TABLE … TO BEFORE DROP [RENAME TO] 됨. PURGE TABLE 원래 이름·BIN 이름, DROP … PURGE, PURGE RECYCLEBIN 됨.
- `ALTER SESSION SET RECYCLEBIN = ON` = -7001, `USE_RECYCLEBIN` 됨. V$PARAMETER 없음(-8033) → 휴지통 켜짐 여부 파라미터로 확인 못 함.

### 3.8 MERGE·바인드

- MERGE USING DUAL: 리터럴·스칼라 바인드·NULL·형식 배열(3행) 됨, INSERT 만 있는 MERGE 두 번째 = 0행. object[] 배열 = InvalidCast(3.1 과 같음).
- 바인드 이름: 남는 매개변수 OK, 모자라면 -12029, 같은 이름 두 번 = 매개변수 1개로 OK.

### 3.9 복사 카탈로그 (copy.* — Oracle SQL 그대로 Tibero 에 보냈을 때)

| copy.* id | Tibero | TB3a 처리 |
|---|---|---|
| session_user·session_roles·session_privs | 됨 | 그대로 |
| table_privs | -8026: ALL_TAB_PRIVS 에 TABLE_SCHEMA·TYPE 없음 | `OWNER` 로 |
| enabled_triggers·referencing_fks | 됨 | 그대로 |
| identity_cols | -8103 | 쿼리 없이 「IDENTITY 없음」(빈 결과) |
| sequence_info·sequence_names | 됨 | 그대로 |
| parameter_value | -8033: V$PARAMETER 없음 | V$PARAMETERS 후보(미실측) — UNDO_RETENTION·MAX_STRING_SIZE 이름도 미실측. 못 읽으면 힌트 생략 |
| tablespace·constraint_owner·index_owner | 됨 | 그대로 |
| col_priv_count·other_grantor_count | -8026: TABLE_SCHEMA 없음 | `OWNER` 로 |
| mview_log_count·vpd_policy_count | 됨 | 그대로 |
| fga_policy_count | -8033: ALL_AUDIT_POLICIES 없음 | NotRestoredProbes 에서 뺌 |
| object_exists·target_is_table | 됨 | 그대로 |
| trigger_count_all | -8026: BASE_OBJECT_TYPE 없음 | 조건 뺌(ALL_TRIGGERS 는 표 트리거만 TABLE_NAME 채움 — TB3a 확인) |

다시 만들기(RecreateStep, TB4) 쪽: triggers_of_table(BASE_OBJECT_TYPE)·referencing_fk_columns/source_foreign_keys(DEFERRABLE·DEFERRED)·recycle_bin_of_table(OPERATION) = -8026, grants_by_owner_twice_bound(TABLE_SCHEMA) = -8026, GET_DDL(TRIGGER) = -14001. GET_DDL(TABLE)·foreign_key_columns·table_exists 됨.

### 3.10 TB1·TB3·TB4 규칙 (이 실측으로 정함)

1. **TB1 실패 판정 = 영향 행 수**: 묶음 ExecuteNonQuery 반환 ≠ 묶음 행 수 → 실패로 보고, 반환값 k 로 「k 번째 행(0부터)이 첫 위반」을 알 수 있음. 처리 = 세이브포인트 ROLLBACK TO → 한 행씩 다시(T5 결정). 예외가 나도(형식·드라이버 오류) 같은 경로.
2. **TB1 값 배열 = 형식 배열만**: `object[]` 는 어떤 종류도 금지(#36: 드라이버가 원소를 string 으로 캐스트). 열마다 형식 배열을 만든다(nullable 형식 배열은 NRE, #42) — VARCHAR2·CHAR·CLOB = `string?[]`, NUMBER = `string?[]` + Decimal 종류(invariant 고정 소수점 글자, NULL·30자리 소수 됨), DATE = `string?[]` + `TO_DATE(:V, 'YYYY-MM-DD HH24:MI:SS')`, TIMESTAMP = `string?[]` + `TO_TIMESTAMP(:V, 'YYYY-MM-DD HH24:MI:SS.FF9')`(#42 는 FF7 로 실측, TB1 IT 에서 FF9 9자리 확인 — 3.11. 형식 DateTime[] 은 밀리초로 잘려 안 씀), RAW = 16진 `string?[]` + `HEXTORAW(:V)`. NULL 이 없는 열도 같은 경로(경로 하나). ArrayBindCount = 모든 배열 길이(크면 드라이버 IndexOutOfRange, 작으면 뒤 행을 조용히 버림 — #53) → TB1 은 묶음마다 길이를 단정한다. INSERT/MERGE 글의 값 자리에 변환 함수를 감싸는 것은 Tibero 실행 방언(TB3b), 값 → 글자는 TiberoBulkValueCodec 한 곳.
2-1. **BLOB·LONG RAW 열이 있는 표 = 배열 바인드 안 함**: 같은 트랜잭션·같은 준비문으로 한 행씩(스칼라 byte[] 바인드, 보충 측정 `single_bind.*` 로 확인), 묶음 단위 세이브포인트·실패 처리는 같게. RAW 는 16진 + `HEXTORAW(:V)` 배열(#42 됨)이라 배열 경로에 남고, 한 행씩은 BLOB·LONG RAW 열이 있는 표만(2000바이트 넘는 값을 HEXTORAW 로 못 넘김). 스칼라 byte[] 바인드 됨(#53). 준비문 재사용은 #53 에서 됐으나 TB1 IT 에서 1 MB 행 다음에 TBR-590727 이 나 행마다 새 명령으로 보낸다(3.11). 빈 byte[] 는 NULL 로 들어감 → 원본 길이 0 BLOB 은 NULL 이 된다(Oracle 결과와 다를 수 있어 TI1b 에서 Oracle 과 비교, 차이면 EMPTY_BLOB() 경로). 속도 목표 없음(Q3), 행/초만 기록.
3. **DDL 은 묶음 밖**: 트리거·제약 끄기, 시퀀스 조정은 묶음 트랜잭션 시작 전에(실패한 DDL 도 커밋).
4. **트리거 DDL 조립**: GET_DDL(TRIGGER) 대신 ALL_TRIGGERS + ALL_SOURCE(TB4, Q5).
5. **FK 다시 켜기**: VALIDATE 실패(-7161) 시 NOVALIDATE 로 켜고 고아 수를 알림(Oracle 경로와 같음). 지연 FK 여부는 카탈로그로 못 읽음 → TB3a 는 「지연 여부 모름」으로 두고 차단하지 않음(또는 사유 표시 — TB3a 결정).
6. **IDENTITY 규칙 없음**: Tibero 방언은 IDENTITY 점검·GENERATED ALWAYS 차단을 하지 않음(쿼리도 안 보냄).
7. **휴지통**: Recreate 안전망은 BIN 이름 SELECT 가 안 되므로(-8052) 「휴지통에서 되읽기」에 기대지 않음. FLASHBACK TO BEFORE DROP 은 됨 — Recreate 차단 해제 여부는 T5 결정 Q5(실측 없으면 차단) 따라 TB4 에서.
8. **시퀀스 다음 값 = CONNECT BY 묶음**: Oracle `CopyStatements.NextValues` 모양 그대로 써도 됨. 소비가 읽은 행 수보다 클 수 있음(Oracle 같음).

### 3.11 TB1 실측 (`TiberoBulkExecutionServiceIt`, ORT_TB1_* 표, win144 #74·#78·#81·#82)

`TiberoBulkExecutionService`(ExecuteArrayAsync·StreamRowsAsync)와 `TiberoBulkValueCodec` 을 실서버로 확인한 결과. 3.10 의 규칙 중 바뀐 것은 ②(TIMESTAMP 서식)와 ②-1(한 행씩 = 행마다 새 명령), 새로 생긴 것은 ③-CLOB.

| 항목 | 결과 | TB1 처리 |
|---|---|---|
| 왕복(300행, 배치 50): NUMBER(38,10)·NUMBER·VARCHAR2·CHAR(5)·NVARCHAR2(CP949 밖 글자)·CLOB(100000자 2행)·DATE(0001-01-01·9999-12-31 23:59:59)·TIMESTAMP(9)·RAW(2000B) | 100000자 CLOB 행이 **다른 열과 함께** 배열에 들어가면 서버가 연결을 끊음(TBR-90405 End Of Stream, 재현). 같은 행들을 열 하나씩 빼면 CLOB 을 뺀 경우만 됨, ID + 열 하나는 모두 됨(ID + CLOB 100000자 포함), 큰 CLOB 이 없는 25행은 됨. 생성 값으로 CLOB 을 4000자로 자른 같은 혼합 50행은 됨. 원본 열거가 열려 있는지와는 무관 | 묶음 안 CLOB·NCLOB 값이 4000자(`ArrayClobMaxChars`)를 넘으면 그 묶음만 한 행씩(스칼라 100000자 됨) |
| 형식 하나씩 배열 50행 | 9형식 모두 됨. CLOB 열만 있는 배열은 1000~100000자(모든 행·일부 행·NULL 섞음) 모두 됨 | — |
| 묶음 가운데 PK 중복(50행) | 예외 없음, 반환 24 → ORANDA-ARRAY-PARTIAL「앞 24행 … 25번째 행에서」 → ROLLBACK TO SAVEPOINT → 한 행씩: 중복 행만 TBR-10007 ConstraintViolation | 3.10 ① 그대로 |
| BLOB 3행(1 MB·중복·NULL) 한 행씩, 명령 하나 재사용 | 1 MB 행 다음 행에서 TBR-590727 「Communication protocol error. Invalid message type」. 행마다 새 명령이면 정상(중복 행 10007). 재사용·새 명령 대조 측정(3000B·64KB·1MB)은 #81 에서 모두 정상 — 서비스 경로의 재사용만 재현 | 한 행씩은 **행마다 새 명령**(3.10 ②-1 의 「준비문 재사용」을 쓰지 않음) |
| 빈 byte[] BLOB | NULL 로 저장(3.10 ②-1 과 같음) | TI1b 에서 Oracle 과 비교 |
| MERGE | UPDATE 있는 모양 배열 k == n(50). 키만 있는 모양 일치 행 → k < n → 한 행씩 50 모두 성공 | 배열 성공 = k == n 만(k > n 도 한 행씩) |
| CHAR(5) 표 키 바인드 | `WHERE K = :P` Varchar2 종류 길이 2·3 → 0건, Char 종류 → 1건. 키만 MERGE(배열, Varchar2) 는 공백 채움·안 채움 둘 다 일치로 봄 | CHAR 키 되읽기 = Char 종류(BindDataType.FixedChar) |
| NUMBER 끝값(38자리·음수·소수 38자리·1E125·1E-125·-0.000001·0) | 스칼라·배열(7행·2행) 모두 같은 글자로 되읽힘. 39자리 = 38자리로 반올림 저장 | Decimal 종류 + 고정 소수점 글자 |
| 키 되읽기 | 38자리 NUMBER·TIMESTAMP(9)·DATE 키 바인드로 정확히 1행 | 3.5 그대로 |
| 정수·실수 계열 열(INTEGER·SMALLINT·NUMERIC·FLOAT·DOUBLE PRECISION·REAL) | 원시 바이트가 NUMBER 형식 → TiberoBulkNumber | — |
| 취소 | 서버 계산 중·행 읽는 중 모두 OperationCanceledException, 잠금 풀림, 다음 SELECT 됨 | — |
| 읽기 전용 프로필 | `BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;` 성공, INSERT 거부(ReadOnlyProfile) | 방언과 같은 글만 통과 |
| TIMESTAMP(9) 배열 | `TO_TIMESTAMP(:C, '… .FF9')` 9자리 됨(FF7 서식도 9자리 글자를 받음) | **FF9**(3.10 ② 의 FF7 대신) |
| 꺼진 형식 후보(측정만) | BINARY_FLOAT/DOUBLE `TO_BINARY_DOUBLE` 쓰기 됨, 'Infinity' -5074, **읽기 GetValue 가 음수를 틀림**(-2.25 → -1.8749999). TZ `TO_TIMESTAMP_TZ` 배열·스칼라·되읽기 됨. LTZ 되읽기 기준 시간대 불명. INTERVAL YM/DS `TO_YMINTERVAL`/`TO_DSINTERVAL` 음수·나노초까지 됨. LONG 쓰기 됨·읽기 GetString 빈 글자. LONG RAW 4B 쓰기 → 72B 로 읽힘 | **INTERVAL YM/DS 만 켬**(TB1 9). BINARY_*·LONG·LONG RAW = 읽기 틀림이라 끔(결과 격자 읽기 고침은 R1x), TZ = 지역 이름·음수 분 오프셋 미측정, LTZ = 의미 불명으로 끔 |

## 4. T5 마감 상태 (T5-9, 2026-11-10)

- **켜진 것**: 내보내기·가져오기·정의서(TI1a 키 등록). 결과 격자·복사 읽기의 BINARY_FLOAT/DOUBLE·LONG·LONG RAW 읽기 고침(R1x, values.md 3장).
- **있지만 꺼진 것 = Tibero 데이터 복사**: 대량 실행(`TiberoBulkExecutionService`)·값 코덱(`TiberoBulkValueCodec`)·방언(`TiberoCopyDialect`)·사전 점검 방언·다시 만들기 단계·실행기/사전 점검 하위 형식(`TiberoCopyRunner`·`TiberoCopyPrecheckService`)이 모두 있으나 어느 것도 `[OrandaService]` 로 등록하지 않았다. 이유: 같은 계정 안 복사는 Core 가 막고(같은 DB·같은 소유자, `TiberoCopySameAccountIt` 로 확인), 다른 계정으로의 실제 복사를 잴 두 번째 Tibero 계정이 시험 서버에 없다(결정65). 화면은 「Tibero 복사는 아직 꺼져 있음」으로 남는다(TU1).
- **켬(xdb-copy XC0, 2026-11-10, 사내용)**: 위 꺼짐을 사용자 지시로 풀었다 — 대량 실행·실행기·사전 점검을 `Dbms.Tibero` 키로 등록해 Tibero→Tibero 복사 메뉴가 켜진다. **두 번째 계정 IT(아래 1번)는 아직 못 쟀다.** 같은 계정 차단(Core)·다시 만들기 차단(`RecreateBlockedReason`)은 그대로다.
- **켜기 전에 할 일** (두 번째 계정 `ORANDA_IT_TIBERO_USER2`·`PASSWORD2` 가 생기면):
  1. 실제 복사 IT(실행기 경로: 다중 배치·배치 실패 뒤 한 행씩·실패 행 재시도·4000자 넘는 CLOB·1 MB BLOB, Q7·Q8) → 녹색이면 대량 실행·실행기·사전 점검을 `Dbms.Tibero` 키로 등록.
  2. ~~CopyRunner 안내 글의 Oracle 번호 고정 → 방언 메시지로.~~ **서버 없이 끝냄(T5 켜기 전 1)**: `ICopyDialect` 에 기본 구현(= 지금 Oracle 글) 멤버를 더했고 Tibero 방언이 채운다. Oracle 글은 한 글자도 안 바뀐다(`OracleCopyDialectTests` 가 글자까지 못 박음). 한국어 글을 코드에 두는 복사 쪽 기존 방식 그대로(복사 Core 에 리소스 파일 방식은 없다).
     | 자리 | Oracle(기본) | Tibero |
     |---|---|---|
     | DROP 이 안 보이는 FK 로 거부(`CopyRunner.DropAsync`) | `(ORA-02449)` | `(TBR-7156)` — 3.5 실측 |
     | 되살린 트리거 컴파일 오류 INVALID(`TriggerCompileErrorName`) | `컴파일 오류(ORA-24344)` | **미실측 — 번호 없는 「컴파일 오류」**. 컴파일 오류 트리거 CREATE 는 오류 없이 INVALID+ENABLED 로 남으므로(TB4 #79) 오류로는 알 수 없고, 같은 문구를 **만든 뒤 상태 조회로 알아챈 경우**(아래 5-1)에 쓴다 |
     | 원본 읽기 이전 시점 상실(`SnapshotTooOldErrorName`) | `(ORA-01555)` | **미실측 — 번호 없음**(`TiberoErrorClassifier` 도 SnapshotTooOld 를 분류하지 않음) |
     | 휴지통에서 되돌리기 안내(`FlashbackRestoreNote`) | `(트리거는 BIN$ 이름으로, 인덱스·권한과 함께 돌아옵니다. 이 표를 참조하던 FK 는 돌아오지 않습니다)` | `BIN$` 말하지 않음(휴지통 이름은 `<계정>_…`, 3.7). **미실측 — 트리거·인덱스·권한·참조 FK 가 함께 돌아오는지** 는 「확인되지 않았습니다」로 적음 |
     | 다시 만들기 확인 글 「원본과 다르게 만들어지는 것」(`ICopyPrecheckDialect.RecreateDifferenceNote`) | `DEFAULT ON NULL·IDENTITY 옵션·DEFERRABLE PK/UK·COLLATE(·도메인) 은 빠지고` | `DEFERRABLE 제약(PK/UK 와, 카탈로그가 지연 여부를 안 알려 줘서 FK 도) 은 즉시 검사로 바뀌고` — `ALL_CONSTRAINTS` 에 DEFERRABLE 열 없음(handover). DEFAULT ON NULL·COLLATE·도메인은 Tibero 에서 확인하지 않아 말하지 않음 |
     그 밖의 `CopyRunner` 안의 `ORA-04098`·`ORA-02298` 은 주석뿐이고 판정은 이미 방언(`IsParentKeysNotFound` = TBR-7161)이 한다. `CopyCatalogQueries` 문서의 ORA-01036 은 문서를 번호 비고정으로 고침(판정 코드 없음).
  3. ~~Tibero 드라이버의 `DbException` 이 아닌 클라이언트 오류를 `CopyErrors` 가 놓치는지 확인·분류.~~ **놓치고 있었고 고침(T5 켜기 전 2)**: 접속 끊김 같은 드라이버 쪽 오류(`TbOdpException`)는 `DbException` 이 아니라서 `CopyErrors.Server` 가 서버 오류로 보지 않아 복사 읽기·쓰기·정리 밖으로 새어 나갔다(그 오류의 `Disconnected` 분류로 하는 COMMIT 결과 불명 판정도 못 탐). `IDbErrorClassifier.IsOwnDriverException`(기본 false = Oracle 그대로, 그 예외 **자체**만 보고 안쪽 예외는 안 봄)을 Tibero 분류기가 어댑터로 구현해 `CopyErrors` 가 `DbException || 분류기가 알아본 예외` 로 본다. 감싼 예외를 삼키지 않는 범위 규칙은 그대로. 시험: `TiberoCopyErrorsTests`·`CopyErrorsTests`(Core).
  4. ~~대량 실행 중 취소: 우리 취소 뒤 TBR-12040 이 OperationCanceledException 으로 이어지는 경로 확인.~~ **코드 경로 확인함, 고칠 것 없음(서버 실측은 못 함)**: 읽기(`TiberoBulkReader.ReadAsync`)는 실행 단계·묶음 읽기 단계 둘 다 `IsCancellation(ex, ct)`(토큰 취소 + 취소된 토큰 뒤의 TBR-12040)이면 `OperationCanceledException`(안쪽에 12040)으로 바꿔 올린다. 쓰기(`ExecuteArrayAsync`)는 배열·스칼라·BLOB 한 행씩 모두 같은 판정으로 `BulkResult.Cancelled` 를 돌려주고 Core 의 `RunState.Accept`·`ExecuteLoggedAsync` 가 `Cancelled` + 취소된 토큰이면 `OperationCanceledException` 으로 올린다. 우리가 취소하지 않은 12040(다른 세션의 KILL 등)은 실패로 남는다. 가짜 12040 주입 시험: 배열·한 행씩·감싼 예외(`TiberoBulkExecutionServiceTests`), 읽기(`TiberoBulkStreamTests`). **미실측**: 실서버에서 대량 읽기/쓰기 도중 우리 취소가 12040 으로 돌아오는 시점(서버가 취소를 늦게 받아 문장이 정상 완료하면 실행 뒤 `ThrowIfCancellationRequested` 가 취소로 처리한다). 두 번째 계정 복사 IT 에서 중지 시험으로 확인한다.
  5. 다시 만들기(Recreate) 차단 해제는 실행기 복사 IT 에서 FK `ADD … ENABLE NOVALIDATE`·휴지통 되살리기를 잰 뒤(TB4). **차단은 그대로 둔다(`RecreateBlockedReason`).** 차단을 풀기 전 위험 4건(tb-session 「다시 만들기 남은 위험」) 가운데 서버 없이(또는 계정 하나로) 고칠 수 있는 것을 고쳤다(T5 다시 만들기 위험 정리):
     1. ~~컴파일 오류 트리거가 오류 없이 INVALID+ENABLED 로 남음~~ **고침(서버 IT 한 개 추가, 아직 서버로는 못 돌림)**: `ICopyDialect.TriggerStatusSql`(기본 null = Oracle 그대로, 조회하지 않고 ORA-24344 오류로 끈다)을 Tibero 방언이 `copy.trigger_status`(`SELECT STATUS FROM ALL_OBJECTS WHERE OWNER = :OWN AND OBJECT_NAME = :NAME AND OBJECT_TYPE = 'TRIGGER'`, Tibero 카탈로그에만 있는 id)로 채운다. `CopyRunner.RestoreTriggersAsync` 가 받아 둔 트리거를 되살린 뒤 방언 글이 있으면 트리거마다 상태를 읽어 INVALID 이면 `ALTER TRIGGER … DISABLE` 하고 05a2aae5 의 같은 문구(「컴파일 오류로 INVALID 입니다. 꺼 두었으니 고친 뒤 켜세요」, Tibero 는 번호 없이)를 알림·표 메시지에 남기고 결과를 실패로 둔다. 원래 꺼져 있던 트리거(생성 문장 뒤에 같은 트리거의 DISABLE 이 따름)는 어차피 다시 꺼지므로 조회하지 않는다. **상태를 읽지 못하면 조용히 넘기지 않고** 「컴파일 상태를 확인하지 못했습니다」로 알리며 결과를 실패로 둔다(막힌 DML 을 놓칠 수 있다). 시험: 가짜 상태 조회·가짜 실행 8건(`TiberoCopyRunnerTriggerTests`: INVALID → 생성 + DISABLE, VALID → 생성만, 대소문자·공백, 원래 꺼진 트리거는 조회 없음, 둘 중 INVALID 만 끔, 상태 못 읽음, DISABLE 실패, 생성 실패), Oracle 기본값(`OracleCopyDialectTests`). 서버 IT: `TiberoCopyTriggerStatusIt`(`ORANDA_IT_TIBERO_WRITE=1`, ORT_ 표 + 없는 열 트리거 + 정상 트리거 + 원래 꺼진 트리거 → 실행기 경로로 INVALID 감지·DISABLE, 정상 트리거 ENABLED·VALID, 이 표의 INSERT 가 막히지 않음). **미실측**: 이 IT 를 서버로 돌린 결과(특히 PSM 끝의 `;` 를 실행 서비스가 그대로 보내도 되는지, DISABLE 뒤 `user_objects.status` 가 INVALID 로 남는지는 출력만 하고 단언하지 않음).
     2. ~~서버 이름 제약 `<소유자>_CON<숫자>` 를 Core 가 명시 이름으로 다시 만듦~~ **고침**: `CreateTableDdl.Build`·`NamedObjects` 가 서버 이름 판정(`Func<string, bool>?`, 기본 null = Oracle `SYS_`/`BIN$`)을 받고, `ICopyDialect.IsServerName(name, owner)`·`ICopyPrecheckDialect.IsServerName(name, owner)`(기본 = Oracle 판정)가 실행기(`Build` 호출)와 사전 점검(`NamedObjects`·작업 안 FK 이름 겹침)에 같은 판정을 넘긴다. Tibero 판은 `TiberoRecreateStep.IsServerName(name, 원본 표 소유자)`(`<소유자>_CON<숫자>`·`_SYS_…`·Oracle 모양)에 위임한다. 서버가 지은 PK·UK·CHECK·FK·인덱스 이름은 이제 명시하지 않아 대상 서버가 새로 짓는다(대상 소유자 안의 같은 이름 충돌이 없다). 소유자가 다른 `<소유자>_CON<숫자>` 는 사용자 이름으로 본다. 시험: `CreateTableDdlTests`(판정 있음·없음), `TiberoCopyDialectTests`(경계), `OracleCopyDialectTests`(Oracle 기본).
     3. 부모·자식 함께 다시 만들 때 휴지통 자식 FK(TBR-7156) 오안내 — 05a2aae5 는 번호만 바꿨고 문구(「대상 계정에 보이지 않는 FK(다른 스키마)」)는 그대로여서 **빈 곳이었고 채움**: `ICopyDialect.RecycledChildForeignKeyNote(children)`(기본 null = Oracle 문구 그대로)을 Tibero 가 채운다. 부모 DROP 이 「참조하는 FK」 로 거부됐는데 이 실행에서 먼저 지운 자식 표(참조 FK 를 받아 둔 표 가운데 `Dropped`)가 있으면 「이 실행에서 먼저 지운 자식 표 …이(가) 휴지통에 남아 그 FK 가 이 표를 참조하고 있을 수 있습니다(휴지통의 그 표를 PURGE 해야 할 수 있습니다)」 를 붙이고 「보이지 않는 FK」 는 가능성 중 하나로 낮춘다. **미실측**: 지운 자식 표가 휴지통에서 FK 를 쥐는지 자체는 이 조합으로 재지 못했다(그래서 「있을 수 있다」). 문구 확인은 방언 단위 시험까지만 했고 `DropAsync` 분기는 실행기 시험이 없다(복사 IT 몫).
     4. DEFERRABLE FK 가 즉시 검사로 돌아옴(안내 없음) — **이미 됨(05a2aae5)**: `TiberoCopyPrecheckDialect.RecreateDifferenceNote` 가 「DEFERRABLE 제약(PK/UK 와, 카탈로그가 지연 여부를 안 알려 줘서 FK 도)은 즉시 검사로 바뀌고」 를 확인 글에 낸다(`TiberoCopyPrecheckDialectTests`). 이번에 더한 것 없음.
     차단을 풀기 전에 남은 것은 (a) 위 IT 와 `TiberoRecreateStepIt` 를 실서버로 돌린 결과, (b) 다른 표에서 들어오는 FK 가 있는 표의 열 순서 다시 만들기, (c) 실행기 복사 IT(두 번째 계정, 1번 항목)에서 FK `ENABLE NOVALIDATE`·휴지통 되살리기를 재는 것이다.
  6. 쓰기 형식 늘리기 후보(3.11 측정): TIMESTAMP WITH TIME ZONE(지역 이름·음수 분 오프셋 측정 뒤), BINARY_FLOAT/DOUBLE(쓰기 됨, 'Infinity' 글자 대안 필요).
- **후속 — 「대상에 표가 없으면 만들기」(`TableRule.CreateIfMissing`, 회차 copy-create)**: 같은 Core 경로(`CopyPrecheckService`·`CopyRunner.CreateMissingTablesAsync`)를 타지만 Tibero 복사가 꺼져 있어 화면에 나오지 않고 `RecreateBlockedReason` 차단도 그대로라 사전 점검이 `CreateBlocked` 로 막는다. Tibero 실측·IT 는 이번 범위 밖이다. 복사를 켤 때 두 번째 계정 IT 에 「없는 표 만들기 → 적재 → 정리」 줄을 더해 잴 것: 서버 이름 제약 `<소유자>_CON<숫자>` 가 새로 지어지는지, DEFERRABLE 손실, 트리거를 만들지 않는지, 적재 실패 뒤 `DROP TABLE … PURGE` 가 Tibero 에서 휴지통 없이 지워지는지(지금 `ICopyDialect.DropCreatedTable` 은 Oracle·Tibero 공통 기본 글이다 — 3.7 휴지통 실측 뒤 필요하면 Tibero 방언이 덮는다), `DDL` 묵시 커밋(3.3) 때문에 지우기 전에 대상 트랜잭션을 끝내는 순서.
- **다른 backlog(조정 등록)**: 스키마 표를 못 읽을 때 결과 격자가 모든 열을 GetString 으로 읽음(실서버에서 본 적 없음), Oracle `CopyCrossOwnerTests` 가 같은 DB 가드 앞에서 멈출 수 있음(미확인).
