using Oranda.Core.Events;

namespace Oranda.Core.Rollback;

/// <summary>트랜잭션이 끝났다(설계 7.3). 공급자가 <see cref="IRollbackLedger.EndAsync"/> 로 넘긴 뒤 같은 값을 화면 알림용으로 낸다.</summary>
public sealed record TransactionEnded(Guid SessionId, TransactionEndKind Kind, TransactionEndSource Source) : OrandaEvent
{
    /// <summary><see cref="TransactionEndKind.RollbackToSavepoint"/> 의 저장점 이름.</summary>
    public string? Savepoint { get; init; }

    /// <summary>끝난 직후 시점(커밋·모름일 때). 못 읽으면 null.</summary>
    public ChangePoint? CommitPoint { get; init; }
}

/// <summary>문장 하나를 원장에 기록했다.</summary>
public sealed record RollbackStatementCaptured(RollbackStatementRecord Record) : OrandaEvent;

/// <summary>묶음을 저장했다(변경 일지 새로 고침).</summary>
public sealed record RollbackGroupSaved(long GroupId, RollbackScriptState State) : OrandaEvent;
