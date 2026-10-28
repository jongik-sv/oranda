using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Composition;
using Oranda.Core.Features;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>
/// 공용 시험 헬퍼 (TSK-02-05). 기능 Task 시험이 명령·도구 창 등록을 확인할 때 쓴다.
/// </summary>
public static class ShellTestHelpers
{
    private static readonly Lazy<ShellContributions> App = new(() => ShellContributions.Scan(typeof(ShellContributions).Assembly));

    /// <summary>Oranda.App 어셈블리 스캔 결과(한 번만 스캔).</summary>
    public static ShellContributions AppContributions => App.Value;

    /// <summary>App 어셈블리 기여로 만든 레지스트리(개발 빌드 기준).</summary>
    public static ToolPanelRegistry CreatePanelRegistry() => new(AppContributions.Panels, includeDevBuildOnly: true);

    public static CommandRegistry CreateCommandRegistry() => new(AppContributions.Commands);

    /// <summary>App 에 등록된 도구 창 하나를 찾는다. 없으면 시험 실패.</summary>
    public static ToolPanelDefinition RequirePanel(string id) =>
        AppContributions.Panels.SingleOrDefault(p => p.Id == id)
        ?? throw new Xunit.Sdk.XunitException($"도구 창 '{id}' 가 등록되지 않았다.");
}

/// <summary>
/// 직접 켜는(opt-in) 기능에 매인 항목 판별. 「모든 기능」·「features 섹션 없음」 기준선에는 이 항목들이 들지 않는다(예: aiTuning).
/// 다음 opt-in 기능이 들어와도 시험 기대값을 손으로 고치지 않도록 <see cref="FeatureCatalog"/> 의 OptIn 표시로 가른다.
/// </summary>
public static class OptInItems
{
    /// <summary>등록 id·형이 opt-in 기능 소속이다.</summary>
    public static bool Is(string? registrationId, Type type) =>
        FeatureCatalog.FeatureFor(registrationId, type) is { } feature && FeatureCatalog.Find(feature) is { OptIn: true };

    public static IEnumerable<CommandRegistration> Without(IEnumerable<CommandRegistration> commands) => commands.Where(c => !Is(c.Definition.Id, c.HandlerType));

    public static IEnumerable<ToolPanelDefinition> Without(IEnumerable<ToolPanelDefinition> panels) => panels.Where(p => !Is(p.Id, p.ViewModelType));

    /// <summary>opt-in 기능 소속 시작 작업 이름(기능이 꺼져 있으면 건너뛴다).</summary>
    public static IReadOnlyList<string> StartupTaskNames { get; } =
        [.. typeof(AppComposition).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IStartupTask).IsAssignableFrom(t) && Is(null, t))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)];
}

/// <summary>모든 식을 같은 값으로 계산하는 가짜 문맥 키.</summary>
public sealed class FakeContextKeys(bool value = true) : IContextKeys
{
    public static FakeContextKeys AllTrue { get; } = new(true);

    public static FakeContextKeys AllFalse { get; } = new(false);

    public bool Evaluate(string? expression) => string.IsNullOrEmpty(expression) || value;
}
