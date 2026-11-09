# Tibero 드라이버 어댑터 (TDP.NET Core, 리플렉션 어댑터)

결정 = decisions D-046 갱신(2026-11-09, 결정25 「드라이버 위치 지정」). 설계 정본 = [tibero.md](tibero.md) 1장. 실측 근거 = [tibero-driver-session.md](tibero-driver-session.md)·[tibero-driver-exec.md](tibero-driver-exec.md)·[tibero-driver-values.md](tibero-driver-values.md).
멤버 이름은 `Tibero.DataAccess` 1.1.35(lib/net5.0) 메타데이터로 확인했다.

## 1. 요약

- `Oranda.Tibero` 는 컴파일 때 `Tibero.DataAccess` 를 참조하지 않는다. 공개본·CI·사내 빌드가 같은 코드다(`#if TIBERO_DRIVER`·`ORANDA_TIBERO_FEED`·`OrandaTiberoDriver` 없음).
- 사용자가 설정에서 `Tibero.DataAccess.dll`(또는 폴더·`.nupkg`) 위치를 지정한다. 개발·IT·WSL 은 환경 변수 `ORANDA_TIBERO_DRIVER` 가 설정보다 먼저다.
- 앱 시작 때는 읽지 않는다. 첫 Tibero 접속·접속 시험·설정 [확인] 중 먼저 오는 때 읽는다. 한 프로세스에서 어셈블리는 한 번만 올린다(올린 뒤 바뀐 경로는 「다시 시작하면 적용」). 올리기 전 실패(지정 안 함·파일 없음·검사 거절)는 기억하지 않는다 — 사용자가 경로를 고치면 다음 `Require(paths)` 가 다시 해석한다.
- 드라이버 형식을 아는 코드는 `src/Oranda.Tibero/Driver/` 의 어댑터 `TiberoDriver` 뿐이다. 읽은 직후 아래 2장의 모든 멤버를 컴파일된 식으로 묶고, 하나라도 없으면 「쓸 수 없음(없는 멤버 이름)」이다. `Unverified` 판(1.1.35 아닌 1.x)에도 같은 규칙이다(일부만 묶인 어댑터는 없다).

## 2. 어댑터 API 표 (`Oranda.Tibero.Driver`, 모두 `internal`)

다른 레인은 이 표의 이름·형식에 맞춰 짠다. 구현 = DV3. 멤버를 더하려면 조정 세션을 거쳐 tb-driver 에 요청한다(이름·형식·쓰는 곳).

### 2.1 진입

| 멤버 | 형식 | 드라이버 쪽 | 쓰는 곳 |
|---|---|---|---|
| `TiberoDriver.Require(IDriverPathSource? paths)` | `static TiberoDriver` | 로더(3장): 경로 해석(`ORANDA_TIBERO_DRIVER` → `paths.GetPath(Dbms.Tibero)`) → 검사 → 로드 → `Bind`. 처음 한 번, 그 뒤 같은 인스턴스. `paths` = 부르는 서비스가 생성자 선택 인자 `IDriverPathSource? paths = null` 로 받은 것(App 이 DU1 에서 등록, 없으면 환경 변수만) | 세션 관리자(tb-session S1′), 접속 시험(DV5)만 |
| `TiberoDriver.Bind(Assembly driver)` | `static TiberoDriver` | 넘긴 어셈블리에서 모든 멤버를 묶는다. 실패 = `TiberoDriverBindException` | 로더, 시험(`FakeDriverHost`) |
| `TiberoDriver.NotSpecifiedMessage` | `const string` | — | 「Tibero 드라이버가 지정되지 않았습니다. 설정 > 드라이버에서 TDP.NET Core(Tibero.DataAccess.dll) 위치를 지정하세요.」 지금의 `NoDriverMessage` 두 개를 대신한다 |
| `Version` | `Version` | `AssemblyName.Version` | 상태 줄·로그 |
| `Location` | `string` | `Assembly.Location` | 상태 줄 |
| `TiberoDriverUnavailableException` | `InvalidOperationException` 파생, `DriverCheck Check` | `Require(paths)` 가 못 쓸 때. `Message` = 사용자에게 보일 문장(`Check.Reason`, 지정 안 함이면 `NotSpecifiedMessage`). `Check.IsUsable` 은 늘 false | 세션 관리자·접속 시험이 잡아 실패 결과로 |
| `TiberoDriverBindException` | `TiberoDriverUnavailableException` 파생, `IReadOnlyList<string> MissingMembers` | 없는 멤버 이름(`TiberoCommand.BindByName` 꼴) 전부. `Check.State = DriverState.Unusable`, `Reason` = 「이 드라이버는 쓸 수 없습니다(없는 멤버: …)」. 그 뒤 `IDbmsDriverStatus.Current` 도 이 결과 | 로더, DV6 실제 드라이버 시험 |

