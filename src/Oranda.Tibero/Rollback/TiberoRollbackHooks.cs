using Oranda.Core.Events;
using Oranda.Core.Rollback;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Rollback;

/// <summary>
/// 세션 API 의 커밋·롤백(<see cref="TiberoSession.CommitAsync"/>·<see cref="TiberoSession.RollbackAsync"/>)이 원장에 끝을 알리는 자리(설계 7.3·7.4·13.5 끝).
/// 명령 잠금을 쥔 채 부른다(같은 세션의 다음 문장 기록보다 끝이 먼저 처리되게 기다린다). 원장·엔진 실패는 커밋·롤백을 막지 않는다.
/// </summary>
internal sealed class TiberoRollbackHooks(IRollbackLedger ledger, IRollbackCapture capture, IEventBus events)
{
    /// <summary>커밋 문장 앞(7.4): 열린 묶음에 바뀐 것이 있으면 원장이 「열림」으로 저장한다. 롤백 앞에서는 부르지 않는다.</summary>
    public async Task BeforeCommitAsync(TiberoSession session, CancellationToken ct)
    {
        try
        {
            await ledger.BeforeEndAsync(session.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 원장은 저장 실패를 삼키는 계약이다. 그래도 던지면(원장 안 제한 시간 포함) 커밋은 그대로 한다. 사용자 취소만 전파한다.
        }
    }

    /// <summary>
    /// 커밋·롤백 문장 뒤: 커밋이면 끝난 직후 시점을 읽고(열린 묶음이 있을 때만, 왕복을 늘리지 않게), 원장 <see cref="IRollbackLedger.EndAsync"/> 를 기다린다.
    /// 돌려준 <see cref="TransactionEnded"/> 는 부른 쪽이 명령 잠금을 놓은 뒤 <see cref="Publish"/> 한다(구독자가 같은 세션에 명령을 보내도 막히지 않게).
    /// </summary>
    public async Task<TransactionEnded> AfterEndAsync(TiberoSession session, TransactionEndKind kind)
    {
        ChangePoint? point = null;
        try
        {
            if (kind == TransactionEndKind.Commit && ledger.Open(session.Id) is not null)
            {
                point = await capture.ReadPointAsync(session.Id, TiberoRollbackChannel.Locked(session), TiberoRollbackDialect.Instance, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            point = null; // 시점 읽기 실패: 커밋 시점 칸만 비운다(14장).
        }

        var ended = new TransactionEnded(session.Id, kind, TransactionEndSource.SessionApi) { CommitPoint = point };
        try
        {
            await ledger.EndAsync(ended, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 원장 저장 실패는 커밋·롤백 결과를 바꾸지 않는다.
        }

        return ended;
    }

    /// <summary>화면 알림용 이벤트(원장 처리 뒤, 명령 잠금 밖).</summary>
    public void Publish(TransactionEnded ended) => events.Publish(ended);
}
