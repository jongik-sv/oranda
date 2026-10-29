using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 실행 토큰 하나가 묶이는 실행 범위(조정 결정 2026-10-28): 접속·허용 수준(<see cref="AiContext.AccessLevel"/>)·대상 문장(참조 객체의 기준)·보낼 범위.
/// 변하지 않는 값이다. AI 요청을 시작하는 쪽이 만들어 <see cref="IAgentRunTokens.Issue"/> 로 토큰을 받는다.
/// </summary>
/// <param name="RunId">실행 id(대화 창·감사 기록이 같은 값을 쓴다).</param>
/// <param name="Profile">
/// 이 요청의 접속(시작 때의 사본). 도구 인자 <c>connection</c> 은 이 접속이어야 한다.
/// 사본이므로 사용자가 접속 설정(환경 라벨·AI 허용 수준)을 바꿔도 이미 시작된 실행에는 반영되지 않는다 —
/// 바꾼 쪽이 그 접속의 실행 토큰을 폐기해야 한다(<see cref="IAgentRunTokens.Revoke"/>).
/// </param>
/// <param name="Context">요청 문맥. 대상 문장·sql_id 와 시작 때의 허용 수준.</param>
/// <param name="Send">문장 참조 객체 밖에서 사용자가 더 허용한 범위(접속의 「보낼 범위」).</param>
public sealed record AgentRunScope(string RunId, ConnectionProfile Profile, AiContext Context, AgentSendScope Send)
{
    public AgentRunScope(string runId, ConnectionProfile profile, AiContext context)
        : this(runId, profile, context, AgentSendScope.StatementOnly)
    {
    }

    /// <summary>
    /// 이 대화의 리터럴 값 사전. 리터럴 가림이 필요한 실행(운영 접속이거나 설정이 켜짐)이면 시작하는 쪽이 채우고,
    /// 게이트가 도구 인자를 되돌리고 결과의 SQL 글을 가리는 데 쓴다. null 이면 이 실행에는 사전이 없다.
    /// 가림이 필요한데 사전이 없으면 게이트는 SQL 을 다루는 도구를 막는다.
    /// </summary>
    public LiteralVault? Literals { get; init; }

    /// <summary>
    /// 이 실행에 리터럴 가림이 필요하다(시작하는 쪽이 <see cref="AiAccessPolicy.MaskLiterals"/> 로 채운다: 운영이거나 설정이 켜짐).
    /// 참인데 <see cref="Literals"/> 가 없으면 게이트가 SQL 을 다루는 도구를 막는다. 운영 접속은 이 값과 관계없이 항상 필요로 본다.
    /// </summary>
    public bool MaskLiteralsRequired { get; init; }
}

