using System.Diagnostics;

namespace Oranda.Oracle.Sessions;

/// <summary>
/// 세션·연결 닫기를 호출한 스레드(UI)에서 떼어 내고 기다리는 시간을 제한한다.
/// ODP.NET 의 Close/Dispose 는 서버가 응답하지 않거나 내부 잠금을 쥔 작업이 있으면 끝나지 않으므로(Logoff 의 AcquireConnectionLocks),
/// UI 스레드에서 직접 부르거나 끝까지 기다리지 않는다. 시간이 지나면 기다림만 그만두고 닫기는 백그라운드에서 끝나게 둔다.
/// </summary>
public static class SessionCloser
{
    /// <summary>닫기를 기다리는 최대 시간. 세션·연결을 닫는 모든 경로가 이 값 하나를 쓴다.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <paramref name="dispose"/> 를 스레드 풀에서 실행하고 <paramref name="timeout"/>(기본 <see cref="Timeout"/>) 까지만 기다린다.
    /// 제한 시간을 넘기면 <paramref name="warn"/>(기본 Trace 경고)에 알리고 돌아온다. 이때 닫기는 백그라운드에서 계속되고, 그 예외는 관찰만 한다.
    /// 제한 시간 안에 실패한 예외는 그대로 던진다. 제한 시간 안에 끝났으면 true, 기다림을 그만뒀으면 false 를 돌려준다.
    /// </summary>
    public static async Task<bool> RunAsync(Func<ValueTask> dispose, string what, Action<string>? warn = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        var closing = Task.Run(() => dispose().AsTask());
        try
        {
            await closing.WaitAsync(timeout ?? Timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            _ = closing.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            var message = $"{what} 닫기가 {(timeout ?? Timeout).TotalSeconds:0.#}초 안에 끝나지 않아 기다림을 그만둡니다. 연결은 버리고 백그라운드에서 마저 닫습니다.";
            if (warn is null)
            {
                Trace.TraceWarning(message);
            }
            else
            {
                warn(message);
            }

            return false;
        }
    }
}
