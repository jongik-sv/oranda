namespace Oranda.Core.Sessions;

/// <summary>시간 상한을 둔 커밋·롤백(<see cref="BoundedTransactionEnd.RunAsync"/>)의 결과.</summary>
public enum TransactionEndOutcome
{
    /// <summary>상한 안에 끝났다.</summary>
    Done,

    /// <summary>상한 안에 실패했다(<see cref="TransactionEndResult.Error"/>).</summary>
    Failed,

    /// <summary>상한 안에 끝나지 않았다(세션 명령 잠금을 다른 명령이 쥐고 있거나 서버가 응답하지 않음). 커밋했는지는 모른다.</summary>
    TimedOut,
}

/// <summary><see cref="BoundedTransactionEnd.RunAsync"/> 의 결과. <see cref="Error"/> 는 <see cref="TransactionEndOutcome.Failed"/> 일 때만 있다.</summary>
public sealed record TransactionEndResult(TransactionEndOutcome Outcome, Exception? Error = null);

/// <summary>
/// 종료·접속 끊기 경로의 커밋·롤백(PHC-3): 세션 명령 잠금을 기다리는 시간까지 넣어 상한 안에서만 기다린다. DBMS 와 무관하다(Oracle·Tibero 가 같게 동작).
/// 다른 명령(멈춘 DDL·library cache 대기 등)이 잠금을 쥐고 있으면 커밋·롤백이 그 뒤에 끝없이 줄 서 앱이 멈춘 것처럼 보였다.
/// 상한이 지나면 토큰을 취소하고(잠금 대기는 그만둔다: 잠금이 나중에 풀려도 늦은 COMMIT 이 몰래 나가지 않게) 기다림도 그만둔다.
/// 이미 서버에 보낸 명령은 백그라운드에서 끝나게 두고 그 예외는 관찰만 한다.
/// </summary>
public static class BoundedTransactionEnd
{
    /// <summary>
    /// <paramref name="session"/> 을 커밋(<paramref name="commit"/>)·롤백하고 <paramref name="limit"/> 까지만 기다린다. 호출한 스레드(UI)에서 떼어 스레드 풀에서 부른다.
    /// 상한이 지나 생긴 취소·드라이버 오류(ORA-01013 등)는 <see cref="TransactionEndOutcome.TimedOut"/> 이다.
    /// </summary>
    public static async Task<TransactionEndResult> RunAsync(ISession session, bool commit, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(session);

        // 토큰 원천은 일이 끝난 뒤 정리한다: 기다림을 먼저 그만둬도 늦게 끝나는 일이 취소된 토큰을 볼 수 있어야 한다.
#pragma warning disable CA2000 // 아래 ContinueWith 가 일이 끝난 뒤 Dispose 한다.
        var cts = new CancellationTokenSource();
#pragma warning restore CA2000
        var token = cts.Token;
        var end = Task.Run(() => commit ? session.CommitAsync(token) : session.RollbackAsync(token), CancellationToken.None);
        _ = end.ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await end.WaitAsync(limit).ConfigureAwait(false);
            return new TransactionEndResult(TransactionEndOutcome.Done);
        }
        catch (TimeoutException)
        {
            try
            {
                cts.Cancel(); // 잠금 대기를 그만두게 한다
            }
            catch (ObjectDisposedException)
            {
                // 그사이 끝났다.
            }

            _ = end.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new TransactionEndResult(TransactionEndOutcome.TimedOut);
        }
#pragma warning disable CA1031 // 커밋·롤백 실패는 어떤 예외든 호출한 쪽이 알리고 고르게 결과로 돌려준다.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new TransactionEndResult(TransactionEndOutcome.Failed, e);
        }
#pragma warning restore CA1031
    }
}
