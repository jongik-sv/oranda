# 교차 DBMS 데이터 복사 (Oracle ↔ Tibero) 설계

상태: 확정(XC12, 2026-11-10). 결정 원문 = 조정자 확정 D1~D19(§1.1). 이 문서는 사내 설계 문서이며 공개 설명서·릴리스 노트에는 Tibero·교차 복사를 쓰지 않는다(D16).
실측으로 확정한 항목은 `docs/design/tibero-driver-transfer.md` §5(XC5)에 있고, 결과 요약은 §6 에 옮겼다. 사용자에게 알리는 안내는 `docs/user/tibero.md`(사내).

## 1. 범위와 원칙

- 쌍: Oracle→Tibero, Tibero→Oracle, Tibero→Tibero. Tibero→Tibero 는 이 회차에서 함께 켠다(사용자 지시 2026-11-10, 초안 D1 의 「꺼짐 유지」를 대체. 같은 DBMS 경로, 값 변환 없음).
- **같은 DBMS 복사 동작은 바꾸지 않는다.** 같은 DBMS 는 이식 값 변환을 아예 거치지 않는다(`IBulkValueCodec.ToPortable`/`FromPortable` 호출 없음, `CrossTypeMap` 은 항상 Exact).
- 상대에 없는 형식은 가장 가깝게 바꾼다(Converted). 그래도 안 되면 그 열을 뺀다(Dropped) — 사전 점검 `ColumnDropped`(등급 `CheckLevel.Confirm` — D4 의 「Warning」은 「Error 아님」이라는 뜻) + 실행 로그 한 줄(D4).
- 값 하나가 변환에 실패하면(NaN·±Inf·기원전 날짜·모르는 시간대 지역) **그 행**이 실패한다(D5). 열 전체를 빼지 않는다.

### 1.1 결정 D1~D19

[제품] = 제품 선택. 기본값은 조정자가 그대로 썼고, 아래는 실제로 확정된 내용이다.

