using System.Data.Common;
using System.Globalization;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Connection;

/// <inheritdoc cref="IConnectionProbe"/>
/// <remarks>
/// 드라이버는 사용자가 지정한 위치에서 처음 쓸 때 읽는다(D-046-1, <see cref="TiberoDriver.Require"/>). 지정 안 했거나 쓸 수 없으면 그 이유를 실패로 돌려준다.
/// 서버 버전은 <c>SELECT * FROM V$VERSION</c> 의 글에서 뽑는다(읽기 전용).
/// 접속은 됐는데 이 뷰를 못 읽으면(일반 계정: TBR-8033·권한 없음) 시험 실패가 아니라 <c>connection.ServerVersion</c> 으로 대신하고
/// 결과의 <see cref="ServerProbeResult.Message"/> 에 안내를 싣는다. 끊김·취소·제한 시간은 그대로 실패(또는 취소)다.
/// </remarks>
[OrandaService(typeof(IConnectionProbe), Dbms = Dbms.Tibero)]
internal sealed class TiberoConnectionProbe : IConnectionProbe
{
    private const string VersionSql = "SELECT * FROM V$VERSION";

    private static readonly TiberoErrorClassifier Classifier = new();

    private readonly TiberoConnectionStringFactory _strings = new();

    private readonly TiberoServerVersionPolicy _policy = new();

    private readonly Func<TiberoDriver> _driver;

    public TiberoConnectionProbe(IDriverPathSource? paths = null)
        : this(() => TiberoDriver.Require(paths))
    {
    }

    /// <summary>시험용: 드라이버를 주는 함수(가짜 드라이버·실패 흉내).</summary>
    internal TiberoConnectionProbe(Func<TiberoDriver> driver)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public Dbms Dbms => Dbms.Tibero;

    public async Task<ServerProbeResult> ProbeAsync(ConnectionProfile profile, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(password);
        ct.ThrowIfCancellationRequested();

        string connectionString;
        try
        {
            connectionString = _strings.Build(profile, password);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return ServerProbeResult.Fail(null, ex.Message);
        }

        TiberoDriver driver;
        try
        {
            driver = _driver();
        }
        catch (TiberoDriverUnavailableException ex)
        {
            return ServerProbeResult.Fail(null, ex.Message);
        }

        string? version;
        string? notice;
        try
        {
            (version, notice) = await ReadVersionAsync(driver, connectionString, profile.ConnectTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException || TiberoErrorClassifier.IsDriverException(ex))
        {
            // 드라이버는 문장 취소를 DbException(12040)으로 알린다 — 호출자가 취소했으면 결과가 아니라 취소로 돌려준다.
            ct.ThrowIfCancellationRequested();
            var error = Classifier.FromException(ex);
            return ServerProbeResult.Fail(error is null ? null : error with { Message = Redact(error.Message, password) }, Redact(ex.Message, password));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or TimeoutException)
        {
            return ServerProbeResult.Fail(null, Redact(ex.Message, password));
        }

        // 버전 뷰를 못 읽어 드라이버 버전도 비면 버전을 모르는 채로 통과시킨다(접속은 됐다 — 버전 모름은 「지원 안 됨」이 아니다).
        if (notice is not null && string.IsNullOrWhiteSpace(version))
        {
            return ServerProbeResult.Ok(null, notice);
        }

        return _policy.IsSupported(version, out var reason)
            ? ServerProbeResult.Ok(version, notice)
            : ServerProbeResult.Unsupported(version, reason);
    }

    public Task ChangePasswordAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("Tibero 는 아직 비밀번호 변경을 지원하지 않습니다."));

    /// <summary>드라이버 메시지가 연결 문자열을 되풀이해도 비밀번호가 화면·로그에 나가지 않게 가린다.</summary>
    internal static string Redact(string message, string password) =>
        password.Length == 0 ? message : message.Replace(password, "***", StringComparison.Ordinal);

