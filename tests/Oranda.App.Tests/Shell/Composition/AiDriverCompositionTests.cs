using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Shell.Composition;

/// <summary>
/// 조립 시험: 실제 컨테이너에서 <see cref="IAiDriver"/> 를 꺼내면 활성 프로필로 경로를 고르는 라우터다.
/// <c>ServiceContainer</c> 는 같은 서비스 형식이 둘 이상이면 훑는 순서로 마지막 등록을 돌려주므로, CliDriver 가 다시 IAiDriver 로 등록되면 라우터가 조용히 가려진다.
/// 라우터가 CliDriver 를 생성자 주입으로 받는 연결(순환 없음)도 함께 확인한다. RoutingAiDriver 는 Platform 안쪽 형식이라 이름으로 단정한다.
/// </summary>
public class AiDriverCompositionTests
{
    [Fact]
    public void Real_container_resolves_the_routing_driver_for_IAiDriver()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-aidriver-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var driver = container.GetService<IAiDriver>();

        Assert.NotNull(driver);
        Assert.Equal("RoutingAiDriver", driver.GetType().Name);
        Assert.True(driver.Capabilities.Images);

        // 라우터는 손상 복구 신호를 실제 설정 저장소 인스턴스에서 읽는다(DI 의 ISettingsStore 가 신호도 구현해야 한다).
        Assert.IsAssignableFrom<IAiSettingsResetSignal>(container.GetService<ISettingsStore>());
    }

    [Fact]
    public void Real_container_registers_the_antigravity_driver_as_a_system_cli_driver_and_not_as_IAiDriver()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-aidriver-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var drivers = container.GetService<IEnumerable<Oranda.Platform.Ai.ISystemCliAiDriver>>();

        Assert.NotNull(drivers);
        var agy = Assert.Single(drivers, d => d.CliId == AiProfilePresets.AntigravityCliId);
        Assert.Equal("AgyDriver", agy.GetType().Name);
        Assert.Equal("RoutingAiDriver", container.GetService<IAiDriver>()!.GetType().Name);
    }

    [Fact]
    public void Real_container_registers_the_codex_driver_as_a_system_cli_driver_and_not_as_IAiDriver()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-aidriver-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var drivers = container.GetService<IEnumerable<Oranda.Platform.Ai.ISystemCliAiDriver>>();

        Assert.NotNull(drivers);
        var codex = Assert.Single(drivers, d => d.CliId == AiProfilePresets.CodexCliId);
        Assert.Equal("CodexDriver", codex.GetType().Name);
        Assert.Equal("RoutingAiDriver", container.GetService<IAiDriver>()!.GetType().Name);
    }
}