| # | 결정 | 확정 내용 |
|---|---|---|
| D1 | 범위 쌍 | Oracle→Tibero·Tibero→Oracle, 그리고 Tibero→Tibero 도 켠다(사용자 지시 2026-11-10 — 초안의 「Tibero→Tibero 꺼짐 유지」를 대체). |
| D2 | 구조 | 원본/대상 `CopyDbmsParts` + Core 이식 값. 같은 DBMS 경로는 변환을 거치지 않는다. |
| D3 | 바인드 형식 | 원본 형식을 대상 DBMS 로 옮긴 형식(대상 열 형식 아님). 대상 열과의 차이는 서버가 바꾼다(`NarrowerType` 그대로). |
| D4 [제품] | 교차에서 옮길 형식 없는 열 | Error 가 아니다: `ColumnDropped` 를 등급 `CheckLevel.Confirm` 으로 알리고(D4 원문의 「Warning」= Error 아님) 그 열은 자동으로 뺀다 + 실행 로그 한 줄. 같은 DBMS 의 `UnsupportedType`(Error + 선택)은 그대로. 빠진 열이 키·기간 조건·부모 키 연결 열이면 `KeyColumnDropped` Error, 대상 NOT NULL·기본값 없음이면 `TargetRequiredColumnMissing` Error 가 그대로다. |
| D5 [제품] | 값 하나 변환 실패(NaN·Inf·기원전·모르는 지역) | 그 행 실패(실패 행 기록·상한). 열 전체를 빼지 않는다. |
| D6 | Oracle BINARY_* → Tibero | NUMBER 글자 바인드(Tibero BINARY 쓰기를 켜지 않는다). XC5 실측으로 확정: `:C1` 그대로 넣으면 서버가 BINARY_* 로 바꾸고 값이 보존된다. |
| D7 [제품] | TZ 지역 이름 | 그 시각의 오프셋으로 옮긴다(지역 잃음 안내). XC5 실측으로 확정: Tibero TZ 쓰기 됨(`TO_TIMESTAMP_TZ(:C1, '… FF9 TZH:TZM')`, 배열 = 스칼라). 시 0 인 음수 오프셋 `-00:MM` 은 같은 시각을 `+00:00` 으로 옮긴다. |
| D8 [제품] | LTZ | 양방향 Dropped + 안내. XC5 실측으로 뜻은 확인됨(절대 시각 저장, 세션 시간대 표시) → 후속 후보로 남김. |
| D9 | Tibero VARCHAR2 >4000B → Oracle | CLOB(만들기 한정, 기존 대상 테이블은 `NarrowerType`). EXTENDED 여부와 무관(단순·안전). NVARCHAR2·NCHAR 가 Oracle 한도를 넘으면 NCLOB. |
| D10 | LONG·LONG RAW | Tibero 원본 = CLOB·BLOB 으로 옮김. Oracle 원본 = Dropped(Oracle 대량 읽기 미지원). |
| D11 [제품] | CreateIfMissing 교차 | 양방향 범위 안. Tibero 대상 만들기 차단(`ICopyPrecheckDialect.CreateBlockedReason`, 다시 만들기 차단과 갈랐다)은 XC8b 에서 풀렸다(Tibero 대상 만들기는 막히지 않는다). 가상 열·함수 기반 인덱스는 만들지 않고 안내. |
| D12 [제품] | Recreate 교차 | Tibero→Oracle 만. Oracle→Tibero 는 막힘 유지(`RecreateBlockedReason`). |
| D13 | 문자셋(Tibero CP949) | XC5 실측: 시험 DB 는 UTF-8 이라 손실이 없었다. 클라이언트 인코딩 검사는 하지 않고 안내만 한다(`CrossCharset`). CP949 DB 에서의 동작은 미실측. |
| D14 [제품] | 앱 진입 | 「DBMS 다름」 막음을 「지원 쌍 아님」으로 바꿨다(`DataCopyServices.IsPairSupported`: 같은 DBMS 는 항상 참, 다른 DBMS 는 Oracle↔Tibero 이고 두 DBMS 의 교차 부품이 등록돼 있을 때 참). Tibero 활성 세션에서도 메뉴를 켠다. |
| D15 | 등록 | 초안(XC10 녹색 뒤 한꺼번에 등록)에서 바뀜. XC0 이 Tibero 대량 실행(`TiberoBulkExecutionService`)·복사 실행기·사전 점검을 `Dbms.Tibero` 키로 먼저 등록했다(Tibero→Tibero 를 켜기 위해). 교차 부품 `TiberoCopyParts`(`ICopyDbmsParts`)의 키 등록은 XC11 이 교차 IT 통과 뒤에 했다(`[OrandaService(typeof(ICopyDbmsParts), Dbms = Dbms.Tibero)]`). 등록 뒤 앱에서 Oracle↔Tibero 쌍이 열린다. |
| D16 | 문서 | 공개 설명서(`site/` 아래)·릴리스 노트에 Tibero·교차 복사 언급 없음(기록된 결정). 이 문서와 `docs/user/tibero.md`(사내)만 쓴다. |
| D17 | 길이 0 BLOB → Tibero | 길이 0 BLOB 은 Tibero 에서 NULL 이 된다. 받아들이고, 복사 열에 BLOB 이 있으면 사전 점검(`BlobEmptyToNull`, 통과 항목)으로 안내한다. |
| D18 | 같은 DB 판정 | DBMS 가 다르면 DbKey 비교 전에 다른 DB 로 본다. |
| D19 | 교차 DDL 의 인덱스·주석 실패 | 테이블 실패로 둔다(지금 경로 그대로). XC5 실측으로 확정: CHAR 길이·DEFAULT·CHECK·인덱스·COMMENT 모두 되므로 완화 불필요. 만든 테이블 정리 = `DROP TABLE … PURGE`. CREATE TABLE 은 앞 트랜잭션을 묵시 커밋한다. |

## 2. 구조

```
원본 읽기 → 마스킹(원본 코덱) → 실패 행 키 기록(원본 값)
  → 대상 값 = target.FromPortable(source.ToPortable(v), bindType)   // 새 배열
  → 대상 배열 DML
```

