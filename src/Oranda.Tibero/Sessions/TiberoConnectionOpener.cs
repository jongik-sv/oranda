using System.Data.Common;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Sessions;

/// <summary>
/// Tibero 연결 열기·닫기(결정27: 드라이버 어댑터의 <c>CreateConnection</c> 만 쓰고 나머지는 ADO.NET 기본 형식).
/// 실측 근거: 드라이버의 <c>OpenAsync</c>·<c>Execute*Async</c> 는 동기로 돈다(tb-exec) → 스레드 풀로 옮긴다.
/// <c>Dispose</c> 만으로는 서버 세션이 남고 <c>Close</c> 를 먼저 불러야 끝난다(tb-session) → 모든 닫기 경로가 <see cref="CloseAsync"/> 를 쓴다.
/// </summary>
internal static class TiberoConnectionOpener
{
    /// <summary>
    /// 연결을 만들어 연다. <paramref name="connectTimeoutSeconds"/> 가 0 보다 크면 그 시간만 기다린다(연결 문자열이 아니라 기다림에 건다).
    /// 취소·시간 초과는 기다림만 끊고, 시작한 연결 시도는 끝난 뒤 백그라운드에서 닫는다(호출 스레드가 응답 없는 서버에 묶이지 않게).
    /// 시간 초과면 <see cref="TimeoutException"/>, 호출자 취소면 <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<DbConnection> OpenAsync(TiberoDriver driver, string connectionString, int connectTimeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(connectionString);
        ct.ThrowIfCancellationRequested();
        var connection = driver.CreateConnection(connectionString);
        var open = Task.Run(() => connection.OpenAsync(ct), CancellationToken.None);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (connectTimeoutSeconds > 0)
            {
                linked.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));
            }

            try
            {
                await open.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"접속 제한 시간({connectTimeoutSeconds}초)을 넘었습니다.");
            }

            return connection;
        }
        catch
        {
            Abandon(connection, open);
            throw;
        }
    }

    /// <summary>
    /// 연결을 닫는다: <c>Close</c> 먼저(서버 세션 끝), 그다음 <c>Dispose</c>. 끝나지 않을 수 있으므로 호출자는 <see cref="TimedClose"/> 로 감싼다.
    /// </summary>
    public static async ValueTask CloseAsync(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>열다 그만둔 연결: 열기가 끝난 뒤(성공이든 실패든) 닫는다. 기다리지 않는다.</summary>
    private static void Abandon(DbConnection connection, Task open)
    {
        _ = open.ContinueWith(
            t =>
            {
                _ = t.Exception;
                return TimedClose.RunAsync(() => CloseAsync(connection), "열다 그만둔 Tibero 연결");
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap().ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }
}
