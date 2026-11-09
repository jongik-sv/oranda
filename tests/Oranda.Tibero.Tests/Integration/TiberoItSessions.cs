using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 통합 시험 하니스(T3~T5 가 같이 쓴다). 서버 = <see cref="TiberoItEnv"/>(<c>ORANDA_IT_TIBERO_*</c>), 드라이버 있는 빌드에서만 연다.
/// <list type="bullet">
/// <item>기본은 읽기 시험이다. 쓰기·DDL 시험은 <c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돌고(<see cref="SkipWrite"/>), 객체 이름은
/// <see cref="TablePrefix"/>(<c>ORT_</c>) 로 시작해야 한다(<see cref="ObjectName"/>, D3).</item>
/// <item>접속 정보(주소·계정·비밀번호)는 시험 메시지·로그에 싣지 않는다. 값 비교는 <c>Assert.True(string.Equals(…))</c> 로 한다.</item>
/// </list>
/// 시험 메서드에는 <c>[TiberoItFact]</c>/<c>[TiberoWriteFact]</c>/<c>[TiberoKillFact]</c> 를 붙여 환경이 없으면 「건너뜀」으로 보이게 한다(<c>TiberoItFacts.cs</c>).
/// 사용: <c>await using var it = TiberoItSessions.Create(); if (it is null) return; var s = await it.OpenAsync();</c>
/// </summary>
internal sealed partial class TiberoItSessions : IAsyncDisposable
{
    /// <summary>시험이 만드는 객체 이름 앞머리(D3).</summary>
    public const string TablePrefix = "ORT_";

    private const string WriteVariable = "ORANDA_IT_TIBERO_WRITE";

    private readonly List<ISession> _opened = [];

    private TiberoItSessions(TiberoItEnv env)
    {
        Env = env;
        Manager = new TiberoSessionManager(Events);
    }

    public TiberoItEnv Env { get; }

    public EventBus Events { get; } = new();

    public TiberoSessionManager Manager { get; }

    /// <summary>쓰기 시험을 켰다(<c>ORANDA_IT_TIBERO_WRITE=1</c>).</summary>
    public static bool WriteEnabled => Environment.GetEnvironmentVariable(WriteVariable) == "1";

    /// <summary>서버·드라이버가 없으면 null(시험은 아무것도 하지 않는다). 서버가 없고 <c>ORANDA_IT_REQUIRE=1</c> 이면 던진다.</summary>
    public static TiberoItSessions? Create(TiberoItEnv? env = null)
    {
        env ??= new TiberoItEnv();
        if (env.Skip())
        {
            return null;
        }

        try
        {
            _ = global::Oranda.Tibero.Driver.TiberoDriver.Require(null);
        }
        catch (global::Oranda.Tibero.Driver.TiberoDriverUnavailableException ex)
        {
            if (env.RequireMode)
            {
                throw new InvalidOperationException("Tibero 통합 시험에 드라이버가 필요하다(ORANDA_TIBERO_DRIVER = Tibero.DataAccess.dll 경로).", ex);
            }

            return null;
        }

        return new TiberoItSessions(env);
    }

    /// <summary>
    /// 접속 정보(계정·DB 이름)가 든 값 비교. <c>Assert.Equal</c> 은 실패 때 두 값을 시험 로그에 찍으므로 <c>Assert.True(TiberoItSessions.Same(…))</c> 로 쓴다.
    /// </summary>
    public static bool Same(string? expected, string? actual, bool ignoreCase = false) =>
        string.Equals(expected, actual, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>쓰기 시험을 켜지 않았으면 true(그 시험은 아무것도 하지 않고 끝낸다).</summary>
    public static bool SkipWrite() => !WriteEnabled;

    /// <summary><c>ORT_</c> 로 시작하는 시험 객체 이름(대문자, 영문·숫자·밑줄만, 30자 이하). 어기면 던진다.</summary>
    public static string ObjectName(string suffix)
    {
        var name = (TablePrefix + suffix).ToUpperInvariant();
        if (!ObjectNamePattern().IsMatch(name))
        {
            throw new ArgumentException("시험 객체 이름은 ORT_ + 영문·숫자·밑줄, 30자 이하여야 한다: " + name, nameof(suffix));
        }

        return name;
    }

    /// <summary>시험 서버 프로필(Tibero). 필요하면 <c>with</c> 로 NLS·Keep Alive 등을 바꾼다.</summary>
    public ConnectionProfile Profile() =>
        new("it", Env.User, $"{Env.Host}:{Env.Port}/{Env.DbName}", ConnectTimeoutSeconds: 15, Dbms: Dbms.Tibero);

    /// <summary>세션을 연다. 하니스를 정리할 때 함께 닫는다.</summary>
    public async Task<TiberoSession> OpenAsync(SessionPurpose purpose = SessionPurpose.Work, ConnectionProfile? profile = null, CancellationToken ct = default)
    {
        using var credential = new SecureCredential(Env.Password);
        var session = (TiberoSession)await Manager.OpenAsync(profile ?? Profile(), credential, purpose, ct);
        _opened.Add(session);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        // 하나가 닫다 실패해도 나머지는 닫는다(남은 서버 세션이 다음 시험으로 새지 않게). 실패는 모아서 끝에 던진다.
        var failures = new List<Exception>();
        foreach (var session in _opened)
        {
            try
            {
                await session.DisposeAsync();
            }
#pragma warning disable CA1031 // 정리 단계: 모든 세션을 닫은 뒤 한꺼번에 알린다.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add(ex);
            }
        }

        _opened.Clear();
        if (failures.Count > 0)
        {
            throw new AggregateException("시험 세션을 닫다 실패했다.", failures);
        }
    }

    [GeneratedRegex("^ORT_[A-Z0-9_]{1,26}$", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectNamePattern();
}
