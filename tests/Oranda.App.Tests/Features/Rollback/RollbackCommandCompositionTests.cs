using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.Rollback;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Rollback;

// 소유: rb-m2-oracle(win144 실앱 결함, 소유 예외 허가). 변경 일지가 부르는 원복 명령을 실제 앱 조립(ServiceContainer)으로 찾고 실행한다.
public sealed class RollbackCommandCompositionTests
{
    [Theory]
    [InlineData(RollbackCommandIds.OpenScript)]
    [InlineData(RollbackCommandIds.ComparePoint)]
    public async Task Change_journal_commands_are_found_and_enabled_in_the_real_composition(string id)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-rbcmd-" + Guid.NewGuid().ToString("N"));
        using var services = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
        var registry = services.GetRequiredService<ICommandRegistry>();
        var keys = services.GetRequiredService<ContextKeyService>();

        Assert.NotNull(registry.Find(id));
        Assert.True(registry.CanExecute(id, keys), $"{id} 를 지금 실행할 수 없다(Enablement·기능 게이트)");
    }

    /// <summary>
    /// win144 실앱: 변경 일지 「원복 스크립트 열기」가 「원복 스크립트를 열지 못했다」만 냈다. 셸은 패널 ViewModel 을 컨테이너로 만드는데,
    /// 컨테이너는 인자가 가장 많은 생성자(시험용 internal 생성자 포함)를 고른다. 명령 실행기는 셸 생성자만 만든다.
    /// </summary>
    [Fact]
    public async Task Change_journal_panel_built_by_the_container_can_run_rollback_commands()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-rbcmd-" + Guid.NewGuid().ToString("N"));
        using var services = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var vm = (ChangeJournalViewModel)services.Create(typeof(ChangeJournalViewModel));
        Assert.False(services.DeferredScanned, services.DeferredScanTrigger); // 시험용 생성자를 따지다 지연 어셈블리를 훑지 않았다

        Assert.True(await vm.CommandRunner(RollbackCommandIds.OpenScript, new RollbackScriptArgs(9999))); // 명령을 찾아 실행했다(없는 묶음은 처리기가 알린다)
    }

    [Fact]
    public async Task A_handler_error_is_shown_instead_of_the_generic_failure_line()
    {
        var vm = ChangeJournalViewModel.ForTests(null);
        vm.SelectedGroup = new RollbackGroupItem(new Oranda.Core.Rollback.RollbackGroup
        {
            Id = 1, ProfileId = Guid.NewGuid(), ProfileName = "prod-example", DbKey = "K",
            State = Oranda.Core.Rollback.RollbackScriptState.Confirmed, HasScriptFile = true,
        });
        vm.CommandRunner = (_, _) => throw new InvalidOperationException("문서 탭을 열 수 없습니다");

        Assert.False(await vm.OpenScriptAsync());
        Assert.Equal("문서 탭을 열 수 없습니다", vm.StatusMessage);
    }
}
