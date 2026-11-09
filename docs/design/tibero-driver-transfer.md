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
| 가져오기 오류 문구 | `행 2: TBR-10007: ODP-10007:UNIQUE constraint violation (…)` — 코드가 두 번 붙는다(`TBR-` 접두 + 드라이버 메시지의 `ODP-`). Oracle 판은 드라이버 메시지가 이미 같은 코드로 시작하면 또 붙이지 않는데 Tibero 는 접두가 다르다(개선 후보, 동작에는 영향 없음) |
| 자동 커밋 | 아니다 — INSERT 뒤 `ROLLBACK` 이 되돌린다(id=300 건수 0) |
| 배치 크기 | 한 문장 `INSERT … UNION ALL`: 50행 13 ms(문장 1,958자) / 500행 69 ms(20,408자) / 2000행 528 ms(84,908자) — 모두 성공 |
| 가져오기 IT(`TiberoTransferIt`) | CSV 5행을 배치 2 로 넣어 값(따옴표·쉼표·한글·날짜·`TIMESTAMP(6)`·4500자 CLOB·RAW·NUMBER(12,2))이 맞고, 같은 파일을 다시 넣으면 5행 모두 중복 키 경고로 남고 표는 그대로, 값 오류 행은 보내기 전에 경고로 걸러진다. 2·0·0 통과 |

배치 크기 기본값 500(`ImportOptions.BatchSize`)은 Tibero 에서도 그대로 써도 된다(문장 20 KB, 69 ms).
