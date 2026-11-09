# Tibero 드라이버(TDP.NET) 값 실측 (MR)

실측 환경: `Tibero.DataAccess` 1.1.35, 서버 Tibero 6.2.15(빌드 302172), 읽기 전용 질의(`SELECT … FROM DUAL`, 사전 뷰, 시험 사용자 표 읽기)와 PSM 블록만 썼다. Tibero 7 은 시험 서버가 없어 실측하지 않았다(D7).
접속 정보는 문서에 싣지 않는다. 실측 프로그램은 리포 밖(스크래치)에 있다. 아래 표는 `Oranda.Tibero.Results` 의 단위 시험 고정 값(fixture)으로 그대로 쓴다.

## 1. 열 형식 → `ColumnDataKind`

`TiberoDataReader.GetDataTypeName(i)` 와 `GetSchemaTable()` 의 `ProviderType`(= `TiberoDbType` 정수)으로 판정한다.

| SQL 형식 | `GetDataTypeName` | ProviderType | `GetValue` 형식 | Core 분류 |
|---|---|---|---|---|
| NUMBER, INTEGER, FLOAT, DOUBLE PRECISION | `Decimal` | 107 | `decimal` (**28자리 초과면 OverflowException**) | `Number` |
| NUMBER(19) 등 정수 열 | `Int64` 등 | 113 등 | `long` 등 | `Number` |
| BINARY_FLOAT / BINARY_DOUBLE | `BinaryFloat` / `BinaryDouble` | 133 / 132 | `float` / `double` | `BinaryFloat` |
| VARCHAR2 / CHAR / NVARCHAR2 / NCHAR | `Varchar2` / `Char` / `NVarchar2` / `NChar` | 126 / 104 / 119 / 117 | `string` (CHAR 는 끝 공백 채움) | `Text` |
| 문자 리터럴 `'lit'` | `Char` | 104 | `string` | `Text` |
| 빈 문자열 | — | — | NULL (Oracle 과 같음) | |
| DATE | `Date` | 106 | `DateTime` (Kind=Unspecified, 초 단위) | `Date` |
| TIMESTAMP(n) | `TimeStamp` | 123 | `DateTime` — **소수초가 버려진다** | `Timestamp` |
| TIMESTAMP WITH LOCAL TIME ZONE | `TimeStampLTZ` | 124 | `TiberoTimeStampLTZ` | `Timestamp` |
| TIMESTAMP WITH TIME ZONE | `TimeStampTZ` | 125 | `TiberoTimeStampTZ`(offset 있는 값은 **ArgumentOutOfRangeException**) | `TimestampTz` |
| INTERVAL YEAR TO MONTH | `IntervalYM` | 115 | `long` (총 개월 수) | `IntervalYearMonth` |
| INTERVAL DAY TO SECOND | `IntervalDS` | 114 | `TimeSpan` (**소수초 밀리초까지만**) | `IntervalDaySecond` |
| RAW | `Raw` | 120 | **`sbyte[]`** (바이트 아님) | `Raw` |
| BLOB / CLOB / NCLOB | `Blob` / `Clob` / `NClob` | 102 / 105 / 116 | `byte[]` / `string` / `string` (전체를 읽어 온다) | `Blob` / `Clob` |
| ROWID | `RowID` | 126 (스키마 `IsRowID=True`) | `string` 18글자 | `RowId` |
| BOOLEAN(PSM) | `Boolean` | 134 (`TiberoDbType` 값, 열로 읽은 실측은 없음) | — | `Boolean` |
| REF CURSOR(OUT) | — | 121 | `TiberoRefCursor` | `RefCursor` |

