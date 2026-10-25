using Oranda.Core.Connection;
using Oranda.Core.History;

namespace Oranda.Core.Execution;

/// <summary>
/// 정책 관문(TRD 4.9, SAFE). 규칙 순서: 읽기 전용 → 조회 전용 Role(TRD 10.7.4) → 라이선스 → 운영 DML/DDL 확인 → WHERE 없는 DML → 위험 명령.
/// 구현: Oranda.Core <c>ExecutionPolicy</c>(TSK-06-01).
/// </summary>
public interface IExecutionPolicy
{
    ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct);
}

/// <summary>정책 판단에 필요한 실행 환경.</summary>
public sealed record PolicyContext(ConnectionProfile Profile, ServerInfo Server, HistorySource Source)
{
    public LicensePack RequiredLicense { get; init; } = LicensePack.None;
}

public enum PolicyDecisionKind
{
    Allow,
    Confirm,
    Deny,
}

/// <summary>판단을 낸 규칙.</summary>
public enum PolicyRule
{
    None,
    ReadOnlyProfile,

    /// <summary>조회 전용 Role 하나만 고름(D-006, TSK-07-05). 읽기 전용 프로필과 같은 기준으로 쓰기 문장을 거부한다.</summary>
    ReadOnlyRole,
    License,
    ProductionChange,
    UnfilteredDml,
    DangerousCommand,
}

/// <summary>
/// 정책 판단. <see cref="PolicyDecisionKind.Confirm"/> 이면 실행 엔진이 <see cref="IExecutionInteraction"/> 으로 묻는다.
/// <see cref="RetypeText"/> 가 있으면 사용자가 그 이름을 다시 입력해야 통과(위험 명령).
/// </summary>
public sealed record PolicyDecision(PolicyDecisionKind Kind, PolicyRule Rule = PolicyRule.None, string? Message = null)
{
    public static PolicyDecision Allowed { get; } = new(PolicyDecisionKind.Allow);

    public string? RetypeText { get; init; }
}