    /// <summary>
    /// 접속·버전 조회. 드라이버의 *Async 는 동기로 돌아(실측 exec 4장) 스레드 풀에서 돌리고, 제한 시간·취소는 기다림에 건다 —
    /// 기다림을 끊어도 시작한 일은 끝난 뒤 백그라운드에서 연결을 닫는다(응답 없는 서버에 부른 쪽이 묶이지 않게).
    /// </summary>
    private static async Task<(string? Version, string? Notice)> ReadVersionAsync(TiberoDriver driver, string connectionString, int connectTimeoutSeconds, CancellationToken ct)
    {
        // 제한 시간은 접속과 버전 조회 전체에 건다(서버가 접속은 받고 응답하지 않는 경우). 일이 끝날 때까지 토큰 원본을 살려 둔다.
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (connectTimeoutSeconds > 0)
        {
            limit.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));
        }

        var token = limit.Token;
        var work = Task.Run(() => ReadVersion(driver, connectionString, token), CancellationToken.None);
        try
        {
            return await work.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or DbException || TiberoErrorClassifier.IsDriverException(ex)) && limit.IsCancellationRequested)
        {
            // 호출자가 취소했으면 취소로, 아니면 제한 시간 초과로 알린다(드라이버는 문장 취소를 DbException 으로 던질 수 있다).
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"접속 제한 시간({connectTimeoutSeconds}초)을 넘었습니다.");
        }
        finally
        {
            // 일이 아직 돌면(기다림을 끊음) 끝난 뒤에 토큰 원본을 버리고 그 오류는 관찰만 한다 — 도는 동안 토큰을 쓰므로 먼저 버리지 않는다.
            if (work.IsCompleted)
            {
                limit.Dispose();
            }
            else
            {
                _ = work.ContinueWith(
                    t =>
                    {
                        _ = t.Exception;
                        limit.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }
    }

    private static (string? Version, string? Notice) ReadVersion(TiberoDriver driver, string connectionString, CancellationToken ct)
    {
        var connection = driver.CreateConnection(connectionString);
        try
        {
            connection.Open();
            ct.ThrowIfCancellationRequested();

            try
            {
                var rows = ReadVersionRows(connection, ct);
                ct.ThrowIfCancellationRequested();
                return (TiberoServerVersionPolicy.FromVersionRows(rows) ?? connection.ServerVersion, null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsVersionViewUnreadable(ex))
            {
                // 접속은 됐는데 V$VERSION 을 못 읽는다(일반 계정: TBR-8033 객체 없음·권한 없음) — 시험 실패가 아니다. 드라이버가 알려 주는 버전으로 대신한다.
                var driverVersion = string.IsNullOrWhiteSpace(connection.ServerVersion) ? null : connection.ServerVersion.Trim();
                return (driverVersion, NoVersionViewNotice(driverVersion));
            }
        }
        finally
        {
            // Dispose 만으로는 서버 세션이 남는다(tb-session 실측) — 먼저 닫는다. 열지 못한 연결은 닫지 않는다(닫기 오류가 원래 오류를 가리지 않게).
            try
            {
                if (connection.State != System.Data.ConnectionState.Closed)
                {
                    connection.Close();
                }
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException || TiberoErrorClassifier.IsDriverException(ex))
            {
                // 닫기 실패는 접속 시험 결과가 아니다.
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>접속 시험 성공에 붙는 안내: 버전 뷰를 못 읽어 드라이버가 알려 준 버전(없으면 모름)으로 대신했다.</summary>
    internal static string NoVersionViewNotice(string? driverVersion) =>
        string.IsNullOrEmpty(driverVersion)
            ? "서버 버전을 읽지 못했습니다(V$VERSION 권한 없음). 버전은 알 수 없습니다."
            : $"서버 버전을 읽지 못했습니다(V$VERSION 권한 없음). 드라이버가 알려 준 버전 {driverVersion} 로 대신합니다.";

    /// <summary>
    /// 버전 조회 실패가 「이 계정이 뷰를 못 본다」(객체 없음 TBR-8033 — 실측으로는 권한 없는 객체도 이 번호 — 또는 권한 없음 TBR-8053)인가.
    /// 끊김·취소·그 밖의 오류는 아니다 — 그건 시험 실패(또는 취소)로 둔다.
    /// </summary>
    private static bool IsVersionViewUnreadable(Exception ex) =>
        Classifier.FromException(ex)?.Kind is DbErrorKind.ObjectNotFound or DbErrorKind.InsufficientPrivileges;

    private static List<(string?, string?)> ReadVersionRows(DbConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = VersionSql;
        // 취소는 타이머 스레드에서도 불린다 — 드라이버가 던져도 프로세스를 죽이지 않게 삼킨다(취소는 최선 노력).
        using var cancel = ct.Register(static state => TryCancel((DbCommand)state!), command);
        using var reader = command.ExecuteReader();

        // 열은 (NAME, VALUE, BANNER). 앞의 두 열만 읽는다.
        var rows = new List<(string?, string?)>();
        while (!ct.IsCancellationRequested && reader.Read())
        {
            rows.Add((
                reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.FieldCount > 1 && !reader.IsDBNull(1) ? Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) : null));
        }

        return rows;
    }

    private static void TryCancel(DbCommand command)
    {
        try
        {
            command.Cancel();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ObjectDisposedException || TiberoErrorClassifier.IsDriverException(ex))
        {
            // 이미 끝났거나 닫힌 명령.
        }
    }
}
