namespace Oranda.Oracle.Execution;

/// <summary>
/// 페이지 가져오기 취소(EXEC-02). 사용자 취소는 행 사이에서 받고(협조적), 진행 중인 서버 왕복이 정한 시간 안에 끝나지 않을 때만
/// 드라이버에 넘긴 토큰(<see cref="DriverToken"/>)을 취소한다(하드 취소).
/// <para>
/// ODP.NET 리더는 한 번 Cancel 되면(<c>OracleDataReaderImpl.m_bCancelled</c>, 새 리더에서만 지워짐) 그 뒤 서버에서 행을 더 가져올 때마다
/// ORA-01013 을 낸다. 그래서 하드 취소가 나간 결과는 더 이어 가져올 수 없다(<see cref="HardCancelled"/>).
/// </para>
/// </summary>
internal sealed class FetchCancellation : IDisposable
{
    /// <summary>사용자 취소 뒤 하드 취소까지 기다리는 시간. 보통 왕복(약 한 페이지)은 이보다 훨씬 빨리 끝난다.</summary>
    public static readonly TimeSpan HardCancelDelay = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _driver = new();
    private readonly CancellationTokenRegistration _user;

    public FetchCancellation(TimeSpan hardCancelDelay, CancellationToken user)
    {
        _user = user.Register(static s =>
        {
            var (driver, delay) = ((CancellationTokenSource, TimeSpan))s!;
            driver.CancelAfter(delay);
        }, (_driver, hardCancelDelay));
    }

    /// <summary>드라이버 호출(ReadAsync)에 넘길 토큰.</summary>
    public CancellationToken DriverToken => _driver.Token;

    /// <summary>하드 취소가 나갔다: 이 결과의 리더는 더 쓸 수 없다.</summary>
    public bool HardCancelled => _driver.IsCancellationRequested;

    public void Dispose()
    {
        // 등록 해제는 돌고 있는 콜백을 기다린다: 그 뒤에는 CancelAfter 가 새로 걸리지 않는다. 걸린 타이머는 CTS 와 함께 정리된다.
        _user.Dispose();
        _driver.Dispose();
    }
}
