using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 도구 호출 감사 기록 저장소(<c>oranda.db</c> 의 <c>agent_audit</c> 표, 마이그레이션 0004). 구현은 ai-bridge.
/// 비밀·결과 행 값·히스토그램 끝값을 담지 않는다 — 인자 요약과 판단만.
/// </summary>
public interface IAgentAuditStore
{
    /// <summary>
    /// 한 줄을 추가한다. 구현은 실패를 예외로 던진다. 게이트는 이를 잡아 도구 결과를 그대로 돌려주고(감사 실패가 조회를 막지 않는다)
    /// 실패를 화면 사건의 메모에 남긴다. 따라서 감사가 빠질 수 있다.
    /// </summary>
    ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct);

    /// <summary>최근 기록(최신 먼저). <paramref name="profileId"/> 가 있으면 그 접속만.</summary>
    ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct);
}

/// <summary><c>agent_audit</c> 한 줄. 속성과 열이 일대일이다.</summary>
public sealed record AgentAuditEntry
{
    /// <summary>열 <c>id</c>. 추가할 때는 0.</summary>
    public long Id { get; init; }

    /// <summary>열 <c>occurred_at</c>(UTC).</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>열 <c>profile_id</c>.</summary>
    public Guid? ProfileId { get; init; }

    /// <summary>열 <c>profile_name</c>.</summary>
    public string? ProfileName { get; init; }

    /// <summary>열 <c>client</c>: hello 의 클라이언트 이름.</summary>
    public string? Client { get; init; }

    /// <summary>열 <c>run_id</c>.</summary>
    public string? RunId { get; init; }

    /// <summary>열 <c>tool</c>: 도구 이름(모르는 이름 요청도 그대로 남긴다).</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>열 <c>arguments</c>: 인자 요약. 긴 SQL 글은 앞부분만 남기고, 값이 될 수 있는 리터럴은 남기지 않는다.</summary>
    public string? Arguments { get; init; }

    /// <summary>열 <c>access_level</c>: 호출 시점의 실효 허용 수준.</summary>
    public AiAccessLevel AccessLevel { get; init; }

    /// <summary>열 <c>verdict</c>(이름 문자열로 저장).</summary>
    public AiToolJudgement Verdict { get; init; }

    /// <summary>열 <c>reason</c>: 거부·가림·실패 사유(짧은 코드 또는 문장).</summary>
    public string? Reason { get; init; }

    /// <summary>열 <c>result_chars</c>: 돌려준 글자 수.</summary>
    public int? ResultChars { get; init; }

    /// <summary>열 <c>elapsed_ms</c>.</summary>
    public long? ElapsedMs { get; init; }
}
