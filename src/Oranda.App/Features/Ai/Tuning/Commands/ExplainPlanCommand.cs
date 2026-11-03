using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Ai.Access;

namespace Oranda.App.Features.Ai.Tuning.Commands;

/// <summary>
/// 「실행 계획 해설」 명령의 자리(스텁). 내용은 <c>cmd-explain-plan</c> 항목이 채우고 <see cref="Enablement"/> 를 <c>TuneEnablement &amp;&amp; aiAccessPlan</c> 으로 바꿔 명령을 켠다.
/// 그때까지 메뉴는 <c>aiNotYet</c>(늘 거짓)이라 비활성이고, 어떻게든 불려도 <see cref="PrepareAsync"/> 가 보내지 않는다.
/// </summary>
public sealed class ExplainPlanCommand : AiCommandSpec
{
    /// <summary>메뉴 활성 식. 이 명령을 켜는 항목이 이 상수만 바꾼다(<c>AiCommands.cs</c> 는 고치지 않는다).</summary>
    public const string Enablement = "aiNotYet";

    public static ExplainPlanCommand Instance { get; } = new();

    public override string Id => AiCommands.ExplainPlan;

    public override string ConversationTitle => "실행 계획 해설";

    public override string UserLine => "실행 계획 해설";

    public override AiAccessLevel AccessCeiling => AiAccessLevel.Plan;

    public override string ResponseRules => string.Empty;

    public override Task<AiCommandPreparation> PrepareAsync(AiCommandStartEnv env, CancellationToken ct) =>
        Task.FromResult(AiCommandPreparation.Refuse("실행 계획 해설은 아직 쓸 수 없습니다."));

    public override string BuildInstruction(AiCommandPromptInput input) => string.Empty;
}
