using System.Diagnostics;

namespace Oranda.Core.Sessions;

/// <summary>
/// 결과 행이 계속 오는지 보는 시계(행 진행 감시). DBMS 와 무관하며 드라이버의 <c>*Async</c> 가 동기로 돌아 취소 토큰으로 수신(recv)을 깨울 수 없는 경로가 쓴다.
/// 읽는 쪽(스레드 풀의 일)은 커서를 연 뒤 <see cref="Start"/>, 행을 하나 받을 때마다 <see cref="Row"/> 하고, 기다리는 쪽은 <see cref="WaitAsync{T}"/> 로 일을 기다린다.
/// <para>
/// 전체 제한 시간이 아니라 진행 기준이다: 큰 목록이 오래 걸려도 행이 계속 오면 끊지 않는다. <see cref="Start"/> 전(문장 실행 중 — 서버가 일하는 시간)은 재지 않는다.
/// 마지막 행(또는 <see cref="Start"/>) 뒤 한도 동안 행이 하나도 오지 않으면 기다림만 그만두고 <see cref="ServerStallException"/> 을 던진다.
/// 일은 그대로 두므로(멈춘 recv 는 깨울 수 없다) 부른 쪽이 연결을 끊긴 것으로 보고 일이 끝난 뒤의 정리(잠금 놓기·연결 닫기)를 백그라운드에 맡긴다.
/// </para>
/// </summary>
public sealed class RowProgressWatch
{
    /// <summary>
    /// 행 진행 한도 기본값 30초. 정상 읽기의 행·묶음 사이 간격은 밀리초 단위이고, 실측한 멈춤(약 1000행·64KB 를 받는 도중 수신이 끊김)은 풀리지 않는다.
    /// 접속 제한 시간 기본값(15초)의 두 배라 느린 망·바쁜 서버의 한 번 끊김은 견디고, 사용자는 30초 안에 오류를 본다.
    /// </summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(30);

    // Stopwatch 시각. 0 = 아직 켜지지 않음(Start 전).
    private long _last;
    private int _rows;

    /// <summary>커서가 열렸다(첫 응답): 시계를 켠다. 어느 스레드에서 불러도 된다.</summary>
    public void Start() => Volatile.Write(ref _last, Math.Max(1, Stopwatch.GetTimestamp()));

    /// <summary>행 하나를 받았다: 시계를 다시 맞춘다(켜지 않았으면 켠다).</summary>
    public void Row()
    {
        Interlocked.Increment(ref _rows);
        Start();
    }

    /// <summary>시계가 켜졌는가(<see cref="Start"/>·<see cref="Row"/> 뒤).</summary>
    public bool IsStarted => Volatile.Read(ref _last) != 0;

    /// <summary>지금까지 받은 행 수(로그·진단용).</summary>
    public int Rows => Volatile.Read(ref _rows);

    /// <summary>
    /// <paramref name="work"/> 가 끝날 때까지 기다린다. 시계가 켜진 뒤 <paramref name="limit"/> 동안 행이 없으면 <see cref="ServerStallException"/>.
    /// <paramref name="limit"/> 가 <see cref="Timeout.InfiniteTimeSpan"/> 이면 감시하지 않는다. 일이 실패하면 그 예외를 그대로 던진다.
    /// </summary>
    public async Task<T> WaitAsync<T>(Task<T> work, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (limit == Timeout.InfiniteTimeSpan)
        {
            return await work.ConfigureAwait(false);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);
        while (!work.IsCompleted)
        {
            var wait = limit;
            var last = Volatile.Read(ref _last);
            if (last != 0)
            {
                var idle = Stopwatch.GetElapsedTime(last);
                if (idle >= limit)
                {
                    throw new ServerStallException(limit);
                }

                wait = limit - idle;
            }

            using var delayCancel = new CancellationTokenSource(); // 일이 먼저 끝나면 타이머를 버린다(CA2027)
            await Task.WhenAny(work, Task.Delay(wait, delayCancel.Token)).ConfigureAwait(false);
            await delayCancel.CancelAsync().ConfigureAwait(false);
        }

        return await work.ConfigureAwait(false);
    }
}

/// <summary>
/// 서버 응답 없음: 결과를 받는 도중 정해진 시간(<see cref="Limit"/>) 동안 행이 하나도 오지 않아 기다림을 그만뒀다(<see cref="RowProgressWatch"/>).
/// 연결은 끊긴 것으로 본다 — 세션은 Broken 이 되고 다시 접속해야 한다.
/// </summary>
public sealed class ServerStallException : TimeoutException
{
    public ServerStallException()
        : this(RowProgressWatch.DefaultLimit)
    {
    }

    public ServerStallException(TimeSpan limit)
        : base($"서버 응답이 없어 읽기를 그만뒀습니다({limit.TotalSeconds:0.#}초 동안 행이 오지 않음). 연결이 끊긴 것으로 보고 정리합니다. 다시 접속한 뒤 다시 시도하세요.")
    {
        Limit = limit;
    }

    public ServerStallException(string message)
        : base(message)
    {
        Limit = RowProgressWatch.DefaultLimit;
    }

    public ServerStallException(string message, Exception inner)
        : base(message, inner)
    {
        Limit = RowProgressWatch.DefaultLimit;
    }

    /// <summary>행을 기다린 한도.</summary>
    public TimeSpan Limit { get; }
}
