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
/// <b>실행·계획에 쓸 문장은 <c>Arguments["sql"]</c>(와 <c>variants[].sql</c>)뿐이다</b> — 게이트가 검사하고(필요하면 풀어 쓴 글로 바꾸고) 채운 글이다.
/// <c>get_tuning_context</c> 에서 <c>sql</c> 인자가 없으면 게이트가 요청의 대상 문장을 같은 검사로 확인해 <c>sql</c> 에 채워 넘기므로 처리자는 <c>Session.Context.StatementText</c> 를 직접 쓰지 않는다.
/// <see cref="Arguments"/> 는 게이트가 스키마에 선언된 키만(대소문자까지 정확히) 골라 새로 만든 객체라 모르는 키·중복 키는 들어오지 않는다.
/// <b>sql_id 도구</b>(<c>get_cursor_plan</c>·<c>get_sql_stats</c>)는 파싱 스키마가 실행 범위의 스키마인 커서만 골라 조회한다. 같은 글을 다른 사용자가 파싱한 자식 커서의 계획·통계를 내지 않는다.
/// </summary>
/// <param name="Session">진행 중인 AI 요청.</param>
/// <param name="Db">사용자 작업 세션이 아닌 에이전트용 DB 세션(<see cref="IAgentSessionSource.AcquireDbSessionAsync"/>).</param>
/// <param name="Level">접속의 실효 허용 수준.</param>
/// <param name="Arguments">도구 인자(스키마의 snake_case 이름).</param>
/// <param name="Resolved">게이트가 해석한 객체 인자: 인자 이름(<c>object</c>·<c>table</c>) → 실제 객체. 처리자는 이 값을 쓴다.</param>
/// <param name="ExplainPlanAllowed">
/// 접속의 EXPLAIN PLAN 허용(허용 수준 Plan 이상이고 접속 설정이 켜져 있음). 처리자는 <c>EXPLAIN PLAN</c> 문을 보내는 등
/// 이 허용이 필요한 하위 동작(예: <c>get_tuning_context</c> 의 예상 계획)을 이 값이 참일 때만 한다.
/// </param>
/// <param name="MayExecuteSql">
/// 문장 실행 허용(허용 수준 Query 이상이고 운영 접속이 아님). 처리자는 <c>GATHER_PLAN_STATISTICS</c> 사본을 실행하는 등
/// 이 허용이 필요한 하위 동작을 이 값이 참일 때만 한다. 실행할 문장을 게이트가 검사했을 때만 참이다 —
/// <c>sql</c>·<c>variants</c> 가 있거나 <c>get_tuning_context</c> 가 대상 문장으로 검사된 경우이고, <c>sql_id</c> 만 받은 호출은 거짓이다.
/// </param>
/// <param name="ReadOnlyExecution">
/// 실행할 문장에 사용자 함수·패키지 함수가 있고 게이트가 확인을 통과시켰다. 처리자는 실행을 읽기 전용 트랜잭션 안에서만 한다
/// (함수 안 DML 은 DB 가 막는다). 함수가 없는 문장은 거짓이다.
/// </param>
public sealed record AgentToolInvocation(
    AgentSession Session,
    ISession Db,
    AiAccessLevel Level,
    JsonObject Arguments,
    IReadOnlyDictionary<string, AgentObjectRef> Resolved,
    bool ExplainPlanAllowed = false,
    bool MayExecuteSql = false,
    bool ReadOnlyExecution = false);

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