### 2.2 연결·명령

| 멤버 | 형식 | 드라이버 쪽 | 쓰는 곳 |
|---|---|---|---|
| `Factory` | `DbProviderFactory` | `TiberoClientFactory.Instance` | 기본 형식 생성 |
| `CreateConnection(string connectionString)` | `DbConnection` | `Factory.CreateConnection()` + `ConnectionString`. 열지 않는다 | tb-session S1′, DV5 |
| `CreateConnectionStringBuilder()` | `DbConnectionStringBuilder` | `TiberoConnectionStringBuilder` | 필요할 때(지금 `TiberoConnectionStringFactory` 는 글자로 만든다) |
| `SetBindByName(DbCommand cmd, bool value = true)` | `void` | `TiberoCommand.BindByName`(기본 false) | tb-exec E1′, tb-result R1b·R2, tb-session |
| `SetInitialLobFetchSize(DbCommand cmd, int size)` | `void` | `TiberoCommand.InitialLOBFetchSize`(1.1.35 에서 효과 관찰 안 됨) | tb-exec, tb-result |
| `SetArrayBindCount(DbCommand cmd, int count)` | `void` | `TiberoCommand.ArrayBindCount` | 배열 DML(T5 가져오기) |

명령은 `DbConnection.CreateCommand()`, 매개변수는 `DbCommand.CreateParameter()`, 트랜잭션은 `DbConnection.BeginTransaction()`, 취소는 `DbCommand.Cancel()` 로 기본 형식 그대로 쓴다.

### 2.3 매개변수

| 멤버 | 형식 | 드라이버 쪽 | 쓰는 곳 |
|---|---|---|---|
| `SetParameterKind(DbParameter p, TiberoParamKind kind)` | `void` | `TiberoParameter.TiberoDbType` = 같은 이름의 `TiberoDbType` 값(정수가 아니라 이름으로 묶는다 — 판이 바뀌어 정수가 달라져도 안전, 결정 정본 「TiberoDbType 이름 지정」. tb-result 요청 `SetParameterType(p, int)` 대신) | tb-result 바인드(R1b), tb-exec(CLOB OUT·RefCursor OUT) |
| `KindOfProviderType(int providerType)` | `TiberoParamKind?` | 스키마 표 `ProviderType`(= `TiberoDbType` 정수) → 이름. 모르는 값 = null | tb-result(정수 직접 비교 대신 권장) |
| `SetCollectionType(DbParameter p, TiberoCollectionKind kind)` | `void` | `TiberoParameter.CollectionType`(`None`·`PLSQLAssociativeArray`). 연관 배열 OUT 은 1.1.35 에서 안 됨(exec 5장) | 배열 바인드 |
| `CreateTimeStamp(DateTime value, int nanosecond)` | `object`(`DbParameter.Value` 에 넣을 값) | `new TiberoTimeStamp(y, M, d, h, m, s, nanosecond)` — `value` 의 초 아래는 버리고 `nanosecond`(0~999,999,999, 밖이면 `ArgumentOutOfRangeException`)를 쓴다. `DateTime` 을 그대로 주면 밀리초까지만 감(values 6장) | tb-result 바인드 |
| `CreateTimeStampTz(DateTime value, string offset)` | `object` | `new TiberoTimeStampTZ(DateTime, string)`. `value` = 그 오프셋의 지역 시각, `offset` = `±hh:mm`(그 밖이면 `ArgumentException`). 실측된 생성자(values 6장). 나노초 생성자도 있으나 실측 전이라 두지 않음 | tb-result 바인드 |

