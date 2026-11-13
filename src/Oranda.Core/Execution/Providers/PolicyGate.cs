using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Rollback;
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
public readonly record struct PolicyGateResult(PolicyGateOutcome Outcome, PolicyDecision Decision, PolicyDecision? Applied)
{
    private readonly ConfirmAnswer? _answer;

    /// <summary>
    /// 확인 창의 답(되돌리기 지점 설계 13.3). 묻지 않고 진행(허용·개발 접속 생략·미리 확인) = <see cref="ConfirmAnswer.Default"/>,
    /// 물었으면 사용자의 답, 거부·취소 = <see cref="ConfirmAnswer.Declined"/>. 채우지 않은 값도 <see cref="ConfirmAnswer.Declined"/> 로 읽힌다.
    /// </summary>
    public ConfirmAnswer Answer { get => _answer ?? ConfirmAnswer.Declined; init => _answer = value; }
}

/// <summary>
/// 정책 관문 단계(TRD 4.9). DBMS 공급자의 실행·대량 실행 서비스(Oracle: <c>OracleExecutionService</c>·<c>OracleBulkExecutionService</c>)가 함께 쓴다.
/// 판단을 <see cref="PolicyDecisionMade"/> 로 알린 뒤, 거부면 그대로 끝내고 확인이면 사용자에게 묻는다.
/// 호출한 쪽이 미리 받은 확인(<see cref="ExecutionOptions.PreConfirmed"/>)에 같은 문장 텍스트(서수 비교)가 있으면 묻지 않는다.
/// 미리 받은 확인은 Confirm 에만 쓴다. Deny 는 어떤 경우에도 넘지 못한다.
/// 세션 접속의 환경이 <see cref="ConnectionEnvironment.Development"/> 이면 Confirm 을 묻지 않고 진행한다(D-065, 사용자 요청 2026-11-11).
/// 그때 결과에 싣는 판단은 <see cref="PolicyDecision.ConfirmationSkipped"/> 가 켜져 있다. 판단 자체(이벤트·사전 점검)는 그대로 Confirm 이다.
/// </summary>
public static class PolicyGate
{
    /// <param name="stmt">드라이버로 보낼 문장(치환 변수 처리 뒤). 미리 받은 확인은 이 텍스트와 비교한다.</param>
    public static Task<PolicyGateResult> DecideAsync(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                                     ISession s, SqlStatement stmt, ExecutionOptions options, CancellationToken ct) =>
        DecideAsync(policy, interaction, events, s, stmt, options, ConfirmExtras.None, ct);

    /// <summary>
    /// 원복 스크립트·백업 테이블 제안(<paramref name="extras"/>)을 확인 창에 덧붙이는 판(되돌리기 지점 설계 6.1 의 3단계).
    /// 확인을 물을 때 <see cref="IExecutionInteraction.ConfirmChangeAsync"/> 를 부르고, 답이 진행이 아니면 취소로 끝낸다.
    /// </summary>
    /// <param name="stmt">드라이버로 보낼 문장(치환 변수 처리 뒤). 미리 받은 확인은 이 텍스트와 비교한다.</param>
    /// <param name="extras">확인 창에 덧붙일 제안. 없으면 <see cref="ConfirmExtras.None"/></param>
    public static async Task<PolicyGateResult> DecideAsync(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                                             ISession s, SqlStatement stmt, ExecutionOptions options, ConfirmExtras extras,
                                                             CancellationToken ct)
    {
        extras ??= ConfirmExtras.None;
        var ctx = new PolicyContext(s.Profile, s.Server, options.Source) { RequiredLicense = options.RequiredLicense };
        var decision = await policy.EvaluateAsync(ctx, stmt, ct);
        events.Publish(new PolicyDecisionMade(s.Id, stmt, decision));
        switch (decision.Kind)
        {
            case PolicyDecisionKind.Allow:
                return new PolicyGateResult(PolicyGateOutcome.Proceed, decision, null) { Answer = ConfirmAnswer.Default(extras) };
            case PolicyDecisionKind.Confirm:
                if (s.Profile.Environment == ConnectionEnvironment.Development)
                {
                    return new PolicyGateResult(PolicyGateOutcome.Proceed, decision, decision with { ConfirmationSkipped = true })
                    {
                        Answer = ConfirmAnswer.Default(extras),
                    };
                }

                if (IsPreConfirmed(options, stmt))
                {
                    return new PolicyGateResult(PolicyGateOutcome.Proceed, decision, decision) { Answer = ConfirmAnswer.Default(extras) };
                }

                var answer = await interaction.ConfirmChangeAsync(stmt, decision, extras, ct);
                if (answer is not { Proceed: true })
                {
                    return new PolicyGateResult(PolicyGateOutcome.Cancelled, decision, null) { Answer = ConfirmAnswer.Declined };
                }

                return new PolicyGateResult(PolicyGateOutcome.Proceed, decision, decision) { Answer = answer };
            default:
                // Deny 와 앞으로 더할 판단 종류는 모두 거부로 본다(보수 원칙).
                return new PolicyGateResult(PolicyGateOutcome.Denied, decision, null) { Answer = ConfirmAnswer.Declined };
        }
    }

    /// <summary>
    /// 집합이 어떤 비교자로 만들어졌든(대소문자 무시 등) 서수로 같은 텍스트만 인정한다.
    /// 집합은 사전 점검이 보인 문장 수만큼이라 순회해도 된다.
    /// </summary>
    private static bool IsPreConfirmed(ExecutionOptions options, SqlStatement stmt) =>
        options.PreConfirmed is { Count: > 0 } set && set.Any(t => string.Equals(t, stmt.Text, StringComparison.Ordinal));
}