### 2.1 구현 구조 요약

| 부분 | 내용 | 파일 |
|---|---|---|
| 원본/대상 부품 | `CopyDbmsParts`(DBMS·카탈로그·오류 분류·복사 방언·점검 방언·코덱·다시 만들기 전 단계 한 벌)와 DBMS 어셈블리가 내는 `ICopyDbmsParts`. 실행기·점검은 원본 벌과 대상 벌을 따로 받는다: 원본 세션에 보내는 문장·원본 값 해석은 원본 벌, 나머지(대상 조회·DML·DDL·되살리기)는 대상 벌. 같은 DBMS 복사는 같은 벌을 두 번 넘긴다. | `src/Oranda.Core/Transfer/Copy/CopyDbmsParts.cs`, `src/Oranda.Oracle/Transfer/Copy/OracleCopyParts.cs`, `src/Oranda.Tibero/Transfer/Copy/TiberoCopyParts.cs` |
| 이식 값 | 두 DBMS 드라이버 형식 사이의 중립 값(§2.2)과 변환 실패 예외 `PortableConversionException`. | `src/Oranda.Core/Transfer/Copy/CrossDbms/PortableValues.cs` |
| 코덱 확장 | `IBulkValueCodec` 의 기본 구현 멤버 `IsReadable`·`IsCrossWritable`·`ToPortable`·`FromPortable`(D2). 구현은 DBMS 별 코덱에 있다. | `src/Oranda.Core/Transfer/Copy/IBulkValueCodec.cs`, `src/Oranda.Oracle/Transfer/Copy/OracleBulkValueCodec.cs`·`OraclePortable.cs`, `src/Oranda.Tibero/Execution/TiberoBulkValueCodec.cs`·`TiberoBulkPortable.cs` |
| 형식 지도 | `CrossTypeMap.Map(srcDbms, tgtDbms, ColumnInfo)` — 순수 함수, 결과 `CrossColumnMapping`(Exact/Converted/Dropped, 바인드 형식, 이유 `CrossNote`·`CrossDropReason`). 점검·실행기·DDL 이 같은 테이블을 쓴다(§3). | `src/Oranda.Core/Transfer/Copy/CrossDbms/CrossTypeMap.cs` |
| 교차 실행기 | `CopyRunner` 가 원본·대상 DBMS 가 다르면 열마다 `CrossTypeMap` 으로 옮기고(옮길 형식 없는 열은 뺀다) 마스킹한 원본 값을 대상 값으로 바꾼다(`RowShape`·`CrossSides`). 값 변환 실패는 그 행 실패로 기록하고, 빠진 열은 실행 로그(`Notices`)에 한 줄 남긴다. | `src/Oranda.Core/Transfer/Copy/CopyRunner.cs` |
| 교차 디스패치 | 같은 DBMS 는 기존 키 서비스(`ICopyRunner`·`ICopyPrecheckService`), 다르면 두 DBMS 의 `ICopyDbmsParts` 로 `CopyRunner`·`CopyPrecheckService` 를 만든다. 쌍 판정은 `DispatchRules`. | `src/Oranda.Core/Composition/Dispatch/DispatchRules.cs`, `CopyRunnerDispatcher.cs`, `CopyPrecheckServiceDispatcher.cs` |
| 교차 점검 | `ColumnDropped`·`KeyColumnDropped`·`TypeConverted`·`CrossCharset`·`BlobEmptyToNull`·`CrossStringBytes`·`CharPadDiffers`·`SequenceDefaultDropped` 항목(코드 상수는 `CopyCheckCodes`). 열 고르기·형식 판정은 원본 읽기 가능·대상 쓰기 가능으로 가른다. | `src/Oranda.Core/Transfer/Copy/PrecheckRules.cs`, `CopyPrecheckService.cs`, `ICopyPrecheckService.cs` |
| 테이블 만들기 | 매핑한 테이블 구조로 `CreateTableDdl` 을 호출한다(`CrossTableMap.MapTable`/`Build`). 실행기와 사전 점검 미리 보기가 같은 함수를 써서 보내는 문장과 보여 주는 문장이 같다. 만들기·다시 만들기 차단은 `ICopyPrecheckDialect.CreateBlockedReason`/`RecreateBlockedReason` 으로 가른다. | `src/Oranda.Core/Transfer/Copy/CrossTableMap.cs`(XC8), `CreateTableDdl.cs`, `ICopyPrecheckDialect.cs`, `src/Oranda.Tibero/Transfer/Copy/TiberoCopyPrecheckDialect.cs` |
| 앱 | 지원 쌍 판정과 점검 코드의 표시 이름. | `src/Oranda.App/Features/DataCopy/DataCopyServices.cs`(`IsPairSupported`), `PrecheckViewModel.cs`(`CodeNames`) |

