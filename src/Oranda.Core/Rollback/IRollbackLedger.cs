namespace Oranda.Core.Rollback;

/// <summary>세션의 열린 묶음 요약(커밋 전 경고, 설계 10.3).</summary>
public sealed record RollbackOpenState(int Statements, int Steps, bool HasMismatch, bool HasIncomplete);

/// <summary>
/// 원장(설계 7.3, RD-6·RD-7): 세션마다 열린 묶음 하나를 들고, 문장 기록·저장점·트랜잭션 끝을 받아 상태를 바꾼다.
/// 구현: <c>Oranda.Core.Rollback.Ledger.RollbackLedger</c>(RB-04). 끔 구현은 <see cref="DisabledRollbackLedger"/>.
/// 공급자는 메서드를 이벤트가 아니라 직접 불러 기다린다(같은 세션의 기록과 끝의 순서 보장).
/// </summary>
public interface IRollbackLedger
{
    void Record(RollbackStatementRecord record);

    /// <summary><c>SAVEPOINT 이름</c> 성공. 내부 저장점 <c>ORANDA$RB</c> 는 알리지 않는다.</summary>
    void MarkSavepoint(Guid sessionId, string name);

    /// <summary>열린 묶음 요약. 열린 묶음이 없으면 null.</summary>
    RollbackOpenState? Open(Guid sessionId);

    /// <summary>열린 묶음의 끝 판정 표본(최대 <paramref name="max"/> 개).</summary>
    IReadOnlyList<RollbackProbe> Probes(Guid sessionId, int max = 5);

    /// <summary>끝낼 수 있는 문장 앞(7.4). 바뀐 것이 있으면 열린 묶음을 Open 으로 저장한다. 저장 실패는 삼킨다.</summary>
    Task BeforeEndAsync(Guid sessionId, CancellationToken ct);

    /// <summary>끝 처리. 저장한 뒤 RollbackGroupSaved 를 낸다. 같은 세션의 Record 와 순서가 보장되게 공급자가 기다린다.</summary>
    Task EndAsync(TransactionEnded ended, CancellationToken ct);

    /// <summary>원복 실행·비교 반영이 만든 반대 문장 묶음을 바로 확정 전 묶음으로 둔다(RD-8). 그 세션의 다음 끝을 따른다.</summary>
    void RecordDerived(Guid sessionId, RollbackOrigin origin, long originGroupId, RollbackStatementRecord record);
}