- `GetFieldType`: NUMBER = `Decimal`, DATE·TIMESTAMP = `DateTime`, IntervalYM = `Int64`, IntervalDS = `TimeSpan`, RAW = `Byte[]`(실제 값은 `sbyte[]`), BLOB = `Byte[]`.
- `GetProviderSpecificFieldType`·`GetProviderSpecificValue` 는 모두 비어 있다(쓸 수 없다).
- `GetString` 은 문자 열에서만 쓸 수 있다. 숫자·날짜 열은 빈 문자열을 준다.
- 스키마 표 열: `ColumnName, ColumnOrdinal, ColumnSize, NumericPrecision, NumericScale, IsUnique, IsKey, IsRowID, BaseColumnName, BaseSchemaName, BaseTableName, DataType, ProviderType, AllowDBNull, IsAliased, IsByteSemantic, IsExpression, IsHidden, IsReadOnly, IsLong, UdtTypeName, IsIdentity, IsAutoIncrement, IdentityType`.
  NUMBER 는 `NumericPrecision=38, NumericScale=-128`(미지정), `ColumnSize=22`. VARCHAR2(30) 은 `ColumnSize=30`. CLOB·BLOB 은 `ColumnSize=4000, IsLong=True`.
- `RowSize`·`FetchSize`(리더·명령)는 항상 0 이고 `FetchSize` 에 값을 써도 읽으면 0 이다 → 한 번에 가져오는 행 수를 조절할 수 없다(20000 행 읽기 약 340ms, 값과 무관).

## 2. 원시 바이트 해석(드라이버 버그 우회)

`reader.GetTiberoBinary(i).Value` 는 모든 형식에서 서버가 보낸 원시 바이트를 준다. NUMBER·TIMESTAMP·시간대·INTERVAL 은 드라이버 값 변환이 틀리거나 던지므로 이 바이트를 직접 해석한다(`TiberoWire`). 아래 바이트는 모두 실측이다.

### NUMBER (`[길이][지수][숫자…][F0]`)
- 길이 바이트는 뒤따르는 바이트 수. 0 = `01 80`.
- 양수: 지수 바이트 `0xC0 + e`, 숫자 바이트 `0x80 + d`(d = 100진 자릿수 0..99). 값 = `0.d1d2… × 100^e`.
- 음수: 지수 바이트 `0x3F − e`, 숫자 바이트 `0x80 − d`, 끝에 `F0`.

| 값 | 바이트 |
|---|---|
| 1 | `02 C1 81` |
| 100 | `02 C2 81` |
| -1.5 | `04 3E 7F 4E F0` |
| -0.0000001 | `03 42 76 F0` |
| 1E125 | `02 FF 8A` |
| 1E-125 | `02 82 8A` |
| 0 | `01 80` |
| 12345678901234567890123456789 | `10 CF 81 97 AD C3 D9 81 97 AD C3 D9 81 97 AD C3 D9` |
| 123456789012345678901234567890.123456789(넣은 리터럴, 서버 저장 = …890.12345679: 유효 38자리로 반올림) | `14 CF 8C A2 B8 CE DA 8C A2 B8 CE DA 8C A2 B8 CE DA 8C A2 B8 CF` |

### DATE (8바이트) / TIMESTAMP (12바이트) / TIMESTAMP WITH TIME ZONE (17바이트)
`[세기+100][연+100][월][일][시][분][초][0][나노초 4바이트 빅엔디언]`(DATE 는 나노초 칸 없음). 시·분·초는 그대로(+1 아님).
- `78 7C 01 02 03 04 05 00 07 5B CD 15` = 2024-01-02 03:04:05.123456789
- DATE `64 65 01 01 00 00 00 00` = 0001-01-01, `93 70 0C 1F …` = 4712-12-31.
- 시간대: 앞 12바이트는 **UTC 시각**, 이어서 `[시 오프셋+100][분 오프셋][0][플래그 2바이트]`.
  `+09:00` → `6D 00`, `-05:30` → `5F 1E`(분은 부호 없이, 부호는 시 오프셋을 따른다), `+14:00` → `72 00`, `-12:00` → `58 00`.
  지역 이름(`Asia/Seoul`, `America/New_York`)도 그 시점의 오프셋이 같은 칸에 들어 있다(뉴욕 1월 `5F 00`, 7월 `60 00`). 플래그: 고정 오프셋 `02 45`, UTC `02 40`, 지역 `…A6`·`01 2D`(지역 번호).
  오프셋 시 부분이 0 이고 분이 0 이 아닌 값(`-00:30`)은 부호를 알 수 없다(서버가 `+00:30` 과 같은 바이트를 저장함) → 양수로 본다.
