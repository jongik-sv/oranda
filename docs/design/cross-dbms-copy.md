# 교차 DBMS 데이터 복사 (Oracle ↔ Tibero) 설계

상태: 초안(XC1, 2026-11-10). 결정 원문 = 조정자 확정 D1~D19. 이 문서는 사내 설계 문서이며 공개 설명서·릴리스 노트에는 Tibero·교차 복사를 쓰지 않는다(D16).
실측으로 확정할 항목은 `*` 로 표시하고, XC5(`docs/design/tibero-driver-transfer.md` §5)가 결과를 적는다.

## 1. 범위와 원칙

- 쌍: Oracle→Tibero, Tibero→Oracle. Tibero→Tibero 도 이 회차에서 켠다(사용자 지시 2026-11-10, 초안 D1 의 「꺼짐 유지」를 대체. 같은 DBMS 경로, 값 변환 없음).
- **같은 DBMS 복사 동작은 바꾸지 않는다.** 같은 DBMS 는 이식 값 변환을 아예 거치지 않는다(`IBulkValueCodec.ToPortable`/`FromPortable` 호출 없음, `CrossTypeMap` 은 항상 Exact).
- 상대에 없는 형식은 가장 가깝게 바꾼다(Converted). 그래도 안 되면 그 열을 뺀다(Dropped) — 사전 점검 `ColumnDropped` Warning + 실행 로그 한 줄(D4).
- 값 하나가 변환에 실패하면(NaN·±Inf·기원전 날짜·모르는 시간대 지역) **그 행**이 실패한다(D5). 열 전체를 빼지 않는다.

## 2. 구조

```
원본 읽기 → 마스킹(원본 코덱) → 실패 행 키 기록(원본 값)
  → 대상 값 = target.FromPortable(source.ToPortable(v), bindType)   // 새 배열
  → 대상 배열 DML
```

- 이식 값(`Oranda.Core.Transfer.Copy.CrossDbms.PortableValues.cs`): 두 DBMS 드라이버 형식 사이의 중립 값.
- 코덱(`IBulkValueCodec`) 기본 구현 멤버(D2): `IsReadable(serverType)`(기본 = `IsSupported`), `ToPortable(value, serverType)`, `FromPortable(portable, bindType)`(기본 = NotSupported).
- 형식 지도(`CrossTypeMap.Map(srcDbms, tgtDbms, ColumnInfo)`): 순수 함수. 사전 점검·실행기·DDL 이 같은 표를 쓴다.
- **바인드 형식 = 원본 형식을 대상 DBMS 로 옮긴 형식**(D3). 대상 열 형식이 아니다: 기존 대상 표의 형식 차이는 지금처럼 서버가 바꾼다(`NarrowerType` 그대로).

### 2.1 이식 값

| 값 | 이식 값 | 비고 |
|---|---|---|
| NUMBER·FLOAT | `PortableNumber`(고정소수점 글자) | 지수 표기는 풀어 쓴다(`1E+125` → 1 뒤 0 125개). 유효숫자는 자르지 않는다 — Tibero 39자리 이상 반올림은 서버 몫(tibero-driver-transfer §3.11) |
| BINARY_FLOAT·DOUBLE | `PortableBinaryFloat`·`PortableBinaryDouble` | NaN·±Inf 포함. 대상이 NUMBER 면 `PortableNumber.FromSingle/FromDouble`(왕복 글자 `"R"`), NaN·Inf 는 행 실패 |
| CHAR·VARCHAR2·NCHAR·NVARCHAR2·CLOB·NCLOB·LONG | `string` | |
| RAW·BLOB·LONG RAW | `byte[]` | |
| DATE | `DateTime`(초, Kind Unspecified) | 기원전 = 행 실패* |
| TIMESTAMP(n) | `PortableTimestamp(DateTime 초, int 나노)` | |
| TIMESTAMP WITH TIME ZONE | `PortableTimestampTz(PortableTimestamp 벽시계, 오프셋 ±HH:MM, 지역)` | 오프셋·지역 중 하나 이상 |
| INTERVAL YEAR TO MONTH | `PortableIntervalYm(long 개월)` | |
| INTERVAL DAY TO SECOND | `PortableIntervalDs(Int128 나노초)` | |

