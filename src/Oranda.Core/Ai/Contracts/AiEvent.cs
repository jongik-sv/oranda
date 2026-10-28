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
public sealed record AiError(string Message, string? Code = null) : AiEvent;

/// <summary>토큰·비용 집계.</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens, decimal? CostUsd = null) : AiEvent;
