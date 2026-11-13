using Oranda.Core.Events;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Rollback;

/// <summary>
/// 세션·실행 서비스가 되돌리기 원장에 트랜잭션 끝을 알리는 자리(설계 7.3·7.4·13.5). 원장 메서드는 이벤트가 아니라 직접 불러 기다리고
/// (같은 세션의 기록과 끝의 순서 보장), 화면 알림용 <see cref="TransactionEnded"/> 는 그 뒤에 낸다(호출한 쪽이 명령 잠금을 쥔 채일 수 있다:
/// 구독자는 같은 세션 API 를 동기로 부르지 않는다).
/// 되돌리기 기능의 실패는 사용자의 커밋·롤백·실행을 막지 않는다(설계 2장): 예외는 삼킨다.
/// </summary>
internal sealed class OracleRollbackHooks
{
    /// <summary>끝난 뒤 판정·커밋 시점 읽기의 제한 시간. 서버가 멈춰도 커밋이 끝나지 않는 일이 없게 한다(못 읽으면 시점 없음·「모름」).</summary>
    internal static readonly TimeSpan AfterEndTimeout = TimeSpan.FromSeconds(10);

    /// <param name="capture">캡처 엔진(판정·시점 읽기). null = 끔</param>
    /// <param name="ledger">원장. null = 끔</param>
    /// <param name="events">알림 이벤트를 낼 버스. null = 내지 않음(시험)</param>
    /// <param name="dialect">방언. null = Oracle</param>
    public OracleRollbackHooks(IRollbackCapture? capture, IRollbackLedger? ledger, IEventBus? events, IRollbackDialect? dialect = null)
    {
        Capture = capture ?? RollbackCapture.Disabled;
        Ledger = ledger ?? DisabledRollbackLedger.Instance;
        Events = events;
        Dialect = dialect ?? OracleRollbackDialect.Instance;
    }

    public IRollbackCapture Capture { get; }

    public IRollbackLedger Ledger { get; }

    public IEventBus? Events { get; }

    public IRollbackDialect Dialect { get; }

    /// <summary>끝낼 수 있는 문장·커밋 앞(7.4, RD-7). 원장이 바뀐 것이 있을 때만 열린 묶음을 저장한다. 사용자 취소 밖의 실패는 삼킨다.</summary>
    public async Task BeforeEndAsync(OracleSession session, CancellationToken ct)
    {
        try
        {
            await Ledger.BeforeEndAsync(session.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 원장 저장 실패가 커밋을 막지 않는다.
        }
    }

    /// <summary>
    /// 트랜잭션 끝을 원장에 넘기고 이벤트를 낸다. 끝은 이미 서버에서 일어났으므로 사용자 취소 없이 끝까지 한다. 열린 묶음이 있고 연결이 살아 있으면
    /// (호출한 쪽이 명령 잠금을 쥐고 있다) 「모름」은 표본으로 판정하고(<see cref="IRollbackCapture.ProbeEndAsync"/>, 7.3),
    /// 커밋·모름이면 끝난 직후 시점(커밋 시점)을 읽는다. 둘 다 <see cref="AfterEndTimeout"/> 안에서만 한다.
    /// </summary>
    public async Task EndAsync(OracleSession session, TransactionEndKind kind, TransactionEndSource source, string? savepoint = null)
    {
        ChangePoint? point = null;
        try
        {
            var live = session.State is not (SessionState.Broken or SessionState.Closed)
                       && source is not (TransactionEndSource.SessionBroken or TransactionEndSource.SessionClosed or TransactionEndSource.AppRestart);
            var open = Ledger.Open(session.Id);
            if (live && open is not null)
            {
                using var limit = new CancellationTokenSource(AfterEndTimeout);
                var channel = OracleRollbackChannel.Locked(session);
                if (RollbackFlow.NeedsProbe(new TransactionEnded(session.Id, kind, source), open))
                {
                    kind = await Capture.ProbeEndAsync(session.Id, channel, limit.Token).WaitAsync(AfterEndTimeout); // 드라이버 취소가 안 먹어도 기다림을 끝낸다
                }

                if (RollbackFlow.ReadsCommitPoint(kind))
                {
                    point = await Capture.ReadPointAsync(session.Id, channel, Dialect, limit.Token).WaitAsync(AfterEndTimeout);
                }
            }
        }
        catch (Exception)
        {
            // 판정·시점 읽기 실패(제한 시간 포함): 끝 종류는 그대로, 시점은 없음.
        }

        var ended = new TransactionEnded(session.Id, kind, source) { Savepoint = savepoint, CommitPoint = point };
        try
        {
            await Ledger.EndAsync(ended, CancellationToken.None);
        }
        catch (Exception)
        {
            // 원장 저장 실패: 묶음은 다음 시작 때 「확인 필요」가 된다(7.4). 이미 끝난 트랜잭션에는 영향이 없다.
        }

        try
        {
            Events?.Publish(ended);
        }
        catch (Exception)
        {
            // 알림 구독자의 실패가 커밋 결과를 바꾸지 않는다.
        }
    }
}
