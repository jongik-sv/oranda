namespace Oranda.Core.Grid;

/// <summary>
/// 질의 결과(TRD 4.4). 커서는 앞으로만 가므로 그리드가 페이지 단위로 요청하고 캐시한다.
/// 구현: Oranda.Oracle(TSK-06-01). FetchSize = RowSize × 페이지 크기.
/// </summary>
public interface IResultSet : IAsyncDisposable
{
    IReadOnlyList<ResultColumn> Columns { get; }

    /// <summary>
    /// 0부터 세는 페이지. 아직 안 가져온 페이지면 그 앞까지 차례로 가져온다.
    /// 끝을 넘으면 <see cref="ResultPage.RowCount"/> 0 인 마지막 페이지.
    /// </summary>
    Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct);

    /// <summary>커서 끝까지 가져왔으면 true.</summary>
    bool IsComplete { get; }

    /// <summary>끝까지 가져온 뒤의 전체 행 수. 그 전에는 null.</summary>
    long? KnownRowCount { get; }

    /// <summary>
    /// 이 결과 집합이 <see cref="GetPageAsync"/> 로 주는 한 페이지의 행 수. 그리드는 페이지 번호를 이 크기로 나누므로 결과 집합이 만들 때 정한 값을 알려야 한다
    /// (설정 <c>grid.pageSize</c> 가 실행과 화면 사이에 바뀌어도 어긋나지 않게). 모르면 null(그리드 기본값).
    /// </summary>
    int? PageSize => null;
}

/// <summary>
/// 결과 가져오기에 쓰는 설정 값(<c>grid.pageSize</c>·<c>grid.initialLobFetchSize</c>). 설정을 바꾸면 다음 실행부터 따라가도록 실행 서비스가
/// 실행할 때마다 읽는다. 구현은 앱이 설정 서비스 위에 둔다(Core 는 설정 서비스를 모른다).
/// </summary>
public interface IGridFetchSettings
{
    /// <summary>한 번에 가져올 행(첫 가져오기와 「더 가져오기」 단위).</summary>
    int PageSize { get; }

    /// <summary>LOB 을 행과 함께 미리 읽을 크기(문자·바이트). 0 이면 미리 읽지 않는다.</summary>
    int InitialLobFetchSize { get; }
}

/// <summary>결과 열 값의 표시 분류(TRD 4.4 「타입 표시」).</summary>
public enum ColumnDataKind
{
    Text,
    Number,
    BinaryFloat,
    Date,
    Timestamp,
    TimestampTz,
    IntervalYearMonth,
    IntervalDaySecond,
    Clob,
    Blob,
    Raw,
    RowId,
    Boolean,
    Json,
    Vector,
    Xml,
    UserDefinedType,
    RefCursor,
    Other,
}

/// <summary>결과 열 1개.</summary>
/// <param name="Ordinal">0부터</param>
/// <param name="Name">별칭이 있으면 별칭</param>
/// <param name="DbTypeName">서버 타입 이름(예: <c>VARCHAR2</c>, <c>NUMBER</c>, <c>VECTOR</c>)</param>
/// <param name="Kind">표시 분류</param>
public sealed record ResultColumn(int Ordinal, string Name, string DbTypeName, ColumnDataKind Kind)
{
    public int? Size { get; init; }

    public int? Precision { get; init; }

    public int? Scale { get; init; }

    public bool IsNullable { get; init; } = true;

    /// <summary>출처 테이블(알 수 있으면, 그리드 편집용).</summary>
    public string? BaseOwner { get; init; }

    public string? BaseTable { get; init; }

    public string? BaseColumn { get; init; }
}

/// <summary>
/// 페이지 1개. 열 지향 버퍼: <c>Columns[열][행]</c>. 값은 원시 값(문자열 변환은 그릴 때).
/// NUMBER 는 정밀도 보존 형식(<see cref="decimal"/> 또는 드라이버 원시 문자열), LOB 은 <see cref="LobValue"/>.
/// </summary>
public sealed record ResultPage(int PageIndex, long FirstRowIndex, int RowCount, IReadOnlyList<IReadOnlyList<object?>> Columns)
{
    /// <summary>이 페이지가 마지막이면 true.</summary>
    public bool IsLast { get; init; }
}
