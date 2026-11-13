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

// 이 파일의 원장 이벤트는 공급자가 명령 잠금을 쥔 채 부른 원장 메서드 안에서 난다. 구독자는 같은 세션에 동기 명령을 보내지 않고
// 비동기로(화면은 UI 스레드에 Post) 처리한다(IRollbackLedger 주석).

/// <summary>문장 하나를 원장에 기록했다.</summary>
public sealed record RollbackStatementCaptured(RollbackStatementRecord Record) : OrandaEvent;

/// <summary>묶음을 저장했다(변경 일지 새로 고침).</summary>
public sealed record RollbackGroupSaved(long GroupId, RollbackScriptState State) : OrandaEvent;
