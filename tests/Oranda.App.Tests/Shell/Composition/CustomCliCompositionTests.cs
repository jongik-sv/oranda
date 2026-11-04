using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Tests.Shell.Composition;

/// <summary>
/// 조립 시험: 기타 CLI 드라이버는 <c>ICustomCliAiDriver</c> 로만 등록되어 라우터가 찾을 수 있고, <see cref="IAiDriver"/> 를 가리지 않는다.
/// 형식은 Platform 안쪽이라 이름으로 단정한다.
/// </summary>
public class CustomCliCompositionTests
{
    [Fact]
    public void Real_container_registers_the_custom_cli_driver_without_hiding_the_router()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-aidriver-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var custom = container.GetService<Oranda.Platform.Ai.ICustomCliAiDriver>();

        Assert.NotNull(custom);
        Assert.Equal("CustomCliDriver", custom.GetType().Name);
        Assert.Equal("RoutingAiDriver", container.GetService<IAiDriver>()!.GetType().Name);
    }

    [Fact]
    public void Real_container_resolves_the_executable_locator_for_the_settings_screen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-aidriver-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var locator = container.GetService<Oranda.Core.Ai.Profiles.IAiCustomCliLocator>();

        Assert.NotNull(locator);
        Assert.Null(locator.Locate("oranda-no-such-cli-xyz"));
    }
}
