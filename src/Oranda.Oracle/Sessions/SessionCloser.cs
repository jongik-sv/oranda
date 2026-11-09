using Oranda.Core.Sessions;

namespace Oranda.Oracle.Sessions;

/// <summary>
/// 세션·연결 닫기를 호출한 스레드(UI)에서 떼어 내고 기다리는 시간을 제한한다. 내용은 Core 의 <see cref="TimedClose"/> 가 정본이고(DBMS 무관),
/// 이 클래스는 Oranda.Oracle 안의 기존 호출처를 위해 그대로 넘기기만 한다.
/// ODP.NET 의 Close/Dispose 는 서버가 응답하지 않거나 내부 잠금을 쥔 작업이 있으면 끝나지 않으므로(Logoff 의 AcquireConnectionLocks),
/// UI 스레드에서 직접 부르거나 끝까지 기다리지 않는다.
/// </summary>
public static class SessionCloser
{
    /// <summary>닫기를 기다리는 최대 시간. <see cref="TimedClose.Timeout"/> 과 같은 값이다.</summary>
    public static TimeSpan Timeout => TimedClose.Timeout;

    /// <inheritdoc cref="TimedClose.RunAsync"/>
    public static Task<bool> RunAsync(Func<ValueTask> dispose, string what, Action<string>? warn = null, TimeSpan? timeout = null) =>
        TimedClose.RunAsync(dispose, what, warn, timeout);
}
