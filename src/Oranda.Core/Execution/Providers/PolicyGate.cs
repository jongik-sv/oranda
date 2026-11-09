using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.Core.Execution.Providers;

/// <summary>정책 관문 단계의 결과. <see cref="Proceed"/> 면 드라이버로 보낸다.</summary>
public enum PolicyGateOutcome
{
    Proceed,
    Denied,
    Cancelled,
}

/// <param name="Outcome">진행·거부·취소</param>
/// <param name="Decision">정책이 낸 판단</param>
/// <param name="Applied">확인을 거쳐 진행한 경우의 판단(결과의 <c>Policy</c> 에 싣는다). 그냥 허용이면 null</param>
public readonly record struct PolicyGateResult(PolicyGateOutcome Outcome, PolicyDecision Decision, PolicyDecision? Applied);

/// <summary>
/// 정책 관문 단계(TRD 4.9). DBMS 공급자의 실행·대량 실행 서비스(Oracle: <c>OracleExecutionService</c>·<c>OracleBulkExecutionService</c>)가 함께 쓴다.
/// 판단을 <see cref="PolicyDecisionMade"/> 로 알린 뒤, 거부면 그대로 끝내고 확인이면 사용자에게 묻는다.
/// 호출한 쪽이 미리 받은 확인(<see cref="ExecutionOptions.PreConfirmed"/>)에 같은 문장 텍스트(서수 비교)가 있으면 묻지 않는다.
/// 미리 받은 확인은 Confirm 에만 쓴다. Deny 는 어떤 경우에도 넘지 못한다.
/// </summary>
public static class PolicyGate
{
    /// <param name="stmt">드라이버로 보낼 문장(치환 변수 처리 뒤). 미리 받은 확인은 이 텍스트와 비교한다.</param>
    public static async Task<PolicyGateResult> DecideAsync(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                                             ISession s, SqlStatement stmt, ExecutionOptions options, CancellationToken ct)
    {
        var ctx = new PolicyContext(s.Profile, s.Server, options.Source) { RequiredLicense = options.RequiredLicense };
        var decision = await policy.EvaluateAsync(ctx, stmt, ct);
        events.Publish(new PolicyDecisionMade(s.Id, stmt, decision));
        switch (decision.Kind)
        {
            case PolicyDecisionKind.Allow:
                return new PolicyGateResult(PolicyGateOutcome.Proceed, decision, null);
            case PolicyDecisionKind.Confirm:
                if (!IsPreConfirmed(options, stmt) && !await interaction.ConfirmAsync(stmt, decision, ct))
                {
                    return new PolicyGateResult(PolicyGateOutcome.Cancelled, decision, null);
                }

                return new PolicyGateResult(PolicyGateOutcome.Proceed, decision, decision);
            default:
                // Deny 와 앞으로 더할 판단 종류는 모두 거부로 본다(보수 원칙).
                return new PolicyGateResult(PolicyGateOutcome.Denied, decision, null);
        }
    }

    /// <summary>
    /// 집합이 어떤 비교자로 만들어졌든(대소문자 무시 등) 서수로 같은 텍스트만 인정한다.
    /// 집합은 사전 점검이 보인 문장 수만큼이라 순회해도 된다.
    /// </summary>
    private static bool IsPreConfirmed(ExecutionOptions options, SqlStatement stmt) =>
        options.PreConfirmed is { Count: > 0 } set && set.Any(t => string.Equals(t, stmt.Text, StringComparison.Ordinal));
}
