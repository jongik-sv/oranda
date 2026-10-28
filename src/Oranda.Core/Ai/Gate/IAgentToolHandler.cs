using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Sessions;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 도구 하나의 실제 처리(기존 <c>IMetadataProvider</c>·<c>ITuningService</c>·<c>IExecutionPlanService</c> 위의 얇은 어댑터).
/// 허용 수준·범위·감사·가림은 <c>AgentGate</c> 가 하므로 처리자는 <b>요청받은 만큼 조회해 결과 JSON 을 만들기만</b> 한다.
/// 도구 이름마다 처리자 하나를 컨테이너에 등록한다(<see cref="ToolName"/> 으로 게이트가 찾는다).
/// </summary>
public interface IAgentToolHandler
{
    /// <summary><c>AgentToolNames</c> 의 값.</summary>
    string ToolName { get; }

    ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct);
}

/// <summary>
/// 게이트가 처리자에게 넘기는 호출 정보. 인자는 이미 스키마 모양·허용 수준·범위 검사를 지났다.
/// <b>객체 이름 인자(<c>object</c>·<c>table</c>)는 게이트가 범위 검사에 쓴 실제 <c>OWNER.NAME</c> 으로 바꿔서 넘긴다</b> —
/// 처리자가 원래 글을 다시 풀면 검사한 객체와 다른 객체(다른 현재 스키마·동의어)를 읽을 수 있으므로, 받은 이름을 그대로 쓴다.
/// <see cref="Arguments"/> 는 게이트가 스키마에 선언된 키만(대소문자까지 정확히) 골라 새로 만든 객체라 모르는 키·중복 키는 들어오지 않는다.
/// <b>sql_id 도구</b>(<c>get_cursor_plan</c>·<c>get_sql_stats</c>)는 파싱 스키마가 실행 범위의 스키마인 커서만 골라 조회한다. 같은 글을 다른 사용자가 파싱한 자식 커서의 계획·통계를 내지 않는다.
/// </summary>
/// <param name="Session">진행 중인 AI 요청.</param>
/// <param name="Db">사용자 작업 세션이 아닌 에이전트용 DB 세션(<see cref="IAgentSessionSource.AcquireDbSessionAsync"/>).</param>
/// <param name="Level">접속의 실효 허용 수준.</param>
/// <param name="Arguments">도구 인자(스키마의 snake_case 이름).</param>
/// <param name="Resolved">게이트가 해석한 객체 인자: 인자 이름(<c>object</c>·<c>table</c>) → 실제 객체. 처리자는 이 값을 쓴다.</param>
public sealed record AgentToolInvocation(
    AgentSession Session,
    ISession Db,
    AiAccessLevel Level,
    JsonObject Arguments,
    IReadOnlyDictionary<string, AgentObjectRef> Resolved);

/// <summary>처리 결과. 실패는 예외 대신 <see cref="Error"/> 로 돌려 게이트가 감사 기록에 <c>Failed</c> 로 남기게 한다.</summary>
public sealed record AgentToolOutput
{
    /// <summary>LLM 에 돌려줄 결과(설계 §5.2·§5.3 의 모양). 실패면 null.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>실패 사유. 있으면 실패다.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>이 결과로 이 요청에서 조회해도 되게 된 sql_id 들(예: 벤치마크로 실행해 본 문장). 게이트는 결과 본문을 훑어 sql_id 를 모으지 않으므로, 넓히려면 여기에만 적는다.</summary>
    public IReadOnlyList<string> SqlIds { get; init; } = [];

    public bool IsError => ErrorMessage is not null;

    public static AgentToolOutput Ok(JsonNode result) => new() { Result = result };

    public static AgentToolOutput Error(string message) => new() { ErrorMessage = message };
}
