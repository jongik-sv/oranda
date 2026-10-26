namespace Oranda.Core.Sessions;

/// <summary>
/// 세션 연결 하나를 지키는 명령 잠금(D-041)을 얼마나 기다릴지 정하는 범위. 기본은 「차례가 올 때까지 기다림(취소 가능)」 이다.
/// 자동 완성·백그라운드 갱신처럼 즉시성 기능은 <see cref="Brief"/> 범위 안에서 조회를 시작한다: 그 세션에서 다른 명령(긴 실행 등)이
/// 돌고 있어 잠금을 짧은 시간 안에 못 잡으면 서버에 보내지 않고 <see cref="SessionBusyException"/> 으로 끝난다(호출한 쪽은 캐시만 쓴다).
/// <para>범위는 비동기 흐름(AsyncLocal)을 따라간다: 범위 안에서 시작한 비동기 작업은 끝날 때까지 이 값을 본다.</para>
/// </summary>
public static class SessionWait
{
    /// <summary>즉시성 기능의 기본 기다림. 사람이 알아채지 못할 만큼 짧게.</summary>
    public static readonly TimeSpan DefaultBrief = TimeSpan.FromMilliseconds(100);

    private static readonly AsyncLocal<TimeSpan?> Limit = new();

    /// <summary>지금 흐름의 잠금 기다림 한도. null = 차례가 올 때까지 기다림.</summary>
    public static TimeSpan? CurrentLimit => Limit.Value;

    /// <summary>이 범위 안에서 시작한 세션 명령은 잠금을 <paramref name="limit"/>(기본 <see cref="DefaultBrief"/>)까지만 기다린다.</summary>
    public static IDisposable Brief(TimeSpan? limit = null)
    {
        var previous = Limit.Value;
        Limit.Value = limit ?? DefaultBrief;
        return new Restore(previous);
    }

    private sealed class Restore(TimeSpan? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Limit.Value = previous;
        }
    }
}

/// <summary>세션이 다른 명령을 처리하는 중이라 <see cref="SessionWait.Brief"/> 한도 안에 차례가 오지 않았다. 서버에는 아무것도 보내지 않았다.</summary>
public sealed class SessionBusyException : InvalidOperationException
{
    public SessionBusyException()
        : base("세션이 다른 작업을 처리하는 중입니다. 끝난 뒤 다시 시도하세요.")
    {
    }

    public SessionBusyException(string message)
        : base(message)
    {
    }

    public SessionBusyException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
