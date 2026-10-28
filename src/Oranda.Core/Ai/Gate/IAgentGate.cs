using System.Text.Json.Nodes;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 도구 호출 관문(<c>AgentGate</c>). 다리(<c>AgentBridgeHost</c>)가 hello 로 실행 토큰을 <see cref="AgentSession"/> 으로 바꾼 뒤 이것만 부른다.
/// 판단 순서: 알려진 도구 → 접속(<c>connection</c> 인자) → 허용 수준 → 접속 설정(EXPLAIN PLAN·운영 제한) → 범위(참조 객체·상대·보낼 범위)
/// → 처리자 실행 → 결과 가림(히스토그램 끝값) → 감사 기록. 거부는 예외가 아니라 <c>IsError</c> 결과로 돌려준다.
/// </summary>
public interface IAgentGate
{
    /// <summary>이 실행의 허용 수준에서 쓸 수 있는 도구 목록(<c>oranda.listTools</c> 응답).</summary>
    IReadOnlyList<AgentToolInfo> ListTools(AgentSession session);

    Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct);

    /// <summary>도구 호출이 끝날 때마다(거부 포함) 난다. 대화 창의 도구 카드가 판단·크기를 맞춘다.</summary>
    event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;
}

/// <summary>게이트 응답: 파이프로 돌려줄 결과와, 화면에 보일 판단.</summary>
public sealed record AgentGateResponse(AgentCallToolResult Result, AiToolJudgement Judgement, string? Note = null);

/// <summary><see cref="IAgentGate.ToolCompleted"/> 인자.</summary>
public sealed class AgentToolActivityEventArgs : EventArgs
{
    public AgentToolActivityEventArgs(string runId, long sequence, string toolName, AiToolJudgement judgement, int size, string? note)
    {
        RunId = runId;
        Sequence = sequence;
        ToolName = toolName;
        Judgement = judgement;
        Size = size;
        Note = note;
    }

    public string RunId { get; }

    /// <summary>그 실행 안에서 도구 호출이 시작된 순서(<see cref="AgentSession.NextSequence"/>). 같은 이름의 호출을 병렬로 해도 화면 카드와 짝지을 수 있다.</summary>
    public long Sequence { get; }

    public string ToolName { get; }

    public AiToolJudgement Judgement { get; }

    /// <summary>돌려준 글자 수.</summary>
    public int Size { get; }

    public string? Note { get; }
}
