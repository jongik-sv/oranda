using System.Data.Common;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Connection;

/// <inheritdoc cref="IConnectionProbe"/>
/// <remarks>
/// 드라이버(TDP.NET)를 쓰는 코드는 <c>#if TIBERO_DRIVER</c> 안에만 있다. 드라이버 없는 빌드(공개 배포본·CI)는 항상 「드라이버 없음」 실패를 돌려준다.
/// 서버 버전은 <c>SELECT * FROM V$VERSION</c> 의 글에서 뽑는다(읽기 전용).
/// </remarks>
[OrandaService(typeof(IConnectionProbe), Dbms = Dbms.Tibero)]
internal sealed class TiberoConnectionProbe : IConnectionProbe
{
    internal const string NoDriverMessage = "이 빌드에는 Tibero 드라이버가 들어 있지 않습니다. Tibero 접속은 드라이버가 포함된 빌드에서만 됩니다.";

    private const string VersionSql = "SELECT * FROM V$VERSION";

    private static readonly TiberoErrorClassifier Classifier = new();

    private readonly TiberoConnectionStringFactory _strings = new();

    private readonly TiberoServerVersionPolicy _policy = new();

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

        if (!TiberoDriverInfo.IsLinked)
        {
            return ServerProbeResult.Fail(null, NoDriverMessage);
        }

        string? version;
        try
        {
            version = await ReadVersionAsync(connectionString, profile.ConnectTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // 드라이버는 문장 취소를 DbException(12040)으로 알린다 — 호출자가 취소했으면 결과가 아니라 취소로 돌려준다.
            ct.ThrowIfCancellationRequested();
            return ServerProbeResult.Fail(Classifier.FromException(ex), Redact(ex.Message, password));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or TimeoutException)
        {
            return ServerProbeResult.Fail(null, Redact(ex.Message, password));
        }

        return _policy.IsSupported(version, out var reason)
            ? ServerProbeResult.Ok(version)
            : ServerProbeResult.Unsupported(version, reason);
    }

    public Task ChangePasswordAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("Tibero 는 아직 비밀번호 변경을 지원하지 않습니다."));

    /// <summary>드라이버 메시지가 연결 문자열을 되풀이해도 비밀번호가 화면·로그에 나가지 않게 가린다.</summary>
    internal static string Redact(string message, string password) =>
        password.Length == 0 ? message : message.Replace(password, "***", StringComparison.Ordinal);

#if TIBERO_DRIVER
    // 드라이버 형식을 쓰는 코드는 이 메서드 안에만 둔다(호출부가 TiberoDriverInfo.IsLinked 로 먼저 거른다).
    // 접속 제한 시간은 연결 문자열에 넣지 않고 기다림에 건다 — 취소·시간 초과는 기다림만 끊고 시작한 연결 시도는 끝난 뒤에 닫는다.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<string?> ReadVersionAsync(string connectionString, int connectTimeoutSeconds, CancellationToken ct)
    {
        DbConnection connection = new global::Tibero.DataAccess.Client.TiberoConnection(connectionString);
        var open = connection.OpenAsync(ct);
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

                // 제한 시간은 접속과 버전 조회 전체에 건다(서버가 접속은 받고 응답하지 않는 경우).
                await using var command = connection.CreateCommand();
                command.CommandText = VersionSql;
                await using var reader = await command.ExecuteReaderAsync(linked.Token).ConfigureAwait(false);
                // 열은 (NAME, VALUE, BANNER). 앞의 두 열만 읽는다.
                var rows = new List<(string?, string?)>();
                while (await reader.ReadAsync(linked.Token).ConfigureAwait(false))
                {
                    rows.Add((
                        reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                        reader.FieldCount > 1 && !reader.IsDBNull(1) ? Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) : null));
                }

                return TiberoServerVersionPolicy.FromVersionRows(rows) ?? connection.ServerVersion;
            }
            catch (Exception ex) when ((ex is OperationCanceledException or DbException) && linked.IsCancellationRequested)
            {
                // 호출자가 취소했으면 취소로, 아니면 제한 시간 초과로 알린다(드라이버는 문장 취소를 DbException 으로 던질 수 있다).
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"접속 제한 시간({connectTimeoutSeconds}초)을 넘었습니다.");
            }
        }
        finally
        {
            if (open.IsCompleted)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _ = open.ContinueWith(
                    t =>
                    {
                        _ = t.Exception;
                        connection.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }
    }
#else
    private static Task<string?> ReadVersionAsync(string connectionString, int connectTimeoutSeconds, CancellationToken ct) =>
        throw new InvalidOperationException(NoDriverMessage);
#endif
}
