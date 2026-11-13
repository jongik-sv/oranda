using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Rollback;

/// <summary>공급자가 엔진에 넘기는 문장 하나(설계 13.5).</summary>
/// <param name="Session">실행 세션</param>
/// <param name="Statement">치환 변수를 처리한 문장</param>
/// <param name="Binds">사용자 바인드</param>
/// <param name="Options">실행 옵션(<see cref="ExecutionOptions.Recording"/>·출처)</param>
public sealed record RollbackRequest(ISession Session, SqlStatement Statement,
                                     IReadOnlyDictionary<string, BindValue> Binds, ExecutionOptions Options)
{
    /// <summary>공급자가 <see cref="IDmlAnalyzer"/> 로 채운다(<c>RowDmlBuilder</c> 가 준 <see cref="SqlStatement.Dml"/> 이 있으면 그것).</summary>
    public DmlShape? Shape { get; init; }
}

/// <summary>
/// 원복 스크립트 캡처 엔진(설계 6.1). 구현: <c>Oranda.Core.Rollback.Capture.RollbackCaptureEngine</c>(RB-02).
/// 끔 구현은 <see cref="RollbackCapture.Disabled"/>.
/// </summary>
public interface IRollbackCapture
{
    /// <summary>정책 관문 앞, 명령 잠금 밖. 실패해도 예외 없이 NotApplicable(Failed). 사용자 취소만 전파.</summary>
    Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct);

    /// <summary>
    /// 명령 잠금 안, 사용자 문장 직전. 층 B 시점 읽기와 0·1단계.
    /// 이 메서드가 <c>SAVEPOINT ORANDA$RB</c>·잠금 읽기를 보낸 뒤 실패하거나 취소되면, 엔진이 스스로 <c>ROLLBACK TO SAVEPOINT ORANDA$RB</c> 를 보내
    /// 잠금을 푼 뒤 끝낸다(설계 6.2). 사용자 취소는 그 뒤 <see cref="OperationCanceledException"/> 으로 전파하고, 그 밖의 실패는 받은 문장을 그대로 보내는 캡처를 돌려준다.
    /// 공급자는 캡처를 받지 못했을 때 대신 <c>ROLLBACK TO</c> 를 보내지 않는다(이름이 같은 앞 문장의 저장점으로 되돌아가 성공한 변경을 지운다).
    /// </summary>
    Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer,
                                       IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct);

    /// <summary>「모름」 끝 판정(7.3). 명령 잠금 안. 표본이 없으면 Unknown.</summary>
    Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct);

    /// <summary>끝난 직후 시점 읽기(커밋 시점). 실패하면 null.</summary>
    Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct);
}

/// <summary>문장 하나의 캡처(설계 6.1 의 5~8단계). <see cref="IRollbackCapture.BeginAsync"/> 가 만든다.</summary>
public interface IStatementCapture
{
    /// <summary>드라이버로 보낼 문장. 한 행 INSERT 면 <c>RETURNING ROWID INTO :ORANDA_RID</c> 가 붙은 글이다.</summary>
    SqlStatement StatementToSend { get; }

    /// <summary>드라이버로 보낼 바인드(붙인 출력 바인드 포함).</summary>
    IReadOnlyDictionary<string, BindValue> BindsToSend { get; }

    /// <summary>사용자 문장 성공 뒤(2~3단계와 원장 기록). 내부 실패는 Incomplete 로 담고 던지지 않는다.</summary>
    Task<RollbackStatementSummary> CompleteAsync(long? rowsAffected, IReadOnlyDictionary<string, object?> outValues,
                                                 string? transactionId, CancellationToken ct);

    /// <summary>
    /// 사용자 문장 실패·취소 뒤. 이번 <see cref="IRollbackCapture.BeginAsync"/> 가 건 내부 저장점이 있을 때만 그리로 되돌려 잠금을 푼다.
    /// 저장점을 걸지 않은 문장(한 행 INSERT, 시점만 기록한 문장, 원복 스크립트를 만들지 않은 문장)에서는 아무것도 보내지 않는다:
    /// 같은 이름의 앞 문장 저장점으로 되돌아가면 앞 문장의 성공한 변경이 지워진다. ct 는 None 으로 부른다.
    /// </summary>
    Task<RollbackStatementSummary> AbandonAsync(DbError? failure, CancellationToken ct);
}

/// <summary>실행 결과에 싣는 원복 요약(<see cref="ExecutionResult.Rollback"/>, 설계 10.2).</summary>
public sealed record RollbackStatementSummary(RollbackScriptState State, NotCreatedReason Reason, int StepCount)
{
    public static RollbackStatementSummary None { get; } = new(RollbackScriptState.NotCreated, NotCreatedReason.None, 0);

    public ChangePoint? BeforePoint { get; init; }

    /// <summary>실행 로그 한 줄. null = 남기지 않음(SCN 만 기록한 문장).</summary>
    public string? LogLine { get; init; }

    /// <summary>경고색 줄(예: 행 수 다름).</summary>
    public string? Warning { get; init; }

    /// <summary>문장 끝에 <c>RETURNING ROWID INTO</c> 를 붙여 보냈다(RD-1).</summary>
    public bool ReturningAppended { get; init; }
}