실패는 `PortableConversionException` 한 가지로 올린다. 실행기는 이것을 잡아 그 행을 실패 행으로 기록한다(XC6).

## 3. 형식 지도

`Converted`/`Dropped` 의 이유는 `CrossNote`/`CrossDropReason` 열거값이고, 사전 점검 문구는 점검 쪽(XC7)이 붙인다.
길이 경계는 `ColumnInfo.Length` 숫자 그대로 비교한다(바이트·문자 구분 없음, D9 「단순·안전」).

### 3.1 Oracle → Tibero

| Oracle | Tibero | 판정 | 바인드 |
|---|---|---|---|
| NUMBER(p,s)·FLOAT(b) | 같음 | Exact | NUMBER |
| BINARY_FLOAT·BINARY_DOUBLE | DDL 같음 | Converted `BinaryAsNumber` | NUMBER 글자(D6). 서버가 BINARY_* 로 바꾼다 |
| CHAR·VARCHAR2·NCHAR·NVARCHAR2 | 같음 | Exact* (Tibero CP949 밖 글자, D13) | 같음 |
| VARCHAR2 > 4000 (EXTENDED) | VARCHAR2(n) (65532B 까지) | Exact | VARCHAR2 |
| CLOB·NCLOB·BLOB | 같음 | Exact (BLOB·큰 CLOB 은 한 행씩, 길이 0 BLOB → NULL 안내 D17) | 같음 |
| RAW ≤ 2000 | RAW | Exact | RAW |
| RAW > 2000 (EXTENDED) | BLOB | Converted `RawToBlob` | BLOB |
| DATE·TIMESTAMP(n) | 같음 | Exact | 같음 |
| TIMESTAMP WITH TIME ZONE | 같음 | Converted `TimeZoneRegionToOffset`* (지역 → 그 시각 오프셋, 지역 잃음 안내, D7). XC5 실측(win144 #128)으로 확정: Tibero TZ 쓰기 됨. 시 0 인 음수 오프셋(`-00:30`)은 같은 시각을 `+00:00` 으로 옮김 | TIMESTAMP WITH TIME ZONE |
| INTERVAL YM·DS | 같음 | Exact | 같음 |
| IDENTITY 열 | 일반 열 | Converted `IdentityToPlain` (표 만들기 전용 — 형식 판정이 Exact 일 때만 이유로 나옴) | 형식 그대로 |
| TIMESTAMP WITH LOCAL TIME ZONE | — | Dropped `LocalTimeZone` (D8) | |
| LONG·LONG RAW | — | Dropped `LongSource` (D10, 대량 읽기 미지원) | |
| ROWID·UROWID | — | Dropped `RowId` | |
| BFILE·XMLTYPE·JSON·VECTOR·BOOLEAN·객체·컬렉션·SDO_GEOMETRY·모르는 형식 | — | Dropped `Unreadable` | |

### 3.2 Tibero → Oracle

| Tibero | Oracle | 판정 | 바인드 |
|---|---|---|---|
| NUMBER·FLOAT | 같음 | Exact | NUMBER |
| BINARY_FLOAT·BINARY_DOUBLE | 같음 | Exact (원시 바이트 읽기 → float/double) | 같음 |
| CHAR·NCHAR(≤1000)·NVARCHAR2(≤2000)·VARCHAR2(≤4000) | 같음 | Exact (Tibero 사전 이름 VARCHAR·NVARCHAR 는 VARCHAR2·NVARCHAR2 로 통일) | |
| VARCHAR2 > 4000 | CLOB | Converted `LongStringToClob` (D9, 만들기 한정 — 기존 대상 표는 `NarrowerType`) | CLOB |
| NVARCHAR2 > 2000·NCHAR > 1000 | NCLOB | Converted `LongNationalStringToNClob` (Oracle 4000B 한도, AL16UTF16 가정 문자당 2B) | NCLOB |
| CLOB·NCLOB·BLOB·RAW | 같음 | Exact | |
| LONG | CLOB | Converted `LongToClob` (D10) | CLOB |
| LONG RAW | BLOB | Converted `LongRawToBlob` (D10) | BLOB |
| DATE·TIMESTAMP(n) | 같음 | Exact | |
| TIMESTAMP WITH TIME ZONE | 같음 | Exact* (Tibero 지역 값 읽기 모양 미실측) | |
| INTERVAL YM·DS | 같음 | Exact | |
| TIMESTAMP WITH LOCAL TIME ZONE | — | Dropped `LocalTimeZone` (D8) | |
| ROWID | — | Dropped `RowId` | |
| TIME·BFILE·XMLTYPE·GEOMETRY·객체·모르는 형식 | — | Dropped `Unreadable` | |

### 3.3 대상 열 정의(`CrossColumnMapping.Target`)

표 만들기 DDL(XC8)이 쓰는 열: 이름·위치·널 허용·기본값·길이·정밀도·`DataType` 의 괄호(`TIMESTAMP(9)`)는 원본 그대로, IDENTITY·도메인(Oracle 23ai)은 지운다.
형식을 바꾼 열(LOB 로의 변환)은 `DataType` 을 바꾸고 길이·정밀도를 버린다. 가상 열·함수 기반 인덱스·COLLATE 처리는 XC8 몫이다.

### 3.4 주의 (실서버 시험 XC10)

- Oracle `CHAR(n CHAR)` → Tibero: Oracle 은 CHAR 를 바이트로, Tibero 는 글자 수로 공백을 채워 뒤 공백 수가 달라질 수 있다(점검 `CharPadDiffers`, 통과 안내).
- Tibero `VARCHAR(n CHAR)` → Oracle: DDL 은 `VARCHAR2(n CHAR)` 그대로이나 Oracle 은 4000바이트를 넘는 값을 받지 못해 그 행이 실패한다(ORA-12899). 점검 `CrossStringBytes`(확인): 복사 열의 짧은 쪽 길이 × 4바이트(UTF-8 최대) > 4000 인 VARCHAR2 열을 알린다(MAX_STRING_SIZE 는 읽지 않고 STANDARD 로 봄).
- 기본값 `시퀀스.NEXTVAL`(양방향 표 만들기): 시퀀스 이름을 대상 스키마 `ALL_SEQUENCES` 에서 찾아 없으면 그 열의 DEFAULT 만 빼고 만든다(점검 `SequenceDefaultDropped`, 확인). 미리 보기·실행 DDL 은 같은 계획 구조에서 나온다. 같은 DBMS 복사는 바꾸지 않는다.

## 4. 실측이 필요한 가정(XC5) — 결과는 `tibero-driver-transfer.md` §5

1. Tibero TZ 쓰기: 지역 이름·음수 분 오프셋 → D7.
2. LTZ 뜻(세션·DB 시간대 기준) → D8.
3. Tibero CP949 대상에 그 밖 글자: 오류인지 조용히 `?` 인지 → D13.
4. 숫자 글자: `OracleDecimal.ToString` 지수 표기와 Tibero 숫자 파서, 39~40자리 반올림.
5. 교차 DDL(CHAR 길이·DEFAULT·CHECK·인덱스) 실패 빈도, `DROP TABLE … PURGE` → D19.

## 5. 후속 후보

- BOOLEAN(23ai) → NUMBER(1), LTZ(양방향, 뜻 확정 후), Oracle LONG 읽기.

## 6. XC5 실측 결과 요약 (win144 #128, Tibero 6.2.15)

- D7 확정: TZ 쓰기 `TO_TIMESTAMP_TZ(:C1, '… FF9 TZH:TZM')` 됨(배열=스칼라). 교차 쓰기는 `IBulkValueCodec.IsCrossWritable`(Tibero: IsSupported + TZ)로 켜고 같은 DBMS `IsSupported` 는 그대로. `-00:MM` 은 +00:00 으로 옮김. 대량 읽기는 오프셋만 준다(지역 잃음).
- D8 유지: LTZ 양방향 Dropped. 뜻은 확인됨(절대 시각 저장, 세션 시간대 표시) → 후속 후보.
- D6 확정: BINARY_* 는 NUMBER 글자 `:C1` 그대로 바인드(값 보존).
- D13: 시험 DB 는 UTF-8 이라 손실 없음 → 인코딩 검사 없이 안내만(`CrossCharset`).
- D19: 만들기 DDL(CHAR 길이·DEFAULT·CHECK·인덱스·COMMENT) 모두 됨 → 실패 완화 불필요. 정리 = `DROP TABLE … PURGE`. CREATE TABLE 은 앞 트랜잭션을 묵시 커밋한다.