/// <summary>
/// 진행 중인 AI 요청 한 번(= 에이전트 세션)의 실행 상태: 변하지 않는 <see cref="Run"/> 범위와, 호출하면서 변하는 값(클라이언트 이름·조회해도 되는 sql_id).
/// 게이트는 도구 호출마다 이것으로 접속·허용 수준·범위를 판단한다. DB 세션(<c>ISession</c>)은 들지 않는다 —
/// <see cref="IAgentSessionSource.AcquireDbSessionAsync"/> 가 고른다.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _sqlIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _revocation = new();
    private long _sequence;
    private volatile bool _revoked;

    public AgentSession(AgentRunScope run)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        RevocationToken = _revocation.Token;
        if (!string.IsNullOrWhiteSpace(run.Context.SqlId))
        {
            _sqlIds[run.Context.SqlId] = 0;
        }
    }

    public AgentRunScope Run { get; }

    public string RunId => Run.RunId;

    public ConnectionProfile Profile => Run.Profile;

    public AiContext Context => Run.Context;

    public AgentSendScope Send => Run.Send;

    /// <summary>
    /// 이 호출에 적용할 허용 수준: min(<see cref="AiAccessPolicy.Effective"/>(<see cref="Profile"/>), <see cref="AiContext.AccessLevel"/>).
    /// 호출한 쪽이 문맥에 넓은 값을 적어도 프로필의 실효 수준(운영 한도 포함)을 넘지 못한다.
    /// 프로필은 시작 때의 사본이라(<see cref="AgentRunScope.Profile"/>) 접속 설정이 그 뒤에 바뀌어도 이 값은 따라가지 않는다 —
    /// 설정을 바꾼 쪽이 그 접속의 토큰을 폐기해야 한다(<see cref="IAgentRunTokens.Revoke"/>).
    /// </summary>
    public AiAccessLevel Level
    {
        get
        {
            var effective = AiAccessPolicy.Effective(Profile);
            var requested = Enum.IsDefined(Context.AccessLevel) ? Context.AccessLevel : AiAccessLevel.Off;
            return requested < effective ? requested : effective;
        }
    }

    /// <summary>
    /// 폐기됐는가. 폐기된 세션의 도구 호출은 이미 연결된 파이프에서도 게이트가 거부한다.
    /// 토큰 폐기(<see cref="IAgentRunTokens.Revoke"/>)와 요청을 낸 쪽의 중단이 이것을 켠다.
    /// </summary>
    public bool IsRevoked => _revoked;

    /// <summary>폐기되면 취소되는 토큰. 게이트가 호출자의 취소 토큰과 묶어 처리자·DB 세션 획득에 넘겨, 진행 중인 조회도 멈추게 한다.</summary>
    public CancellationToken RevocationToken { get; }

    /// <summary>세션을 폐기한다(되돌릴 수 없다). <see cref="RevocationToken"/> 도 취소한다. 취소 콜백의 예외는 밖으로 내지 않는다.</summary>
    public void Revoke()
    {
        _revoked = true;
        try
        {
            _revocation.Cancel();
        }
        catch (AggregateException)
        {
            // 취소 콜백(처리자·DB 쪽 등록)의 예외다. 폐기 표시는 위에서 했고, 폐기하는 쪽이 이 예외로 멈추면 안 된다.
        }
    }

    /// <summary>세션당 도구 호출을 한 번에 하나로 줄이는 문(게이트가 쓴다). 같은 DB 세션을 겹쳐 쓰지 않게 한다.</summary>
    internal SemaphoreSlim CallGate { get; } = new(1, 1);

    /// <summary>
    /// 실행이 끝났음을 알린다: <see cref="Revoke"/> 만 한다. 세마포어와 취소 소스는 일부러 Dispose 하지 않는다 —
    /// 대기 중인 호출이 있을 때 정리하면 대기자가 영원히 기다리거나 ObjectDisposedException 으로 터지므로(<c>SessionCommandGate</c> 와 같은 이유),
    /// 이 객체가 가비지 컬렉션될 때 함께 사라지게 둔다. 둘 다 대기 핸들·타이머를 만들지 않아 새는 자원이 없다.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "대기 중인 호출이 있을 수 있어 Dispose 하지 않는다(위 설명).")]
    public void Dispose() => Revoke();

    /// <summary>도구 호출마다 1씩 늘어나는 순번(<see cref="AgentToolActivityEventArgs.Sequence"/>).</summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>호출한 클라이언트 이름(hello 에서 받은 값). 연결을 맡은 쪽이 채운다.</summary>
    public string? Client { get; set; }

    /// <summary>이 요청에서 조회해도 되는 sql_id 인가(문맥의 sql_id + 도구 결과에서 알게 된 sql_id).</summary>
    public bool IsKnownSqlId(string sqlId) => !string.IsNullOrWhiteSpace(sqlId) && _sqlIds.ContainsKey(sqlId);

    /// <summary>도구 결과에서 알게 된 sql_id 를 기억한다.</summary>
    public void AddSqlId(string sqlId)
    {
        if (!string.IsNullOrWhiteSpace(sqlId))
        {
            _sqlIds[sqlId] = 0;
        }
    }
}

/// <summary>
/// 문장이 참조하는 객체와 그 인덱스·FK 상대 밖에서 추가로 허용하는 범위.
/// <see cref="Patterns"/> 는 <c>OWNER.NAME</c> 꼴이며 <c>*</c>(여러 글자)·<c>?</c>(한 글자)를 쓸 수 있다.
/// </summary>
/// <param name="AllObjects">true 면 접속의 모든 객체. 기본은 false.</param>
/// <param name="Patterns">허용 패턴.</param>
public sealed record AgentSendScope(bool AllObjects, IReadOnlyList<string> Patterns)
{
    /// <summary>추가 허용 없음: 문장 참조 객체와 그 상대만.</summary>
    public static AgentSendScope StatementOnly { get; } = new(false, []);

    /// <summary>
    /// 객체가 이 범위에 드는가. <see cref="AllObjects"/> 이거나 패턴 하나와 <c>OWNER.NAME</c> 이 맞으면 참이다.
    /// 패턴의 <c>*</c> 는 어떤 글자든(점 포함) 여러 개, <c>?</c> 는 글자 하나이며 대소문자를 가리지 않는다.
    /// </summary>
    public bool Matches(string owner, string name)
    {
        if (AllObjects)
        {
            return true;
        }

        var key = owner + "." + name;
        foreach (var pattern in Patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
            if (Regex.IsMatch(key, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                return true;
            }
        }

        return false;
    }
}