- 코덱(`IBulkValueCodec`) 기본 구현 멤버(D2): `IsReadable(serverType)`(기본 = `IsSupported`), `IsCrossWritable(bindType)`(기본 = `IsSupported`; Tibero 는 TZ 를 더해 켠다, §6), `ToPortable(value, serverType)`, `FromPortable(portable, bindType)`(기본 = NotSupported).
- **바인드 형식 = 원본 형식을 대상 DBMS 로 옮긴 형식**(D3). 대상 열 형식이 아니다: 기존 대상 테이블의 형식 차이는 지금처럼 서버가 바꾼다(`NarrowerType` 그대로).
- 오류 분류는 원본 벌·대상 벌을 합쳐 쓴다(대상 우선). 같은 DB 판정(`CopySameDatabase.cs`)은 DBMS 가 다르면 DbKey 비교 전에 다른 DB 로 본다(D18).

### 2.2 이식 값

| 값 | 이식 값 | 비고 |
|---|---|---|
| NUMBER·FLOAT | `PortableNumber`(고정소수점 글자) | 지수 표기는 풀어 쓴다(`1E+125` → 1 뒤 0 125개). 유효숫자는 자르지 않는다 — Tibero 39자리 이상 반올림은 서버 몫(tibero-driver-transfer §3.11) |
| BINARY_FLOAT·DOUBLE | `PortableBinaryFloat`·`PortableBinaryDouble` | NaN·±Inf 포함. 대상이 NUMBER 면 `PortableNumber.FromSingle/FromDouble`(왕복 글자 `"R"`), NaN·Inf 는 행 실패 |
| CHAR·VARCHAR2·NCHAR·NVARCHAR2·CLOB·NCLOB·LONG | `string` | |
| RAW·BLOB·LONG RAW | `byte[]` | |
| DATE | `DateTime`(초, Kind Unspecified) | 기원전 = 행 실패 |
| TIMESTAMP(n) | `PortableTimestamp(DateTime 초, int 나노)` | |
| TIMESTAMP WITH TIME ZONE | `PortableTimestampTz(PortableTimestamp 벽시계, 오프셋 ±HH:MM, 지역)` | 오프셋·지역 중 하나 이상 |
| INTERVAL YEAR TO MONTH | `PortableIntervalYm(long 개월)` | |
| INTERVAL DAY TO SECOND | `PortableIntervalDs(Int128 나노초)` | |

실패는 `PortableConversionException` 한 가지로 올린다. 실행기는 이것을 잡아 그 행을 실패 행으로 기록한다(값은 실패 이유에 싣지 않는다).

## 3. 형식 지도

`Converted`/`Dropped` 의 이유는 `CrossNote`/`CrossDropReason` 열거값이고, 사전 점검 문구는 점검 쪽(`PrecheckRules`)이 붙인다.
길이 경계는 `ColumnInfo.Length` 숫자 그대로 비교한다(바이트·문자 구분 없음, D9 「단순·안전」).

### 3.1 Oracle → Tibero

