using Oranda.App.Shell;
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

/// <summary>모든 식을 같은 값으로 계산하는 가짜 문맥 키.</summary>
public sealed class FakeContextKeys(bool value = true) : IContextKeys
{
    public static FakeContextKeys AllTrue { get; } = new(true);

    public static FakeContextKeys AllFalse { get; } = new(false);

    public bool Evaluate(string? expression) => string.IsNullOrEmpty(expression) || value;
}