- TIMESTAMP WITH LOCAL TIME ZONE: 바이트는 DB 시간대(UTC) 시각, 드라이버 `Value` 는 클라이언트 PC 시간대로 바꾼 값이다. 이 레인은 드라이버 `Value` 를 쓰고, 세션 시간대 처리는 열린 문제로 남긴다.

### INTERVAL
- YEAR TO MONTH 5바이트: `[연 4바이트 빅엔디언 − 0x40000000][월 + 11]`. `1-2` = `40 00 00 01 0D`, `-1-2` = `3F FF FF FF 09`, `100-0` = `40 00 00 64 0B`. 총 개월 = 연×12 + (월바이트 − 11).
- DAY TO SECOND 12바이트: `[일][초(하루 안, 부호 있음)][나노초]` 각각 `빅엔디언 − 0x40000000`. `-5 23:59:59.999999999` = `3F FF FF FB  3F FE AE 81  04 65 36 01`(−5일, −86399초, −999999999ns). 소수초는 `TimeSpan` 틱(100ns)으로 내림.

## 3. 값 읽기 규칙 (R1 `TiberoValues`)

| 분류 | 읽기 | Core 값 |
|---|---|---|
| Number | `Decimal`(107) 열은 원시 바이트(NUMBER 형식) 해석. `Byte`/`Int16·32·64`/`Double`/`Single` 형식 열은 `GetValue` | 28자리·스케일 28 이내면 `decimal`, 아니면 문자열(`1E+125` 형) |
| Date | `GetValue` | `DateTime` |
| Timestamp | 원시 바이트(소수초 보존, 틱 단위) / LTZ 는 `GetTiberoTimeStampLTZ(i).Value` | `DateTime` |
| TimestampTz | 원시 바이트 | `DateTimeOffset` (UTC 시각 + 오프셋) |
| IntervalYearMonth | 원시 바이트 | `long` 총 개월(`CellValueFormatter` 가 `+yy-mm` 으로 그림) |
| IntervalDaySecond | 원시 바이트 | `TimeSpan` |
| Raw | `GetValue` 의 `sbyte[]` (LONG RAW(110) 도 같음 — 원시 바이트는 72바이트 서술자, `GetBytes` 조각은 쓰레기, R1x win144 #85·#86) | `byte[]` |
| Text / RowId | `GetString`. **LONG(109) 만 `GetValue`** — `GetString` 은 값이 있어도 빈 글자, 원시 바이트는 72바이트 서술자, `GetChars` 는 오류(R1x #85, 40000자까지 `GetValue` 정확) | `string` |
| Clob / Blob | `GetTiberoClob` / `GetTiberoBlob` 로케이터 + 미리보기 | `LobValue` |
| BinaryFloat | **원시 바이트**(4 = BINARY_FLOAT, 8 = BINARY_DOUBLE): 저장 형식 = 순서 보존 인코딩(big-endian, 맨 위 비트 1 = 양수 → 부호 비트만 반전, 0 = 음수 → 모든 비트 반전) 을 풀어 IEEE 로. `GetValue`·`GetFloat`·`GetDouble` 은 음수가 틀림(드라이버가 부호 비트만 뒤집음: -2.25 → -1.8749999, R1x #85). 서버는 -0.0 을 +0 으로 저장 | `float` / `double` |
| RefCursor | OUT 값 `TiberoRefCursor.GetDataReader()` | 결과 집합 |

## 4. LOB

- 임시 LOB(`TO_CLOB(…)`)·열 LOB 모두 `TiberoClob`/`TiberoBlob` 이 `Stream` 이다. `Clob.Length` 와 `Clob.Position` 은 **UCS-2 바이트** 단위(문자 수 = Length/2, 문자 N 으로 가려면 Position = 2N). Oracle `OracleClob` 과 같다. `Read(char[],…)` 는 문자, `Read(byte[],…)` 는 CLOB 에서 -1 을 준다.
- `GetValue` 의 CLOB 문자열은 전체 내용이다(미리보기 길이를 줄일 수 없다). 미리보기는 로케이터에서 앞부분만 읽어 만든다.
- 리더가 다음 행으로 넘어간 뒤에도 앞 행의 로케이터를 읽을 수 있다. `Clone()` 한 복사본은 `Position=0` 으로 따로 읽힌다.
- `TiberoCommand.InitialLOBFetchSize` 는 설정되지만 리더에서 읽으면 0 이다(효과를 관찰할 수 없다).
- NCLOB 는 `IsNClob=true`, 임시 NCLOB 의 `Length` 가 0 으로 오는 경우가 있다(로케이터 읽기는 정상). 시험 사용자 스키마에는 LOB 열 표가 없어 열 LOB 은 실측하지 못했다.

## 5. ROWID

- `SELECT ROWID …` 는 **실제 표**에서만 된다. `DUAL`·뷰·인라인 뷰·`USER_TABLES` 는 `ODP-8026 Invalid identifier`. 표 별칭(`t.ROWID`)·`ROWID AS RID`·`ORDER BY`·자체 조인은 된다.
- 형식 `AAAAtYAAEAAAAfQAAA`(18글자, Oracle 확장 ROWID 와 같은 모양). `GetString` 으로 읽는다. 스키마 `IsRowID=True`, `GetDataTypeName`=`RowID`, `ProviderType=126`.
- 비교 바인드: `WHERE ROWID = :R`(VARCHAR2 로 바인드)와 `WHERE ROWID = CHARTOROWID(:R)` 둘 다 된다.
- **스키마 표의 출처 정보는 믿을 수 없다.** `BaseTableName` 은 FROM 첫 표 이름이 모든 열(식·다른 표 열 포함)에 들어가고, `BaseColumnName` 은 열 이름이 아니라 **별칭**이다(`t.REV AS R2` → `R2`), `BaseSchemaName` 은 비어 있다. `IsAliased`·`IsExpression` 도 비어 있다. → 결과 편집(R4)은 이 값만으로 표·열을 정하지 말고 문장을 해석해 확인한다. 조인에서는 BaseTable 이 두 번째 표 열에도 첫 표로 나온다.
- `FROM` 뒤에 `VERSIONS BETWEEN` 같은 Oracle 플래시백 구문은 Tibero 6 에서 문법 오류.

## 6. 바인드

- `BindByName=true`, 이름에 `:` 접두를 붙여도 된다. 같은 이름을 여러 번 쓰는 문장은 한 번만 넣는다.
- IN 바인드 왕복(`SELECT … :V`): `Varchar2`·`NVarchar2`·`Char`·`Decimal`(28자리 decimal)·`Date`·`Raw`(`byte[]`/`sbyte[]` 모두)·`Blob`·`Clob` 정상. `DBNull.Value` 는 NULL. 한글 문자열 정상.
  - `TimeStamp` 에 `DateTime` 을 주면 **밀리초까지만** 전달된다. 나노초가 필요하면 `new TiberoTimeStamp(y,m,d,h,mi,s, 나노초)` 를 값으로 준다(정상 왕복). `TiberoTimeStamp(string)` 은 소수초 문자열에서 예외.
  - `TimeStampTZ` 에 `DateTime` 을 주면 `ODP-5012 Invalid TIME format`. `new TiberoTimeStampTZ(DateTime, "+09:00")` 는 정상(오프셋 문자열은 `±hh:mm`). `TiberoTimeStampTZ(string)` 은 틀린 값을 만든다.
  - `Boolean` 은 숫자 1/0 으로 전달된다. `BinaryDouble` 은 숫자로 전달된다.
- OUT 바인드 값 형식: `Varchar2`·`Char`·`NVarchar2` → `TiberoString`, `Decimal` → `TiberoDecimal`(23자리 값 확인, 28자리 넘으면 `OverflowException` — 아래 NUMBER OUT 항목), `Date` → `TiberoDate`, `TimeStamp` → `TiberoTimeStamp`, `Raw` → `TiberoBinary`, `Clob` → `TiberoClob`, `Blob` → `TiberoBlob`, `RefCursor` → `TiberoRefCursor`, INOUT 문자열 정상.
- **OUT 으로 쓸 수 없는 형식**(`ODP-590703 Unsupported data type`): `TimeStampTZ`, `TimeStampLTZ`, `Boolean`, `BinaryDouble`, `BinaryFloat`, `IntervalDS`, `IntervalYM`. 대신 `Varchar2` OUT(크기 지정)로 받으면 서버가 글자로 바꿔 준다: `2026/11/09 20:39:47.790407000 Asia/Seoul`, `1.5E+000`, `+000000001 02:03:04.500000000`, `+000000001-02`, `2024/01/02 03:04:05.123456789`, 큰 NUMBER 는 `1234567890123456789012345678901234567.5`. → `TiberoBindBuilder` 는 이 형식들의 OUT 을 `Varchar2`(200 이상) 로 바꾼다. INOUT 의 입력은 글자로 바꿔 넣는데(`yyyy-MM-dd HH:mm:ss.fffffff ±hh:mm`), 서버가 이 글자를 되받는 왕복은 실측하지 못했다. Boolean(PSM `TRUE`)은 변환에 실패할 수 있다.
- **NUMBER OUT·INOUT 도 `Varchar2` 로 받는다**(결정40, 실서버 실측 win144 #11): 드라이버 1.1.35 가 OUT `Decimal` 을 `ExecuteNonQuery` 안에서 `System.Decimal` 로 바꾸다가 28자리를 넘으면 `OverflowException`(`TbNumber.toDecimal`)을 던진다. 그래서 `TiberoBindPlan.IsOutCapable` 에서 `Decimal` 을 뺐다(IN 은 `Decimal` 그대로). 받은 글자는 `TiberoValues.FromOut` 이 공백을 떼고 28자리 이내·지수 표기 아님이면 `decimal`, 아니면 글자 그대로 돌려준다(빈 글자·NULL = null). INOUT 입력 `decimal` 은 invariant 글자(`1234.5`)로 넣는다.
  - **가정(결정41)**: 세션 `NLS_NUMERIC_CHARACTERS = '.,'`. Tibero 는 `NLS_TERRITORY` 를 받지 않아(tibero-driver-session.md) 사용자가 직접 `ALTER SESSION` 으로 바꾸지 않으면 기본값이 유지된다. 이 가정이 깨지면 글자→숫자 변환(OUT 읽기·INOUT 입력)이 어긋난다. 후속 항목(R2 연결 뒤): 세션을 열 때 읽은 소수점 글자로 변환한다 — 지금은 하지 않는다.
- OUT REF CURSOR: `p.Value` = `TiberoRefCursor`, `GetDataReader()` 로 열 정보(`FieldCount`, 스키마 표)와 행을 읽는다.
- `SELECT CURSOR(SELECT …) FROM DUAL` 은 열 정보도 행도 나오지 않았다(중첩 커서 열은 지원하지 않는다고 본다).

## 7. 기타

- `Pooling=false` 연결 문자열로 접속한다(연결 문자열 팩토리가 이미 넣음).
- `ReadAsync(ct)`: 이미 취소된 토큰이면 `TaskCanceledException`(드라이버가 비동기를 직접 구현하지 않고 기본 구현이 동기 `Read` 를 부른다). 서버에 가 있는 읽기를 토큰으로 끊을 수는 없다 → 취소는 `TiberoCommand.Cancel()` (R2·tb-exec).
- 서버 문자 집합이 EUC-KR 계열이면 `LPAD('가',1000,'가')` 처럼 바이트로 세는 함수가 있어 문자 수가 달라질 수 있다(서버 설정 차이, 드라이버 문제 아님).
- 서버 버전 확인: `ServerVersion` 은 `6` 만 준다(자세한 판별은 접속 시험 코드가 `V$VERSION` 으로).