| Oracle | Tibero | 판정 | 바인드 |
|---|---|---|---|
| NUMBER(p,s)·FLOAT(b) | 같음 | Exact | NUMBER |
| BINARY_FLOAT·BINARY_DOUBLE | DDL 같음 | Converted `BinaryAsNumber` | NUMBER 글자(D6). 서버가 BINARY_* 로 바꾼다 |
| CHAR·VARCHAR2·NCHAR·NVARCHAR2 | 같음 | Exact (Tibero CP949 밖 글자는 안내만, D13) | 같음 |
| VARCHAR2 > 4000 (EXTENDED) | VARCHAR2(n) (65532B 까지) | Exact | VARCHAR2 |
| CLOB·NCLOB·BLOB | 같음 | Exact (BLOB·큰 CLOB 은 한 행씩, 길이 0 BLOB → NULL 안내 D17) | 같음 |
| RAW ≤ 2000 | RAW | Exact | RAW |
| RAW > 2000 (EXTENDED) | BLOB | Converted `RawToBlob` | BLOB |
| DATE·TIMESTAMP(n) | 같음 | Exact | 같음 |
| TIMESTAMP WITH TIME ZONE | 같음 | Converted `TimeZoneRegionToOffset` (지역 → 그 시각 오프셋, 지역 잃음 안내, D7). XC5 실측(win144 #128)으로 확정: Tibero TZ 쓰기 됨. 시 0 인 음수 오프셋(`-00:30`)은 같은 시각을 `+00:00` 으로 옮김 | TIMESTAMP WITH TIME ZONE |
| INTERVAL YM·DS | 같음 | Exact | 같음 |
| IDENTITY 열 | 일반 열 | Converted `IdentityToPlain` (테이블 만들기 전용 — 형식 판정이 Exact 일 때만 이유로 나옴) | 형식 그대로 |
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
| VARCHAR2 > 4000 | CLOB | Converted `LongStringToClob` (D9, 만들기 한정 — 기존 대상 테이블은 `NarrowerType`) | CLOB |
| NVARCHAR2 > 2000·NCHAR > 1000 | NCLOB | Converted `LongNationalStringToNClob` (Oracle 4000B 한도, AL16UTF16 가정 문자당 2B) | NCLOB |
| CLOB·NCLOB·BLOB·RAW | 같음 | Exact | |
| LONG | CLOB | Converted `LongToClob` (D10) | CLOB |
| LONG RAW | BLOB | Converted `LongRawToBlob` (D10) | BLOB |
| DATE·TIMESTAMP(n) | 같음 | Exact | |
| TIMESTAMP WITH TIME ZONE | 같음 | Exact (대량 읽기가 오프셋만 줘서 지역은 잃음 — XC5 #2) | |
| INTERVAL YM·DS | 같음 | Exact | |
| TIMESTAMP WITH LOCAL TIME ZONE | — | Dropped `LocalTimeZone` (D8) | |
| ROWID | — | Dropped `RowId` | |
| TIME·BFILE·XMLTYPE·GEOMETRY·객체·모르는 형식 | — | Dropped `Unreadable` | |

### 3.3 대상 열 정의(`CrossColumnMapping.Target`)

테이블 만들기 DDL 이 쓰는 열: 이름·위치·널 허용·기본값·길이·정밀도·`DataType` 의 괄호(`TIMESTAMP(9)`)는 원본 그대로, IDENTITY·도메인(Oracle 23ai)은 지운다.
형식을 바꾼 열(LOB 로의 변환)은 `DataType` 을 바꾸고 길이·정밀도를 버린다. 가상 열·옮기지 않는 열·LOB 로 옮기는 열을 쓰는 제약·인덱스는 만들지 않고 안내하며, 함수 기반 인덱스는 카탈로그에 식이 없어 만들지 않는다. 이 처리는 `CrossTableMap.cs` 가 한다.

### 3.4 테이블 만들기·다시 만들기 범위

- 만들기(CreateIfMissing): 양방향 범위 안(D11). 만든 테이블 실패 정리 = `DROP TABLE … PURGE`(D19).
- 다시 만들기(Recreate): Tibero→Oracle 만(D12). Oracle→Tibero 는 `RecreateBlockedReason` 으로 막는다. 옛 테이블을 지운 뒤 CREATE 가 실패하면 실행기가 그 자리에서 `FLASHBACK TABLE … TO BEFORE DROP` 으로 되살리고 결과에 알린다(XC8e, 결정 67: 같은 DBMS 다시 만들기도 같다). 휴지통에 남지 않았거나 되살리기도 실패하면 직접 되돌리는 안내가 나온다.
- Oracle→Tibero 만들기: Tibero 대상 만들기 차단(`CreateBlockedReason`)은 XC8b 에서 풀렸다(D11).

### 3.5 주의 (실서버 시험 XC10)

- Oracle `CHAR(n CHAR)` → Tibero: Oracle 은 CHAR 를 바이트로, Tibero 는 글자 수로 공백을 채워 뒤 공백 수가 달라질 수 있다(점검 `CharPadDiffers`, 통과 안내).
- Tibero `VARCHAR(n CHAR)` → Oracle: DDL 은 `VARCHAR2(n CHAR)` 그대로이나 Oracle 은 4000바이트를 넘는 값을 받지 못해 그 행이 실패한다(ORA-12899). 점검 `CrossStringBytes`(확인): 복사 열의 짧은 쪽 길이 × 4바이트(UTF-8 최대) > 4000 인 VARCHAR2 열을 알린다(MAX_STRING_SIZE 는 읽지 않고 STANDARD 로 봄).
- 기본값 `시퀀스.NEXTVAL`(양방향 테이블 만들기, 같은 DBMS 포함 — 조정 결정 76 이 결정 50 을 대신함): 원본 테이블에 연결된 시퀀스(열 DEFAULT + 「없으면 만들기」 표는 그 표 트리거가 참조하는 시퀀스, `copy.trigger_sequences`)를 대상 스키마 `ALL_SEQUENCES` 에서 찾아 없으면 테이블보다 먼저 원본 속성(`copy.sequence_attrs`)으로 만든다(점검 `SequenceCreated`, 계획 `CopyTablePlan.CreateSequences`, 문장 `CopySequenceDdl`). 만들 수 없으면(CREATE SEQUENCE 권한 없음·속성 못 읽음·다른 계정 시퀀스) 그 열의 DEFAULT 만 빼고 만든다(`SequenceDefaultDropped`). 대상에 이미 있으면 건드리지 않고 다음 값이 원본 키 최댓값 이하면 `SequenceBehind`. 실행기는 적재 뒤 키 최댓값이 START WITH 이상이면 `ALTER SEQUENCE … INCREMENT BY` 로 맞추고(DROP 없음), 쓰는 표를 모두 지우면 `DROP SEQUENCE`(미리 보기에 있음). 미리 보기·실행 DDL 은 같은 계획 구조에서 나온다. 같은 DBMS 복사는 바꾸지 않는다. 대상이 Oracle 11g(주 버전 11 이하)이면 시퀀스가 있어도 DEFAULT 시퀀스.NEXTVAL 을 쓸 수 없어(12c 부터, ORA-00984) 모든 시퀀스 기본값을 빼고 같은 항목(`SequenceDefaultDropped`)으로 알린다(XC8e).

## 4. 실측이 필요했던 가정(XC5) — 결과는 `tibero-driver-transfer.md` §5

1. Tibero TZ 쓰기: 지역 이름·음수 분 오프셋 → D7.
2. LTZ 뜻(세션·DB 시간대 기준) → D8.
3. Tibero CP949 대상에 그 밖 글자: 오류인지 조용히 `?` 인지 → D13.
4. 숫자 글자: `OracleDecimal.ToString` 지수 표기와 Tibero 숫자 파서, 39~40자리 반올림 → D6.
5. 교차 DDL(CHAR 길이·DEFAULT·CHECK·인덱스) 실패 빈도, `DROP TABLE … PURGE` → D19.

## 5. 후속 후보

- BOOLEAN(23ai) → NUMBER(1), LTZ(양방향, 뜻 확정됨 — 원본 절대 시각 + 쓰기 세션 시간대 정책이 필요), Oracle LONG 읽기, Tibero 시간대 지역 이름 그대로 쓰기.

## 6. XC5 실측 결과 요약 (win144 #128, Tibero 6.2.15)

근거: `docs/design/tibero-driver-transfer.md` §5.

- D7 확정: TZ 쓰기 `TO_TIMESTAMP_TZ(:C1, '… FF9 TZH:TZM')` 됨(배열=스칼라). 교차 쓰기는 `IBulkValueCodec.IsCrossWritable`(Tibero: IsSupported + TZ)로 켜고 같은 DBMS `IsSupported` 는 그대로. `-00:MM` 은 +00:00 으로 옮김. 대량 읽기는 오프셋만 준다(지역 잃음).
- D8 유지: LTZ 양방향 Dropped. 뜻은 확인됨(절대 시각 저장, 세션 시간대 표시) → 후속 후보.
- D6 확정: BINARY_* 는 NUMBER 글자 `:C1` 그대로 바인드(값 보존).
- D13: 시험 DB 는 UTF-8 이라 손실 없음 → 인코딩 검사 없이 안내만(`CrossCharset`).
- D19: 만들기 DDL(CHAR 길이·DEFAULT·CHECK·인덱스·COMMENT) 모두 됨 → 실패 완화 불필요. 정리 = `DROP TABLE … PURGE`. CREATE TABLE 은 앞 트랜잭션을 묵시 커밋한다.

## 7. XC10 실측으로 정한 것 (win144 #137, Oracle 26ai + Tibero 6.2.15)

- **Tibero VARCHAR(n CHAR) 다바이트 값 → Oracle 4000B 초과**: 만든 열은 `VARCHAR2(n CHAR)` 그대로이고 4000바이트를 넘는 행은 ORA-12899 로 그 행만 실패한다(PartiallyFailed). 결정(xc-core XC8d): 열은 그대로 두고 사전 점검 안내를 낸다.
- **Oracle CHAR(n CHAR) → Tibero**: Oracle 은 바이트로, Tibero 는 글자로 채워 뒤 공백 수가 다르다(값은 같음). 결정: 안내 + 문서(값을 맞추지 않음).
- **Tibero DEFAULT seq.NEXTVAL → Oracle**: 카탈로그 저장 모양은 `이름.NEXTVAL`(소유자·따옴표 없음) → Oracle 대상 스키마의 같은 이름 시퀀스를 가리킨다. 대상에 시퀀스가 없으면 CREATE 가 ORA-02289 로 실패하고 점검 안내도 없었다. 결정: 그 DEFAULT 를 빼고 테이블을 만들고 안내한다. 뒤의 결정 76(SQ1): 시퀀스를 먼저 만들고 DEFAULT 는 그대로 둔다(만들 수 없을 때만 뺀다).
- **새 테이블에 딸린 객체(SQ2, 결정 78)**: 적재 뒤 함수 기반 인덱스(`ICopyRecreateStep.IndexColumnsAsync` = 객체 상세의 인덱스 열 조회)·원본 트리거·원본 권한(「없으면 만들기」만, 원본 세션 `CaptureAsync`)·작업 밖 부모 FK(대상에 부모가 있을 때, NOVALIDATE 로 바꾸지 않음)를 만든다(`CompanionDdl`, 점검 `CompanionObjects`). 못 만든 것은 부분 실패 알림.
- **새 테이블 PK·UK·인덱스는 적재 뒤(PF4a, 결정 74)**: `CreateTableDdl.Build(…, keysAfterLoad: true)` 가 떼어 낸 `AfterLoad` 문장을 되살리기 맨 앞에서 보낸다. PK·UK 실패 = 표 실패.
- **VARCHAR2(32767 CHAR)** 는 EXTENDED 가 아닌 Oracle 26ai 에서는 원본을 만들 수 없어 측정하지 못했다. 임시 사용자는 `V$PARAMETER` 를 못 읽는다(MAX_STRING_SIZE 조회 ORA-00942).
- Oracle 다시 만들기(Tibero→Oracle)는 PURGE 없이 옛 테이블을 휴지통에 남긴다(FLASHBACK 안내가 나온다).

