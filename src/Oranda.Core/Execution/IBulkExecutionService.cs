using Oranda.Core.Sessions;

namespace Oranda.Core.Execution;

/// <summary>
/// 대량 실행 경로(접속 간 데이터 복사, 설계 6.2). 문장마다 정책을 한 번 평가한 뒤 행 묶음을 바인드 배열로 실행한다.
/// 정책 단계는 <see cref="IExecutionService"/> 와 같다. 치환 변수 처리는 하지 않는다(호출한 쪽이 만든 SQL 이다).
/// <para>
/// 트랜잭션을 열거나 끝내지 않는다. 읽기 전용 프로필이라도 문장마다 읽기 전용 트랜잭션을 다시 걸지 않으므로,
/// 원본 세션의 <c>SET TRANSACTION READ ONLY</c> 는 호출한 쪽이 열고 끝낸다(설계 8.1). 커밋·롤백도 호출한 쪽 몫이다.
/// </para>
/// <para>
/// DDL(DROP·CREATE·ALTER)과 SQL*Plus 명령은 받지 않는다(<see cref="ArgumentException"/>). 변경 일지(실행 전 DDL 보관)를 거치는
/// <see cref="IExecutionService"/> 로 보낸다. 읽기 전용 프로필에서는 글이 SET TRANSACTION·COMMIT·ROLLBACK 인 문장 외에는
/// 정책 판단과 상관없이 거부한다. 취소는 <see cref="OperationCanceledException"/>(읽기) 또는 <see cref="ExecutionStatus.Cancelled"/>(실행)로 알린다.
/// </para>
/// </summary>
public interface IBulkExecutionService
{
    /// <summary>
    /// 정책을 한 번 평가한다. 실효 분류가 Query 가 아니거나 글의 첫 낱말이 SELECT·WITH 가 아니면 행을 내지 않고 <see cref="BulkDeniedException"/>.
    /// 값은 손실 없는 DBMS 고유 값이다(T5 결정 Q1): 같은 DBMS 의 <see cref="ExecuteArrayAsync"/> 가 바꾸지 않고 그대로 받는다.
    /// Core 는 값을 직접 해석하지 않고 그 DBMS 의 <c>Oranda.Core.Transfer.Copy.IBulkValueCodec</c> 로만 본다(건수·시퀀스 번호·실패 행 키·마스킹 갈래).
    /// Oracle: CLOB·NCLOB → string(전체), BLOB·RAW → byte[](전체), 그 밖은 ODP.NET 값(OracleDecimal, OracleDate,
    /// OracleTimeStamp*, OracleIntervalDS/YM, float, double, string).
    /// 열거가 끝나거나 열거자를 Dispose 할 때까지 세션 <paramref name="s"/> 를 쥔다(같은 세션의 다른 명령은 기다린다).
    /// 같은 세션을 다시 쓰려면 <c>await foreach</c> 를 끝내거나 빠져나온 뒤에 쓴다. 쓰기는 다른 세션(대상)에 한다.
    /// </summary>
    IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                ExecutionOptions options, CancellationToken ct);

    /// <summary>
    /// 같은 SQL 텍스트에 정책을 한 번 평가하고, <paramref name="rows"/> 를 ArrayBindCount 로 한 번에 실행한다.
    /// 실패하면 <see cref="BulkResult.RowsAffected"/> 는 0 이지만 실패한 행 앞의 행은 열린 트랜잭션에 이미 들어갔을 수 있다:
    /// 한 행씩 다시 넣기 전에 호출한 쪽이 롤백(또는 세이브포인트로 되돌림)한다.
    /// </summary>
    /// <param name="columns">바인드 순서대로의 열(이름·서버 형식). 행 배열의 열 순서와 같다. 비면(바인드 없는 문장) 행 수와 상관없이 한 번 실행한다.</param>
    /// <param name="rows">행마다 <paramref name="columns"/> 수만큼의 값. 열이 있는데 비면 드라이버로 보내지 않고 Succeeded·0.</param>
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
