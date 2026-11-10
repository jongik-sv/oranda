using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 대량 읽기(<see cref="IBulkExecutionService.StreamRowsAsync"/>)가 내는 DBMS 고유 값의 해석(T5 결정 Q1). 값은 바꾸지 않고 그대로 같은 DBMS 의
/// 배열 DML 로 넘기며, Core 가 값을 들여다봐야 할 때(건수·시퀀스 번호·카탈로그 글자·실패 행 키)만 이것을 쓴다.
/// 서버 형식 이름은 <c>ColumnInfo.DataType</c> 그대로다(정밀도·길이 괄호가 붙어도 된다).
/// 구현은 DBMS 어셈블리에 두고 그 DBMS 의 하위 형식(예: Oracle 사전 점검)이 생성자에서 넘긴다. 키 서비스가 아니다.
/// </summary>
public interface IBulkValueCodec
{
    /// <summary>대량 실행 경로(복사)가 다루는 형식인가. 아니면 그 열은 복사하지 않는다(사전 점검의 <c>UnsupportedType</c>).</summary>
    bool IsSupported(string serverType);

    /// <summary>마스킹·고정값이 보는 값 갈래. 지원하지 않는 형식이면 <see cref="NotSupportedException"/>.</summary>
    BulkKind KindOf(string serverType);

    /// <summary>키 열 형식 → 다시 읽기 바인드 형식(<see cref="FromKeyText"/> 값을 그대로 싣는다). 키로 쓸 수 없는 형식이면 <see cref="NotSupportedException"/>.</summary>
    BindDataType BindTypeOf(string serverType);

    /// <summary>키 값 → 실패 행 기록 글자. NULL 이면 null. 키로 쓸 수 없는 형식이면 <see cref="NotSupportedException"/>.</summary>
    string? ToKeyText(object? value, string serverType);

    /// <summary>실패 행 기록 글자 → 키 값(대량 읽기와 같은 DBMS 고유 형식). null 이면 null.</summary>
    object? FromKeyText(string? text, string serverType);

    /// <summary>숫자 결과 → long. NULL 이면 null, long 범위를 넘으면 끝값으로 자른다. 소수는 버린다.</summary>
    long? ToInt64(object? value);

    /// <summary>카탈로그 조회 값 → 글자. NULL 이면 null. 숫자는 그 DBMS 드라이버의 글자 그대로다.</summary>
    string? ToText(object? value);

    /// <summary>
    /// 숫자 결과(건수·최대값·시퀀스 번호)면 그 DBMS 드라이버의 글자, NULL 이거나 그 DBMS 의 숫자 값이 아니면 null. 실행기의 「숫자인가」 판정이다
    /// (<see cref="ToText"/> 와 달리 글자·다른 형식은 숫자로 보지 않는다).
    /// </summary>
    string? NumberText(object? value);

    /// <summary>숫자 결과가 0 인가. NULL·숫자 아님 = false.</summary>
    bool IsZero(object? value);

    /// <summary>
    /// 숫자 결과(<see cref="NumberText"/> 가 null 이 아닌 값) → long. long 범위를 넘으면 <see cref="OverflowException"/>
    /// (<see cref="ToInt64"/> 처럼 끝값으로 자르지 않는다: 시퀀스 보정이 「값이 너무 큼」으로 건너뛴다). 숫자가 아니면 <see cref="InvalidCastException"/>.
    /// </summary>
    long ToInt64Exact(object? value);

    // ---- 교차 복사(Oracle↔Tibero) 이식 값. 같은 DBMS 복사는 이 멤버를 부르지 않는다(값 변환을 아예 거치지 않음). ----

    /// <summary>
    /// 이 DBMS 에서 <b>읽어서</b> 이식 값으로 낼 수 있는 형식인가(교차 복사의 원본 쪽 판정). 기본 = <see cref="IsSupported"/>.
    /// 쓰기는 꺼져 있어도 읽기만 되는 형식(Tibero BINARY_*·TIMESTAMP WITH TIME ZONE)이 있어 따로 둔다.
    /// </summary>
    bool IsReadable(string serverType) => IsSupported(serverType);

    /// <summary>
    /// 이 DBMS 의 대량 읽기 값(<paramref name="serverType"/> 열의 값) → 중립 이식 값(<c>Core/Transfer/Copy/CrossDbms/PortableValues</c>).
    /// 매핑: 문자 = string, RAW·BLOB = byte[], CLOB·NCLOB = string, DATE = <see cref="DateTime"/>, NUMBER·FLOAT = <c>PortableNumber</c>,
    /// BINARY_FLOAT·DOUBLE = <c>PortableBinaryFloat</c>·<c>PortableBinaryDouble</c>, TIMESTAMP = <c>PortableTimestamp</c>,
    /// TIMESTAMP WITH TIME ZONE = <c>PortableTimestampTz</c>, INTERVAL = <c>PortableIntervalYm</c>·<c>PortableIntervalDs</c>. NULL 은 null.
    /// 값을 만들 수 없으면(예: NaN 을 NUMBER 로) <c>PortableConversionException</c>(그 행 실패). 읽을 수 없는 형식이면 <see cref="NotSupportedException"/>.
    /// 기본 구현 = <see cref="NotSupportedException"/>(교차 복사를 지원하지 않는 DBMS).
    /// </summary>
    object? ToPortable(object? value, string serverType)
        => throw new NotSupportedException("이 DBMS 는 교차 복사 이식 값을 지원하지 않습니다.");

    /// <summary>
    /// 이식 값 → 이 DBMS 의 대량 쓰기 값(<paramref name="bindType"/> = <c>CrossTypeMap</c> 이 정한 바인드 형식 이름, 예 <c>NUMBER</c>·<c>CLOB</c>).
    /// 값 하나를 못 바꾸면(기원전 날짜·모르는 시간대 지역·범위 밖 숫자) <c>PortableConversionException</c>(그 행 실패). 쓸 수 없는 형식이면 <see cref="NotSupportedException"/>.
    /// 기본 구현 = <see cref="NotSupportedException"/>.
    /// </summary>
    object? FromPortable(object? portable, string bindType)
        => throw new NotSupportedException("이 DBMS 는 교차 복사 이식 값을 지원하지 않습니다.");
}

/// <summary>대량 값의 갈래(<see cref="IBulkValueCodec.KindOf"/>). 문자 열만 마스킹 함수를 걸 수 있고, LOB 는 고정값을 따로 바꾼다.</summary>
public enum BulkKind
{
    /// <summary>VARCHAR2·NVARCHAR2·CHAR·NCHAR 등 길이 있는 문자.</summary>
    Text,

    /// <summary>문자 LOB(CLOB·NCLOB).</summary>
    Clob,

    /// <summary>이진 LOB(BLOB).</summary>
    Blob,

    /// <summary>그 밖의 지원 형식(숫자·날짜·RAW·INTERVAL 등).</summary>
    Other,
}