`TiberoParamKind`(어댑터 enum, 값 이름 = `TiberoDbType` 이름, 정수는 드라이버 값과 무관): `Varchar2, NVarchar2, Char, NChar, Long, Clob, NClob, Blob, Raw, LongRaw, Byte, Int16, Int32, Int64, Decimal, Single, Double, BinaryFloat, BinaryDouble, Boolean, Date, TimeStamp, TimeStampTZ, TimeStampLTZ, IntervalDS, IntervalYM, RefCursor, XmlType, BFile`.
`TiberoCollectionKind`: `None, AssociativeArray`.

### 2.4 리더

| 멤버 | 형식 | 드라이버 쪽 | 쓰는 곳 |
|---|---|---|---|
| `ReadRawBytes(DbDataReader r, int i)` | `byte[]?` | `TiberoDataReader.GetTiberoBinary(i)` → `IsNull` 이면 null, 아니면 `Value`(서버 원시 바이트) | tb-result `TiberoValues.ReadCell`(NUMBER·TIMESTAMP·TZ·INTERVAL 직접 해석) |
| `ReadClob(DbDataReader r, int i)` | `TiberoLob?` | `GetTiberoClob(i)`(CLOB·NCLOB, 구분은 `IsNClob`). `IsNull` 이면 null | tb-result 미리보기·R3 LOB 접근 |
| `ReadBlob(DbDataReader r, int i)` | `TiberoLob?` | `GetTiberoBlob(i)`. `IsNull` 이면 null | 같음 |
| `ReadTimeStampLtz(DbDataReader r, int i)` | `DateTime?` | `GetTiberoTimeStampLTZ(i).Value`(클라이언트 PC 시간대) | tb-result |

리더의 `FetchSize`·`RowSize` 는 1.1.35 에서 항상 0 이고 써도 효과가 없어(values 1장) 어댑터 멤버를 두지 않는다.

2장의 `DbCommand`·`DbParameter`·`DbDataReader`·`DbConnection` 인자가 이 어댑터의 드라이버 형식이 아니면(다른 공급자·다른 ALC 의 가짜) `ArgumentException`. null 이면 `ArgumentNullException`.

### 2.5 공급자 값(OUT 매개변수·`GetValue` 의 드라이버 값)

| 멤버 | 형식 | 드라이버 쪽 | 쓰는 곳 |
|---|---|---|---|
| `IsProviderNull(object? value)` | `bool` | null·`DBNull` 이거나 `Tibero.DataAccess.Types.INullable.IsNull` | tb-exec(OUT NULL 은 `DBNull` 이 아니라 `IsNull=true` 인 `TiberoString`) |
| `ConvertProviderValue(object? value)` | `TiberoOutValue` | 아래 변환 | tb-result `TiberoValues` |
| `ReadOutValue(DbParameter p)` | `TiberoOutValue` | `ConvertProviderValue(p.Value)` | tb-exec(DBMS_OUTPUT CLOB OUT), tb-result OUT 값 |

`TiberoOutValue`(readonly record struct): `TiberoValueKind Kind`, `string? ProviderTypeName`(드라이버 형식의 짧은 이름, 진단·오류 문장용), `bool IsNull`, `object? Value`, `int Nanosecond`(시각 종류만, 그 밖 0).
`TiberoValueKind`: `Null, String, Decimal, Date, TimeStamp, TimeStampLtz, Binary, Lob, RefCursor, Clr, Other`.

**`Value` 에는 드라이버 형식 객체를 절대 넣지 않는다**(.NET 기본 형식·`TiberoLob`·`DbDataReader` 만).

