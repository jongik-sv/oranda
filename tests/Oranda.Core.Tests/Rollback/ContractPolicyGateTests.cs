using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;
using Oranda.Core.Text;
using Oranda.Testing;
using Oranda.Testing.Rollback;

namespace Oranda.Core.Tests.Rollback;

/// <summary>정책 관문이 확인 창의 답(<see cref="PolicyGateResult.Answer"/>)을 채우는 경우 넷과 거절(되돌리기 지점 설계 13.3·16.1).</summary>
public class ContractPolicyGateTests
{
    private static readonly SqlStatement Update = new("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 1", default(TextRange), StatementKind.Dml);

    private static readonly ConfirmExtras Offer = new(new RollbackOffer(true, NotCreatedReason.None) { DefaultOn = true, EstimatedRows = 12 }, null);

    private sealed class FixedPolicy(PolicyDecisionKind kind) : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => new(new PolicyDecision(kind));
    }

    private static Task<PolicyGateResult> Decide(PolicyDecisionKind kind, ConnectionEnvironment env, IExecutionInteraction interaction,
                                                 ExecutionOptions? options = null) =>
        PolicyGate.DecideAsync(new FixedPolicy(kind), interaction, new FakeEventBus(), new FakeSession(TestData.Profile(env)),
                               Update, options ?? ExecutionOptions.Default, Offer, CancellationToken.None);

    [Fact]
    public async Task Allow_answers_default_of_offer()
    {
        var r = await Decide(PolicyDecisionKind.Allow, ConnectionEnvironment.Production, new RecordingRollbackInteraction());
        Assert.Equal(PolicyGateOutcome.Proceed, r.Outcome);
        Assert.Equal(new ConfirmAnswer(true, true, null), r.Answer);
    }

    [Fact]
    public async Task Development_skip_answers_default_without_asking()
    {
        var ask = new RecordingRollbackInteraction();
        var r = await Decide(PolicyDecisionKind.Confirm, ConnectionEnvironment.Development, ask);
        Assert.Equal(PolicyGateOutcome.Proceed, r.Outcome);
        Assert.True(r.Answer.CreateScript);
        Assert.Empty(ask.Asked);
    }

    [Fact]
    public async Task PreConfirmed_answers_default_without_asking()
    {
        var ask = new RecordingRollbackInteraction();
        var options = new ExecutionOptions { PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { Update.Text } };
        var r = await Decide(PolicyDecisionKind.Confirm, ConnectionEnvironment.Production, ask, options);
        Assert.Equal(PolicyGateOutcome.Proceed, r.Outcome);
        Assert.Equal(ConfirmAnswer.Default(Offer), r.Answer);
        Assert.Empty(ask.Asked);
    }

    [Fact]
    public async Task Asked_passes_extras_and_returns_user_answer()
    {
        var ask = new RecordingRollbackInteraction { Answer = new ConfirmAnswer(true, false, "CREATE TABLE ORB_X AS SELECT * FROM X") };
        var r = await Decide(PolicyDecisionKind.Confirm, ConnectionEnvironment.Production, ask);
        Assert.Equal(PolicyGateOutcome.Proceed, r.Outcome);
        Assert.Same(ask.Answer, r.Answer);
        Assert.Same(Offer, Assert.Single(ask.Asked).Extras);
    }

    [Fact]
    public async Task Declined_answer_cancels_and_deny_answers_declined()
    {
        var declined = await Decide(PolicyDecisionKind.Confirm, ConnectionEnvironment.Production,
                                    new RecordingRollbackInteraction { Answer = ConfirmAnswer.Declined });
        Assert.Equal(PolicyGateOutcome.Cancelled, declined.Outcome);
        Assert.Equal(ConfirmAnswer.Declined, declined.Answer);

        var denied = await Decide(PolicyDecisionKind.Deny, ConnectionEnvironment.Production, new RecordingRollbackInteraction());
        Assert.Equal(PolicyGateOutcome.Denied, denied.Outcome);
        Assert.False(denied.Answer.Proceed);
    }

    [Fact]
    public async Task Default_ConfirmChangeAsync_maps_ConfirmAsync()
    {
        IExecutionInteraction yes = new FakeExecutionInteraction { ConfirmAnswer = true };
        IExecutionInteraction no = new FakeExecutionInteraction { ConfirmAnswer = false };
        var decision = new PolicyDecision(PolicyDecisionKind.Confirm);

        Assert.Equal(ConfirmAnswer.Default(Offer), await yes.ConfirmChangeAsync(Update, decision, Offer, CancellationToken.None));
        Assert.Equal(ConfirmAnswer.Declined, await no.ConfirmChangeAsync(Update, decision, Offer, CancellationToken.None));
    }

    [Fact]
    public async Task Old_overload_passes_no_extras()
    {
        var r = await PolicyGate.DecideAsync(new FixedPolicy(PolicyDecisionKind.Allow), new FakeExecutionInteraction(), new FakeEventBus(),
                                             new FakeSession(), Update, ExecutionOptions.Default, CancellationToken.None);
        Assert.Equal(new ConfirmAnswer(true, false, null), r.Answer);
        Assert.Equal(ConfirmAnswer.Declined, default(PolicyGateResult).Answer);
    }
}
