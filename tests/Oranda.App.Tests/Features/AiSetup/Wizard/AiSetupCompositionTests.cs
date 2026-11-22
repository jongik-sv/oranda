using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Tests.Features.AiSetup.Wizard;

/// <summary>실제 조립: 런처·단계 팩터리(방법 고르기·설치·로그인)·claude 준비 서비스가 붙어 창이 「준비 중」 없이 갈래 A 앞 단계를 그린다. claude 는 부르지 않는다.</summary>
public sealed class AiSetupCompositionTests
{
    [Fact]
    public void Real_container_wires_the_launcher_steps_and_the_claude_service()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-aisetup-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        Assert.IsType<AiSetupLauncher>(container.GetRegisteredOrDefault<IAiSetupLauncher>());
        Assert.NotNull(container.GetRegisteredOrDefault<IClaudeSetupService>());
        var steps = container.GetService<IEnumerable<IAiSetupStepFactory>>()!.Select(f => f.StepId).ToList();
        Assert.Contains(AiSetupStepId.Choose, steps);
        Assert.Contains(AiSetupStepId.Install, steps);
        Assert.Contains(AiSetupStepId.Login, steps);
        Assert.Equal(steps.Count, steps.Distinct().Count());
    }
}
