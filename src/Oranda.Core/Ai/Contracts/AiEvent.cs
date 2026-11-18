using System.Text.Json;

namespace Oranda.Core.Ai.Contracts;

/// <summary>드라이버가 화면에 넘기는 사건(설계 §2.3). 경로(CLI·API)가 달라도 같은 사건으로 정규화한다.</summary>
public abstract record AiEvent;

/// <summary>대화가 시작되어 이을 수 있는 id 가 정해졌다. 중간에 멈춰도 이어 말하기가 가능하도록 끝(<see cref="AiFinal"/>)보다 먼저 낸다.</summary>
public sealed record AiStarted(string? ResumeId) : AiEvent;

/// <summary>응답 글 조각.</summary>
public sealed record AiTextDelta(string Text) : AiEvent;

/// <summary>LLM 이 도구를 불렀다. <paramref name="ArgumentsSummary"/> 는 화면 표시용 요약(긴 SQL 글은 줄인다).</summary>
public sealed record AiToolCall(string CallId, string ToolName, string ArgumentsSummary) : AiEvent;

/// <summary>도구 결과. <paramref name="Size"/> 는 LLM 에 돌려준 글자 수, <paramref name="Judgement"/> 는 게이트 판단.</summary>
public sealed record AiToolResult(string CallId, string ToolName, int Size, AiToolJudgement Judgement, string? Note = null) : AiEvent;

/// <summary>도구 호출에 대한 게이트 판단(화면 도구 카드 표시용). 감사 기록(<c>agent_audit.verdict</c>)에 이름 문자열로 저장되므로 이름을 바꾸지 않고 값은 맨 끝에 추가한다.</summary>
public enum AiToolJudgement
{
    /// <summary>요청대로 돌려줬다.</summary>
    Allowed,

    /// <summary>일부(끝값·민감 컬럼 등)를 가려서 돌려줬다.</summary>
    Masked,

    /// <summary>허용 수준·범위·정책 때문에 거부했다.</summary>
    Denied,

    /// <summary>허용됐지만 실행에 실패했다(DB 오류·시간 초과 등).</summary>
    Failed,
}

/// <summary>모호해서 사용자가 골라야 한다(<c>needs_choice</c>).</summary>
public sealed record AiNeedsChoice(string Question, IReadOnlyList<string> Options) : AiEvent;

/// <summary>끝. <paramref name="Result"/> 는 요청의 <c>OutputSchema</c> 를 따르는 구조 결과(없으면 null).</summary>
public sealed record AiFinal(string? Text, JsonElement? Result, string? ResumeId) : AiEvent;

/// <summary>실패. <paramref name="Code"/> 는 드라이버가 정한 짧은 구분 값(예: <c>cli_not_found</c>).</summary>
public sealed record AiError(string Message, string? Code = null) : AiEvent
{
    /// <summary>
    /// 가벼운 일 모델로 보낸 요청이 실패했다: 화면이 「주 모델로 다시 보내기」 단추를 붙인다(설계 §0.4·AE-1). 라우터는 몰래 다른 모델로 넘기지 않는다.
    /// 다시 보낼 때는 등급을 <see cref="AiTier.Main"/> 으로 바꾼 새 요청으로, 확인 창(보내는 곳·가림)을 다시 거친다.
    /// </summary>
    public bool RetryOnMain { get; init; }
}

/// <summary>
/// 라우터가 이 요청을 보낸 모델(설계 §1.2 「표시」). 드라이버 사건보다 먼저 한 번 낸다. 화면은 답 머리에 <paramref name="Label"/> 을 작게 적는다
/// (예: <c>haiku</c>·<c>Ollama qwen3:8b</c>). 채팅창 머리의 모델 표시는 주 모델이므로 턴마다 이 값을 따로 쓴다.
/// </summary>
/// <param name="Label">사용자에게 보일 모델 이름. 비밀 값(키·주소의 사용자 정보)은 넣지 않는다.</param>
/// <param name="Tier">실제로 보낸 등급.</param>
/// <param name="RequestedTier">요청이 원한 등급. 가벼운 일이 입력 크기 때문에 주 모델로 올라가면 <see cref="Tier"/> 와 다르다.</param>
public sealed record AiModelChosen(string Label, AiTier Tier, AiTier RequestedTier = AiTier.Main) : AiEvent
{
    /// <summary>가벼운 일 요청을 주 모델로 올려 보냈다(「입력이 커서 주 모델로 보냈습니다」 안내를 남길 때).</summary>
    public bool Escalated => RequestedTier == AiTier.Light && Tier == AiTier.Main;
}

/// <summary>토큰·비용 집계.</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens, decimal? CostUsd = null) : AiEvent;
