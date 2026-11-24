using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 외부 허가(Grant) 하나의 읽기 모델(설계 agent-cli-control §3.2, 계약 A1). 앱 도구 처리자·상태 화면이 보는 값이며 변하지 않는 사본이다 —
/// 연장·클라이언트 추가가 있으면 <see cref="IAgentGrants"/> 가 새 사본을 준다. 저장·만료·하위 세션은 A2 몫.
/// </summary>
/// <param name="GrantId">허가 id(감사의 실행 id 앞부분: <c>grantId + ":" + 접속</c>).</param>
/// <param name="Connections">허용 접속. <see cref="AgentGrantConnection.Level"/> 은 이미 외부 상한까지 줄인 값이다.</param>
/// <param name="CreatedAt">만든 시각.</param>
/// <param name="ExpiresAt">만료 시각. null 이면 앱을 끌 때까지.</param>
/// <param name="Clients">hello 로 붙은 클라이언트 이름(붙은 순서, 같은 이름은 한 번).</param>
/// <param name="AllowUnboundTabText">접속이 없는 탭의 글을 읽어도 된다(「탭 글 읽기 허용」, 기본 켬).</param>
/// <param name="Label">창 이름(<see cref="AgentGrantRequest.Label"/>, 연결 파일의 <c>label</c>).</param>
public sealed record AgentGrantView(
    string GrantId,
    IReadOnlyList<AgentGrantConnection> Connections,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Clients,
    bool AllowUnboundTabText = true,
    string Label = "")
{
    /// <summary>
    /// 「로컬 모델 — 가리지 않음」을 골랐다(<see cref="AgentGrantRequest.LocalModel"/>, 2단계 D4). 진짜 로컬 모델인지는 앱이 확인하지 못한다.
    /// 실제로 가림을 풀지는 환경과 스위치를 보는 <see cref="AgentGrantPolicy.UsesLocalModel"/> 이 정한다.
    /// </summary>
    public bool LocalModel { get; init; }

    /// <summary>별칭(대소문자 무시)으로 허용 접속을 찾는다. 없으면 null.</summary>
    public AgentGrantConnection? FindConnection(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        foreach (var connection in Connections)
        {
            if (string.Equals(connection.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return connection;
            }
        }

        return null;
    }

    /// <summary>프로필 id 로 허용 접속을 찾는다(탭의 <c>ConnectionProfileId</c>). 없으면 null.</summary>
    public AgentGrantConnection? FindConnection(Guid profileId)
    {
        foreach (var connection in Connections)
        {
            if (connection.ProfileId == profileId)
            {
                return connection;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>connection</c> 인자를 허용 접속으로 바꾼다(설계 §3.2): 값이 있으면 그 별칭, 없으면 허용 접속이 하나일 때만 그 접속.
    /// 못 고르면 null 과 오류 코드(<c>connection</c> = 허가 밖 별칭, <c>connection_required</c> = 여럿인데 빠짐).
    /// </summary>
    public AgentGrantConnection? ResolveConnection(string? alias, out string? errorCode)
    {
        errorCode = null;
        if (!string.IsNullOrWhiteSpace(alias))
        {
            var found = FindConnection(alias);
            errorCode = found is null ? AgentErrorCodes.Connection : null;
            return found;
        }

        if (Connections.Count == 1)
        {
            return Connections[0];
        }

        errorCode = AgentErrorCodes.ConnectionRequired;
        return null;
    }

    /// <summary>탭 접속의 수준. 접속 없는 탭(null)은 <see cref="AiAccessLevel.Off"/>, 허가 밖 접속도 Off.</summary>
    public AiAccessLevel LevelFor(Guid? profileId) =>
        profileId is { } id && FindConnection(id) is { } connection ? connection.Level : AiAccessLevel.Off;

    /// <summary>허가 밖 접속에 묶인 탭이다(<c>list_tabs</c> 의 <c>locked</c>). 접속 없는 탭은 잠기지 않는다.</summary>
    public bool IsLocked(Guid? profileId) => profileId is { } id && FindConnection(id) is null;

    /// <summary>
    /// 탭 글을 읽어도 되는가(설계 §6.1): 접속 없는 탭은 <see cref="AllowUnboundTabText"/>, 허가 밖 접속은 거부,
    /// 운영 접속은 <see cref="AgentGrantPolicy.CanReadProductionTabText"/> 가 아니면 수준과 관계없이 거부(리터럴에 업무 값이 있다, E-18), 수준 Off 도 거부.
    /// </summary>
    public bool CanReadTabText(Guid? profileId)
    {
        if (profileId is not { } id)
        {
            return AllowUnboundTabText;
        }

        return FindConnection(id) is { } connection
            && (connection.Environment != ConnectionEnvironment.Production || AgentGrantPolicy.CanReadProductionTabText(this))
            && connection.Level > AiAccessLevel.Off;
    }

    /// <summary>그 접속의 결과에서 민감 열·비밀 바인드 값을 가리는가(<see cref="AgentGrantPolicy.MasksSensitive"/>). 접속을 모르면 가린다.</summary>
    public bool MasksSensitive(Guid? profileId) =>
        profileId is not { } id || FindConnection(id) is not { } connection || AgentGrantPolicy.MasksSensitive(this, connection.Environment);

    /// <summary>그 접속의 민감 컬럼 가림이 켜져 있는가(D-069). 접속을 모르면 켬. 꺼져 있으면 가림을 「항상 가리는 열」로 좁힌다.</summary>
    public bool MasksSensitiveColumns(Guid? profileId) =>
        profileId is not { } id || FindConnection(id) is not { } connection || connection.MaskSensitiveColumns;

    /// <summary>그 접속의 이력·오류 글에서 리터럴을 가리는가(<see cref="AgentGrantPolicy.MasksLiterals"/>). 접속을 모르면 거짓(그런 탭은 먼저 거부된다).</summary>
    public bool MasksLiterals(Guid? profileId) =>
        profileId is { } id && FindConnection(id) is { } connection && AgentGrantPolicy.MasksLiterals(this, connection.Environment);

    /// <summary>만료됐는가(<paramref name="now"/> 가 만료 시각 이상).</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && now >= at;
}

/// <summary>허가 안 접속 하나. 접속 정보 중 밖으로 내도 되는 것만 담는다(호스트·서비스·사용자 이름 없음).</summary>
/// <param name="ProfileId">접속 프로필 id.</param>
/// <param name="Alias">접속 별칭(<c>ConnectionProfile.Name</c>). 도구 인자 <c>connection</c> 값.</param>
/// <param name="Environment">환경 라벨.</param>
/// <param name="Dbms">DBMS 종류.</param>
/// <param name="Level">외부 허가에서 쓰는 수준 = min(접속 실효 수준, 외부 상한). <see cref="AgentGrantPolicy.LevelFor"/>.</param>
/// <param name="ReadOnly">접속의 읽기 전용 설정.</param>
/// <param name="ExplainPlanAllowed">접속의 EXPLAIN PLAN 허용.</param>
public sealed record AgentGrantConnection(
    Guid ProfileId,
    string Alias,
    ConnectionEnvironment Environment,
    Dbms Dbms,
    AiAccessLevel Level,
    bool ReadOnly = false,
    bool ExplainPlanAllowed = true,
    bool MaskSensitiveColumns = true);

/// <summary>허가를 만들 때 고른 접속 하나와 그 외부 상한.</summary>
/// <param name="Profile">접속 프로필(지금 값).</param>
/// <param name="Cap">외부 상한. 보통 <see cref="AgentGrantPolicy.DefaultCap"/>.</param>
public sealed record AgentGrantTarget(ConnectionProfile Profile, AiAccessLevel Cap);

/// <summary>허가 만들기 요청(연결 창 「연결」).</summary>
/// <param name="Targets">허용 접속. 비어 있어도 된다(편집기 도구만 쓰는 연결).</param>
/// <param name="Lifetime">기한. null 이면 앱을 끌 때까지. 기본 <see cref="AgentGrantPolicy.DefaultLifetime"/>.</param>
/// <param name="AllowUnboundTabText">접속 없는 탭 글 읽기 허용.</param>
/// <param name="Label">창 이름(연결 파일의 <c>label</c>, 보통 작업 공간 이름).</param>
/// <param name="LocalModel">「로컬 모델 — 가리지 않음」(2단계 D4, 기본 꺼짐). <see cref="AgentGrantView.LocalModel"/> 로 옮겨진다.</param>
public sealed record AgentGrantRequest(
    IReadOnlyList<AgentGrantTarget> Targets,
    TimeSpan? Lifetime,
    bool AllowUnboundTabText = true,
    string Label = "",
    bool LocalModel = false);

/// <summary>허가를 만든 결과. <see cref="Token"/> 원문은 연결 파일에만 쓰고 다른 곳에 두지 않는다.</summary>
public sealed record AgentGrantIssue(AgentGrantView Grant, string Token)
{
    /// <summary>토큰이 로그·예외 글에 섞이지 않게 토큰을 빼고 보인다.</summary>
    public override string ToString() => $"AgentGrantIssue {{ GrantId = {Grant.GrantId} }}";
}

/// <summary>
/// 외부 허가 저장소(설계 §3.2, 구현 A2). 토큰은 해시로만 둔다(<see cref="AgentRunTokenStore"/> 와 같은 방식).
/// 연결 창(레인 E)이 만들고·연장하고·끊고, 다리(A4)가 hello 토큰을 풀고, 처리자는 <see cref="AgentGrantView"/> 만 받는다.
/// </summary>
public interface IAgentGrants
{
    /// <summary>살아 있는 허가(만료·폐기 빼고). 이 앱(창)의 것만.</summary>
    IReadOnlyList<AgentGrantView> Active { get; }

    /// <summary>허가를 만들고 토큰을 발급한다. 같은 창의 옛 허가는 폐기한다(다시 켜면 새 토큰, 설계 §6.4).</summary>
    AgentGrantIssue Create(AgentGrantRequest request);

    /// <summary>토큰으로 살아 있는 허가를 찾는다. 모르거나 만료·폐기면 null.</summary>
    AgentGrantView? Resolve(string token);

    /// <summary>기한을 지금부터 <paramref name="by"/> 만큼으로 늘린다. 없거나 폐기된 허가면 null.</summary>
    AgentGrantView? Extend(string grantId, TimeSpan by);

    /// <summary>hello 의 클라이언트 이름을 더한다(이미 있으면 그대로). 없거나 폐기된 허가면 null.</summary>
    AgentGrantView? AddClient(string grantId, string clientName);

    /// <summary>허가와 그 하위 세션을 폐기한다(「끊기」·만료·앱 종료). 진행 중 조회는 멈춘다.</summary>
    void Revoke(string grantId);

    /// <summary>만들기·연장·클라이언트 추가·폐기·만료 뒤에 한 번. 연결 파일 쓰기(A4)·상태 표시줄 띠(E)가 듣는다.</summary>
    event EventHandler<AgentGrantChanged>? Changed;
}

/// <summary><see cref="IAgentGrants.Changed"/> 인자. 폐기·만료면 <see cref="Grant"/> 는 마지막 사본이고 <see cref="Ended"/> 가 참.</summary>
public sealed record AgentGrantChanged(AgentGrantView Grant, bool Ended);

/// <summary>
/// 외부 허가의 기본값과 수준 계산(설계 §6.3·§6.4). 순수 함수.
/// 2단계(agent-cli-1122 C0): 운영 접속 허용과 로컬 모델 가림 해제를 스위치 둘(<see cref="ProductionRuns"/>·<see cref="LocalModelCoversProduction"/>)과
/// 정책 함수 몇 개에 모은다. 처리자·게이트는 환경을 직접 보지 않고 이 함수들을 부른다 — 스위치를 바꿔도 다른 코드는 그대로다.
/// </summary>
public static class AgentGrantPolicy
{
    /// <summary>
    /// D5: 운영 접속에서도 <c>query</c>·<c>run_tab</c>·<c>export_result</c>·계획 도구를 쓴다(외부 상한 운영 = QueryRows).
    /// 켬(D5 확정, m-gate-prod G3). 끄면(false) 운영 = Metadata 상한으로 돌아가고 다른 코드는 바꾸지 않는다(§3.2 되돌리기).
    /// const 가 아닌 까닭: 시험·처리자의 <c>if</c> 에 넣어도 CS0162(도달 못 하는 코드)가 -warnaserror 빌드를 깨지 않게.
    /// </summary>
    public static readonly bool ProductionRuns = true;

    /// <summary>
    /// D5: 「로컬 모델」 허가면 운영 접속도 가리지 않는다(민감 열·리터럴·운영 탭 글). 꺼져 있으면 로컬 모델은 개발·시험에서만 가림을 푼다.
    /// 켬(D5 확정, m-gate-prod G3). 끄면 운영은 로컬 모델이어도 가린다. const 가 아닌 까닭은 <see cref="ProductionRuns"/> 와 같다.
    /// </summary>
    public static readonly bool LocalModelCoversProduction = true;

    /// <summary>기본 기한 8시간(E-4). 「연장」 한 번도 같은 길이.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(8);

    /// <summary>만료 몇 분 전에 「연장」 띠를 보일지.</summary>
    public static readonly TimeSpan ExtendNoticeBefore = TimeSpan.FromMinutes(10);

    /// <summary>환경별 외부 상한(<see cref="EnvironmentCap"/> 과 같다). 연결 창이 고른 접속의 기본 상한.</summary>
    public static AiAccessLevel DefaultCap(ConnectionEnvironment environment) => EnvironmentCap(environment);

    /// <summary>
    /// 환경별 외부 상한: 운영 = <see cref="ProductionRuns"/> 면 QueryRows 아니면 Metadata, 그 밖 = QueryRows(곧 접속 실효 수준이 그대로 적용).
    /// </summary>
    public static AiAccessLevel EnvironmentCap(ConnectionEnvironment environment) =>
        environment == ConnectionEnvironment.Production && !ProductionRuns ? AiAccessLevel.Metadata : AiAccessLevel.QueryRows;

    /// <summary>그 환경에서 외부 에이전트가 문장을 실행해도 되는가: 개발·시험은 언제나, 운영은 <see cref="ProductionRuns"/> 일 때만.</summary>
    public static bool ExecutesIn(ConnectionEnvironment environment) =>
        environment != ConnectionEnvironment.Production || ProductionRuns;

    /// <summary>
    /// 그 접속에서 문장을 실행해도 되는가(<c>query</c>·<c>run_tab</c>·<c>export_result</c>): 수준 Query 이상이고 <see cref="ExecutesIn"/>.
    /// 문장 검사(한 문장 SELECT/WITH 등)는 따로 한다.
    /// </summary>
    public static bool MayExecute(AgentGrantConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Level >= AiAccessLevel.Query && ExecutesIn(connection.Environment);
    }

    /// <summary>
    /// 이 허가의 그 환경 호출을 「로컬 AI」로 보는가(하위 세션 <c>AiContext.LocalAi</c> 값): 허가가 <see cref="AgentGrantView.LocalModel"/> 이고
    /// 개발·시험이거나 <see cref="LocalModelCoversProduction"/>.
    /// </summary>
    public static bool UsesLocalModel(AgentGrantView grant, ConnectionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant.LocalModel && (environment != ConnectionEnvironment.Production || LocalModelCoversProduction);
    }

    /// <summary>처리자가 민감 열(<c>***</c>)·비밀 바인드 값을 가리는가 = 로컬 모델이 아니면 언제나.</summary>
    public static bool MasksSensitive(AgentGrantView grant, ConnectionEnvironment environment) => !UsesLocalModel(grant, environment);

    /// <summary>이력·오류 글·하위 세션의 리터럴을 가리는가 = 운영 접속이고 로컬 모델이 아닐 때.</summary>
    public static bool MasksLiterals(AgentGrantView grant, ConnectionEnvironment environment) =>
        environment == ConnectionEnvironment.Production && !UsesLocalModel(grant, environment);

    /// <summary>운영 탭 글을 줘도 되는가(E-18 유지): 로컬 모델이 운영까지 덮을 때만. 클라우드 모델은 운영 탭 글을 받지 않는다.</summary>
    public static bool CanReadProductionTabText(AgentGrantView grant) => UsesLocalModel(grant, ConnectionEnvironment.Production);

    /// <summary>연결 창에서 기본으로 체크하는가: 개발만(E-6).</summary>
    public static bool CheckedByDefault(ConnectionEnvironment environment) => environment == ConnectionEnvironment.Development;

    /// <summary>외부 허가에서 쓰는 수준 = min(접속 실효 수준, 상한). 상한도 환경 기본 상한을 넘지 못한다.</summary>
    public static AiAccessLevel LevelFor(ConnectionProfile profile, AiAccessLevel cap)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var effective = AiAccessPolicy.Effective(profile);
        var limit = DefaultCap(profile.Environment);
        var capped = Enum.IsDefined(cap) && cap < limit ? cap : limit;
        return effective < capped ? effective : capped;
    }

    /// <summary>허가 안 접속 하나를 만든다(밖으로 내도 되는 값만 옮긴다).</summary>
    public static AgentGrantConnection ToConnection(AgentGrantTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var profile = target.Profile;
        return new AgentGrantConnection(
            profile.Id,
            profile.Name,
            profile.Environment,
            profile.Dbms,
            LevelFor(profile, target.Cap),
            profile.ReadOnly,
            AiAccessPolicy.ExplainPlanEnabled(profile),
            AiAccessPolicy.MasksSensitiveColumns(profile));
    }
}
