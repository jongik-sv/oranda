using Oranda.App.Shell.Composition;
using Oranda.Core.Language;
using Oranda.Core.MyBatis;
using Oranda.Core.Plans;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>
/// 조합 검사가 쓰는 서비스를 처음 쓸 때 꺼낸다. 계산기(<see cref="IMyBatisEvaluator"/>)는 Language 형이라 창·처리기 형이 시작 때 읽히지 않게
/// 이 형 안에만 둔다(design.md 9장 R21, 03-01 <c>MyBatisStatementExpander</c> 와 같은 방법). 시험은 <c>…Source</c> 를 넣는다.
/// </summary>
internal static class ComboServices
{
    /// <summary>시험용: 실행기를 직접 준다. null 이면 서비스 공급자에서 만든다.</summary>
    internal static Func<ComboCheckRunner?>? RunnerSource { get; set; }

    /// <summary>시험용: 시나리오 저장소를 직접 준다.</summary>
    internal static Func<IMyBatisScenarioStore?>? ScenarioSource { get; set; }

    /// <summary>실행기. 계산기나 언어 서비스가 없으면 null.</summary>
    public static ComboCheckRunner? Runner(IServiceProvider? services)
    {
        if (RunnerSource is { } source)
        {
            return source();
        }

        if (services is null)
        {
            return null;
        }

        // 계산기 구현은 지연 어셈블리(Oranda.Language)에 있고 목록형 해석은 그것을 깨우지 않으므로, 검사할 때 한 번 훑는다.
        (services as ServiceContainer)?.ScanDeferred();
        var evaluator = services.GetService(typeof(IEnumerable<IMyBatisEvaluator>)) is IEnumerable<IMyBatisEvaluator> found ? found.LastOrDefault() : null;
        if (evaluator is null || services.GetService(typeof(ILanguageService)) is not ILanguageService language)
        {
            return null;
        }

        return new ComboCheckRunner(evaluator, language, services.GetService(typeof(IExecutionPlanService)) as IExecutionPlanService);
    }

    /// <summary>시나리오 저장소. 없으면 null(시나리오 없이 조합만 검사).</summary>
    public static IMyBatisScenarioStore? Scenarios(IServiceProvider? services) =>
        ScenarioSource is { } source ? source() : services?.GetService(typeof(IMyBatisScenarioStore)) as IMyBatisScenarioStore;
}