| 드라이버 값 | `Kind` | `Value` |
|---|---|---|
| null·`DBNull`·`INullable.IsNull = true` | `Null` | null, `IsNull = true`(`ProviderTypeName` 은 드라이버 형식이면 채움) |
| `TiberoString` | `String` | `string`(`Value`) |
| `TiberoDecimal` | `Decimal` | invariant 글자(`ToString()` 을 invariant 로). 23자리 값은 실측됨(values 6장). **OUT 파라미터는 해당 없음**(DV6 실측, win144 #11): 1.1.35 는 `Decimal` OUT 을 `ExecuteNonQuery` 안에서 `System.Decimal` 로 바꿔(`TbNumber.toDecimal` → `Decimal.Parse`) 28자리를 넘으면 `OverflowException` 이 나고 `TiberoDecimal` 로 받을 기회가 없다 → NUMBER OUT 은 `Varchar2` OUT 으로 받는다(결정40, `TiberoBindPlan` = tb-result) |
| `TiberoDate` | `Date` | `DateTime`(`Value`) |
| `TiberoTimeStamp` | `TimeStamp` | `DateTime`(`Value` 의 초까지) + `Nanosecond` = `TiberoTimeStamp.Nanosecond` 속성 |
| `TiberoTimeStampLTZ` | `TimeStampLtz` | `DateTime`(`Value`, 클라이언트 PC 시간대, 초까지) + `Nanosecond` 속성 |
| `TiberoBinary` | `Binary` | `byte[]`(`Value`) |
| `TiberoClob`·`TiberoBlob` | `Lob` | `TiberoLob` |
| `TiberoRefCursor` | `RefCursor` | `DbDataReader`(`GetDataReader()`) |
| .NET 기본 값(`string`·`decimal`·`long`·`int`·`double`·`float`·`DateTime`·`TimeSpan`·`byte[]`·`sbyte[]` 등) | `Clr` | 그대로(`sbyte[]` 은 `byte[]` 로 복사) |
| 그 밖 드라이버 형식(`TiberoTimeStampTZ`·`TiberoIntervalDS`·`TiberoIntervalYM`·`TiberoXmlType`·`TiberoBFile`·`TiberoRef` 등) | `Other` | `ToString()` 글자(invariant). TZ·INTERVAL OUT 은 드라이버가 못 하므로(values 6장) `Varchar2` OUT 으로 받고, 리더 값은 `ReadRawBytes` 경로를 쓴다 |

- CLOB OUT 의 값은 실측마다 `TiberoString`(exec 5장)·`TiberoClob`(values 6장) 둘 다 나왔다 → `ReadOutValue` 를 쓰는 쪽은 `String` 과 `Lob` 을 모두 받는다.
- 소유: `TiberoLob`·`DbDataReader`(RefCursor) 는 받은 쪽이 `Dispose` 한다. 드라이버 `TiberoRefCursor` 자체는 명령·매개변수 몫(명령을 닫으면 닫힘) — 어댑터는 따로 들고 있지 않는다.

### 2.6 LOB (`TiberoLob : IDisposable`)

| 멤버 | 형식 | 드라이버 쪽 |
|---|---|---|
| `Kind` | `TiberoLobKind`(`Clob, NClob, Blob`) | `TiberoClob.IsNClob` 로 Clob·NClob 구분 |
| `IsNClob` | `bool` | `TiberoClob.IsNClob` |
| `Length` | `long` | `Stream.Length`. CLOB 은 **UCS-2 바이트**(문자 수 = Length/2). 임시 NCLOB 은 0 일 수 있음(그때도 `Read` 는 정상 — 끝은 `Read` 가 0 을 줄 때로 판단) |
| `Position` | `long`(get·set) | `Stream.Position`. CLOB 은 바이트(문자 N = 2N, 홀수면 `ArgumentException`) |
| `Read(char[] buffer, int offset, int count)` | `int` | `TiberoClob.Read(char[], int, int)`(문자). Blob 이면 `NotSupportedException` |
| `Read(byte[] buffer, int offset, int count)` | `int` | `Stream.Read`. Clob 이면 `NotSupportedException`(드라이버는 -1 을 준다) |
| `Clone()` | `TiberoLob` | `TiberoClob.Clone()`/`TiberoBlob.Clone()`(새 사본은 `Position=0`, 따로 `Dispose`) |
| `AsStream()` | `Stream` | Blob 의 드라이버 스트림 그대로(소유는 이 `TiberoLob`, 따로 닫지 않음). Clob 이면 `NotSupportedException` |
| `Dispose()` | | 드라이버 객체 `Dispose` |

- `ReadClob`·`ReadBlob`·`ConvertProviderValue` 는 부를 때마다 드라이버에서 새 로케이터를 받아 새 `TiberoLob` 을 만든다(둘이 한 드라이버 객체를 나누지 않음). 받은 쪽이 `Dispose` 한다.
- `TiberoLob` 은 스트림이라 스레드 안전하지 않다(한 번에 한 스레드). 어댑터(`TiberoDriver`)만 스레드 안전.
- 리더가 다음 행으로 넘어가도 앞 행의 로케이터는 읽힌다(values 4장). 리더를 닫은 뒤에도 연결이 열려 있으면 읽힌다(DV6 실측, win144 #11). 연결을 닫은 뒤는 재지 않았다 → 연결이 열려 있는 동안만 쓴다.

### 2.7 예외

| 멤버 | 형식 | 드라이버 쪽 | 쓰는 곳 |
|---|---|---|---|
| `TryReadError(Exception ex, out TiberoErrorInfo error)` | `bool` | 이 예외 자체만 본다(안쪽은 호출자가 훑음). 서버 오류 `Tibero.DataAccess.Client.TiberoException.Number`(`DbException`), 드라이버 쪽 오류 `tbcomm.err.TbOdpException.ErrorCode`(`System.Exception`) | tb-session S4 `TiberoErrorClassifier`(지금의 `TiberoDriverErrors.TryRead` 를 대신), DV5 |

| `TiberoDriver.TryReadAnyError(Exception ex, out TiberoErrorInfo error)` | `static bool` | 이 프로세스에서 묶인 모든 어댑터(앱 `Shared` + 시험 가짜)에 `TryReadError` 를 묻는다. 묶인 것이 없으면 false | 어댑터를 받지 않는 정적 분류(`TiberoErrorClassifier.FromException`·`ToDbError`·`IsDriverException`, DV5) |

`TiberoErrorInfo`(readonly record struct): `int Number`(드라이버 값 그대로, 보통 음수 — 부호 없애기는 분류기 몫: `TiberoErrorClassifier.Classify` 가 `Math.Abs`), `string Message`, `TiberoErrorOrigin Origin`(`Server` = `TiberoException`, `Client` = `TbOdpException`. 번호 범위가 달라 분류기가 나눠 본다, session 7장).

오류 위치(줄·열)는 드라이버 멤버가 없다(`TiberoError` 에 `ParseErrorOffset` 없음). 메시지의 `at line L, column C` 와 `^` 줄을 `TiberoErrorClassifier`(S4)가 해석한다 — 어댑터 멤버가 아니다.

### 2.8 일부러 두지 않은 멤버

| 드라이버 멤버 | 이유 |
|---|---|
| `TiberoConnection.AutoCommit` | 없음. 수동 커밋 = `BeginTransaction()`, 커밋·롤백 = SQL 문장(exec 1장) |
| `TiberoConnection.ModuleName`·`ActionName`·`ClientInfo`·`ClientId` | `ModuleName`·`ActionName`·`ClientInfo` 는 `V$SESSION` 에 반영 안 됨(session 5장). `ClientId` 는 반영되지만 필요 없음 — 세션 식별은 넷 다 `DBMS_APPLICATION_INFO`·`DBMS_SESSION` 블록 하나로 넣는다(Oracle 과 같음) |
| `TiberoCommand.FetchSize`·`RowSize`, 리더 `FetchSize` | 1.1.35 에서 효과 없음(values 1장) |
| `CommandTimeout` | 기본 형식에 있음. 드라이버가 지키지 않아 토큰 + `Cancel()` 로 건다(exec 3장) |
| `TiberoException.Errors` | 요청 없음. 필요하면 요청 |

## 3. 로더(DV2)

| 부품 | 일 |
|---|---|
| `TiberoDriverLocator` | 경로 해석: `ORANDA_TIBERO_DRIVER` → `IDriverPathSource.GetPath(Dbms.Tibero)`. 파일이면 그 dll, 폴더면 그 안 `Tibero.DataAccess.dll` → 없으면 `lib/net5.0/Tibero.DataAccess.dll`. `.nupkg` 는 `lib/net5.0/Tibero.DataAccess.dll` 을 `%LOCALAPPDATA%/Oranda/drivers/tibero/<sha256>/` 에 풀어 그 파일을 쓴다 |
| `TiberoDriverLoader.Inspect(path)` | `System.Reflection.Metadata` `PEReader` 로 메타데이터만 읽는다(올리지 않음, 파일을 열어 둔 채 두지 않음). 경로는 앞뒤 공백·따옴표를 걷고 `~`·`%VAR%` 를 펼친다. 관리 코드(ILOnly 아니면 `WrongAssembly`) → 어셈블리 이름이 정확히 `Tibero.DataAccess`(아니면 `WrongAssembly`, `Oracle.DataAccess` 포함) → `mscorlib` 참조면 `FrameworkBuild`(「.NET Framework 용 TDP.NET(2.x)은 쓸 수 없습니다. TDP.NET Core 1.1.x 가 필요합니다.」) → 메이저 1 이 아니면 `WrongAssembly` → 1.1.35 = `Ok`, 그 밖 1.x = `Unverified`(「시험하지 않은 버전」 경고, 허용) |
| `TiberoDriverLoader.Load(AssemblyLoadContext alc, string path)` | `alc.LoadFromAssemblyPath` 한 번 + `alc.Resolving` 예비 처리기: 드라이버가 직접 참조하는 이름만, 드라이버 폴더의 같은 이름·요청 이상 버전 파일로 찾아 준다(앱의 다른 실패에 끼어들지 않음). 한 컨텍스트에 한 폴더 → `TiberoDriver.Bind`. 앱은 `AssemblyLoadContext.Default`, 시험은 새 ALC |
| `TiberoDriverStatus` | `IDbmsDriverStatus`(Core, DU0) 구현, `[OrandaService(typeof(IDbmsDriverStatus), Dbms = Dbms.Tibero)]`. `Check(path)`·`Current`·`ConfiguredPath`·`RestartRequired`. 지금의 `TiberoDriverInfo` 를 대신한다 |

검사 결과 형식 = Core `DriverCheck(State, Version, Path, Reason)`, `DriverState { Ok, Missing, NotFound, WrongAssembly, FrameworkBuild, Unverified, Unusable }`(DU0. `Unusable` = 검사는 통과했으나 올린 뒤 멤버 묶기 실패, 이 문서가 DU0 에 추가 요청). 3장 부품은 모두 `src/Oranda.Tibero/Driver/` 에 둔다.

## 4. 경계 규칙(모든 레인)

1. 드라이버 형식 이름(`TiberoConnection`·`TiberoCommand`·`TiberoParameter`·`TiberoDataReader`·`TiberoException`·`TbOdpException`·`TiberoDecimal`·`TiberoTimeStamp*`·`TiberoClob`·`TiberoBlob`·`TiberoRefCursor`·`INullable` 등)과 `TiberoDbType` 값 이름, 형식 이름 문자열·리플렉션·컴파일된 식은 `src/Oranda.Tibero/Driver/` 에만 둔다.
2. `Sessions`·`Execution`·`Results`·`Grid`·`Connection` 은 `System.Data.Common`(`DbConnection`·`DbCommand`·`DbParameter`·`DbDataReader`·`DbException`)과 2장의 어댑터 멤버만 쓴다. 예외 형식 판정도 `TryReadError` 로 한다(형식 이름 비교 금지).
3. `TiberoDriver.Require(paths)` 는 세션 관리자와 접속 시험만 부른다. Tibero 세션 형식(tb-session 소유)은 연 때 받은 어댑터를 `internal TiberoDriver Driver { get; }` 로 들고 있고, 실행·결과 코드는 거기서 받아 쓴다. 정적 도우미(`TiberoValues`·`TiberoBindBuilder` 등)는 `TiberoDriver` 를 첫 인자로 받는다. 정적 `Require(paths)` 를 여기저기서 부르지 않는다 → 시험이 가짜 어댑터를 넘길 수 있다.
4. `[OrandaService]` 생성자·정적 초기화에서 드라이버를 읽지 않는다(시작 때 안 읽음).
5. 어댑터 인스턴스는 바뀌지 않고 여러 스레드에서 써도 된다. 드라이버의 `*Async` 는 동기로 돈다 → 호출자가 `Task.Run` 으로 옮긴다(exec 4장).
6. 아키텍처 시험(기계로 막는 것): `Tibero.DataAccess` 어셈블리·패키지 참조 없음(csproj·props 와 `Oranda.Tibero` 의 참조 어셈블리 목록), 드라이버 이름공간 `Tibero.DataAccess.Client`·`Tibero.DataAccess.Types`·`tbcomm.` 이 `src/**/*.cs` 의 코드(주석 뺀 줄)에서 `src/Oranda.Tibero/Driver/` 밖에 없음(`"Tibero.DataAccess.dll"` 같은 파일 이름은 걸리지 않음), 빌드 출력에 `Tibero.DataAccess.dll` 없음, `src/**` 가 `Oranda.Tibero.FakeDriver` 를 참조하지 않음, `TIBERO_DRIVER` 기호가 쓰이지 않음. 1번의 형식 이름(이름공간 없이 쓴 `TiberoDbType` 값 이름 등)은 기계로 다 막지 못한다 → 리뷰로 본다.

## 5. 시험(DV4·DV6)

- 가짜 드라이버 `tests/Oranda.Tibero.FakeDriver`: 어셈블리 이름 `Tibero.DataAccess`, 버전 1.1.35, 2장이 묶는 형식·멤버를 같은 이름공간·이름으로 둔다. 동작은 최소(연결 열기·닫기·상태, 명령 속성 기록, 시험이 넣은 처리기의 결과·예외를 돌려줌). 시험 프로젝트는 `ReferenceOutputAssembly=false` 로 빌드만 하고 출력 하위 폴더(`fake-driver/`)에 둔다.
- 시험 도우미 `tests/Oranda.Tibero.Tests/Driver/FakeDriverHost.cs`(이름공간 `Oranda.Tibero.Tests.Driver`): `FakeDriverHost.Driver` = 새 ALC 에 가짜를 읽어 `Bind` 한 어댑터(시험 프로세스에 하나). 세션·실행·결과 단위 시험도 이것을 쓴다.
  - `SetHandler(Func<DbCommand, object?>? handler)`: 명령 실행 결과를 정한다. `ExecuteReader` = 처리기가 돌려준 `DataTable` 위 리더(없으면 빈 것), `ExecuteNonQuery` = 돌려준 `int`(아니면 -1), `ExecuteScalar` = 돌려준 값. 처리기가 던지면 그대로 나간다. OUT 값은 처리기 안에서 `DbParameter.Value` 에 넣는다. 처리기는 비동기 흐름마다 따로(AsyncLocal)라 다른 시험과 병렬로 돌아도 섞이지 않는다 — 시험 메서드 본문에서 부른다.
  - `ServerError(int number, string message)` = 서버 오류(`TiberoException`, `Number` 그대로), `ClientError(int code, string message)` = 드라이버 쪽 오류(`TbOdpException`). `Reset()` = 처리기 빼기.
  - 가짜 값 만들기: `New(fullName, args…)`·`Static(fullName, field)`·`Get(target, property)`·`NewReader(DataTable)`·`TypeOf(fullName)`(드라이버 형식 이름 문자열은 시험 쪽 `Driver/` 폴더에서만 쓴다).
- 프로세스 단위 상태는 `TiberoDriverHost`(앱 = `TiberoDriverHost.Shared`, 기본 ALC). 시험은 `new TiberoDriverHost(새 ALC, 환경 변수 함수, 풀기 폴더)` 로 따로 만든다.
- DV6 실측(win144 #11, Tibero 6.2.15, 드라이버 위치 = dll·nupkg 둘 다 같은 결과): 실제 1.1.35 검사 Ok·2장 모든 멤버 묶임(nupkg 도 로케이터가 풂). 2.70(.NET Framework 판) 거절. 같은 칸 `ReadClob` 두 번 = 서로 독립(`Clone()` 불필요), 리더 닫은 뒤(연결 열림) 로케이터 읽힘. NUMBER 28자리 초과 `Decimal` OUT = 드라이버 안 `OverflowException` → `Varchar2` OUT 이면 글자 그대로(2.5 `TiberoDecimal` 행, 결정40). NUMBER 는 유효 숫자 38자리까지라 39자리 이상은 서버가 반올림한다(win144 #13, 결정43 — 시험 기대값 = 38자리).
- 메모리 PE 거절·판정: 이름 다름·`Oracle.DataAccess`·`mscorlib` 참조·메이저 2·`Unverified`(1.2.0 등)·없는 경로·텍스트 파일·폴더·nupkg. 버전별 판정은 메모리에서 만든 PE 로 보고, 가짜 드라이버는 1.1.35 하나만 둔다. 멤버 하나가 빠진 경우(`Unusable`)는 가짜 드라이버 어셈블리를 묶을 때 형식을 일부 빼는 시험용 바인드 옵션 또는 멤버 없는 메모리 어셈블리로 본다(DV4).
- 실제 드라이버 IT: `ORANDA_TIBERO_DRIVER` = 실제 1.1.35 dll 일 때만. 2장의 모든 멤버가 묶이는지(핵심), 2.70 거절(파일이 있을 때만).
