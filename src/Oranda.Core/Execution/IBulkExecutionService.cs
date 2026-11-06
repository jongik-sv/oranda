using Oranda.Core.Sessions;

namespace Oranda.Core.Execution;

/// <summary>
/// 대량 실행 경로(접속 간 데이터 복사, 설계 6.2). 문장마다 정책을 한 번 평가한 뒤 행 묶음을 바인드 배열로 실행한다.
/// 정책 단계는 <see cref="IExecutionService"/> 와 같다. 치환 변수 처리는 하지 않는다(호출한 쪽이 만든 SQL 이다).
/// </summary>
public interface IBulkExecutionService
{
    /// <summary>
    /// 정책을 한 번 평가한다. 실효 분류가 Query 가 아니면 행을 내지 않고 <see cref="BulkDeniedException"/>.
    /// 값: CLOB·NCLOB → string(전체), BLOB·RAW → byte[](전체), 그 밖은 손실 없는 ODP.NET 값(OracleDecimal, OracleDate,
    /// OracleTimeStamp*, OracleIntervalDS/YM, float, double, string). <see cref="ExecuteArrayAsync"/> 가 그대로 받는다.
    /// </summary>
    IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                ExecutionOptions options, CancellationToken ct);

    /// <summary>같은 SQL 텍스트에 정책을 한 번 평가하고, <paramref name="rows"/> 를 ArrayBindCount 로 한 번에 실행한다.</summary>
    /// <param name="columns">바인드 순서대로의 열(이름·서버 형식). 행 배열의 열 순서와 같다.</param>
    Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns,
                                       IReadOnlyList<object?[]> rows, ExecutionOptions options, CancellationToken ct);
}

/// <summary>바인드 배열의 열 하나. <see cref="ServerType"/> = <c>ColumnInfo.DataType</c>(예: <c>TIMESTAMP(6)</c>).</summary>
public sealed record BulkColumn(string BindName, string ServerType);

/// <summary>바인드 배열 실행 결과. 실패하면 <see cref="Error"/>, 거부·확인을 거쳤으면 <see cref="Policy"/> 가 있다.</summary>
public sealed record BulkResult(ExecutionStatus Status, long RowsAffected)
{
    public DbError? Error { get; init; }

    public PolicyDecision? Policy { get; init; }
}

/// <summary><see cref="IBulkExecutionService.StreamRowsAsync"/> 가 정책 거부·Query 아닌 문장을 받았을 때.</summary>
public sealed class BulkDeniedException(PolicyDecision decision) : Exception(decision.Message)
{
    public PolicyDecision Decision { get; } = decision;
}
