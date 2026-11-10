namespace Oranda.Core.Sessions;

/// <summary>
/// 세션 연결 하나를 지키는 명령 잠금(D-041)을 얼마나 기다릴지 정하는 범위. 기본은 「차례가 올 때까지 기다림(취소 가능)」 이다.
/// 자동 완성·백그라운드 갱신처럼 즉시성 기능은 <see cref="Brief"/> 범위 안에서 조회를 시작한다: 그 세션에서 다른 명령(긴 실행 등)이
/// 돌고 있어 잠금을 짧은 시간 안에 못 잡으면 서버에 보내지 않고 <see cref="SessionBusyException"/> 으로 끝난다(호출한 쪽은 캐시만 쓴다).
/// <para>
/// 뒤 갱신처럼 긴 읽기는 <see cref="BriefWithResume"/> 로 「시작 한도」 와 「이어 읽기 한도」 를 따로 준다. 커서를 연 뒤 행·페이지마다 잠금을 다시 잡는
/// 읽기는 그 부분을 <see cref="Resume"/> 범위로 감싼다(이어 읽기 한도가 없으면 아무것도 바꾸지 않는다).
/// </para>
/// <para>범위는 비동기 흐름(AsyncLocal)을 따라간다: 범위 안에서 시작한 비동기 작업은 끝날 때까지 이 값을 본다.</para>
/// </summary>
public static class SessionWait
{
    /// <summary>즉시성 기능의 기본 기다림. 사람이 알아채지 못할 만큼 짧게.</summary>
    public static readonly TimeSpan DefaultBrief = TimeSpan.FromMilliseconds(100);

    private static readonly AsyncLocal<TimeSpan?> Limit = new();

    private static readonly AsyncLocal<TimeSpan?> ResumeLimit = new();

    private static readonly AsyncLocal<SessionHoldMeter?> Meter = new();

    /// <summary>지금 흐름의 잠금 기다림 한도. null = 차례가 올 때까지 기다림.</summary>
    public static TimeSpan? CurrentLimit => Limit.Value;

    /// <summary>
    /// 지금 흐름에서 이미 시작한 읽기를 이어 갈 때(<see cref="Resume"/>)의 잠금 기다림 한도. null = 따로 정하지 않음(<see cref="CurrentLimit"/> 그대로).
    /// </summary>
    public static TimeSpan? CurrentResumeLimit => ResumeLimit.Value;

    /// <summary>이 범위 안에서 시작한 세션 명령은 잠금을 <paramref name="limit"/>(기본 <see cref="DefaultBrief"/>)까지만 기다린다.</summary>
    public static IDisposable Brief(TimeSpan? limit = null)
    {
        var previous = (Limit.Value, ResumeLimit.Value);
        Limit.Value = limit ?? DefaultBrief;
        ResumeLimit.Value = null; // 이어 읽기도 같은 한도(바깥 범위의 이어 읽기 한도를 물려받지 않는다)
        return new Restore(previous);
    }

    /// <summary>
    /// 질의를 시작하는(커서를 여는) 잠금은 <paramref name="start"/> 까지만, 이미 시작한 읽기를 이어 가는 잠금(<see cref="Resume"/> 범위)은
    /// <paramref name="resume"/> 까지만 기다린다. 뒤 갱신용(D-041): 시작은 0 으로 두어 사용자의 실행(여러 문장 스크립트·결과 페이지 넘김 사이 빈틈)
    /// 뒤에 줄을 서지 않고, 시작한 긴 읽기는 짧은 질의 하나에 끊기지 않게 한다.
    /// </summary>
    public static IDisposable BriefWithResume(TimeSpan start, TimeSpan resume)
    {
        var previous = (Limit.Value, ResumeLimit.Value);
        Limit.Value = start;
        ResumeLimit.Value = resume;
        return new Restore(previous);
    }

    /// <summary>
    /// 이미 시작한 읽기를 이어 가는 범위(커서를 연 뒤 행·페이지마다 잠금을 다시 잡는 곳). 바깥에 <see cref="BriefWithResume"/> 가 있으면
    /// 그 이어 읽기 한도로 기다리고, 없으면 아무것도 바꾸지 않는다(기본 기다림·<see cref="Brief"/> 한도 그대로).
    /// </summary>
    public static IDisposable Resume()
    {
        if (ResumeLimit.Value is not { } resume)
        {
            return NoChange.Instance;
        }

        var previous = (Limit.Value, ResumeLimit.Value);
        Limit.Value = resume;
        return new Restore(previous);
    }

    /// <summary>
    /// 이 범위 안에서 세션 잠금(<c>SessionCommandGate</c>)을 쥔 시간의 합을 잰다(기다린 시간은 빼고 서버에 명령을 보낸 시간만).
    /// 다 쓰면 Dispose 한다. 범위 안의 흐름이 잠금을 한 번도 잡지 않았으면 <see cref="SessionHoldMeter.Entered"/> 가 false 다.
    /// </summary>
    public static SessionHoldMeter MeasureHold()
    {
        var meter = new SessionHoldMeter(Meter.Value);
        Meter.Value = meter;
        return meter;
    }

    /// <summary>세션 잠금을 잡았다(잠금이 부른다).</summary>
    internal static void NoteEntered() => Meter.Value?.Enter();

    /// <summary>세션 잠금을 놓았다(잠금이 부른다).</summary>
    internal static void NoteReleased() => Meter.Value?.Exit();

    internal static void EndMeasure(SessionHoldMeter meter, SessionHoldMeter? previous)
    {
        if (ReferenceEquals(Meter.Value, meter))
        {
            Meter.Value = previous;
        }
    }

    private sealed class Restore((TimeSpan? Limit, TimeSpan? Resume) previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Limit.Value = previous.Limit;
            ResumeLimit.Value = previous.Resume;
        }
    }

    private sealed class NoChange : IDisposable
    {
        public static readonly NoChange Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// <see cref="SessionWait.MeasureHold"/> 범위에서 세션 잠금을 쥔 시간의 합. 한 흐름이 쓴다(잠금은 재진입하지 않으므로 한 번에 하나만 쥔다).
/// </summary>
public sealed class SessionHoldMeter : IDisposable
{
    private readonly SessionHoldMeter? _previous;
    private long _held;
    private long _enteredAt;
    private bool _done;

    internal SessionHoldMeter(SessionHoldMeter? previous) => _previous = previous;

    /// <summary>범위 안에서 잠금을 한 번이라도 잡았다.</summary>
    public bool Entered { get; private set; }

    /// <summary>잠금을 쥔 시간의 합(지금 쥐고 있으면 지금까지).</summary>
    public TimeSpan Held
    {
        get
        {
            var held = Interlocked.Read(ref _held);
            var at = Interlocked.Read(ref _enteredAt);
            return System.Diagnostics.Stopwatch.GetElapsedTime(0, held + (at == 0 ? 0 : System.Diagnostics.Stopwatch.GetTimestamp() - at));
        }
    }

    internal void Enter()
    {
        Entered = true;
        Interlocked.Exchange(ref _enteredAt, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    internal void Exit()
    {
        var at = Interlocked.Exchange(ref _enteredAt, 0);
        if (at != 0)
        {
            Interlocked.Add(ref _held, System.Diagnostics.Stopwatch.GetTimestamp() - at);
        }
    }

    public void Dispose()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        SessionWait.EndMeasure(this, _previous);
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
