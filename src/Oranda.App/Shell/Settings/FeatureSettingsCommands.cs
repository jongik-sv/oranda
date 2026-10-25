using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell.Features;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// TSK-07-04 — 명령 처리기. 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 셸이 자동 수집한다(docs/design/ui-contract.md).
/// 명령 ID 는 <see cref="FeatureCommandIds"/>, 제목은 <c>CommandTitles*.json</c>(TSK-07-01 이 넣음).
/// </summary>
public static class FeatureSettingsCommands
{
    /// <summary>이 파일이 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids = [FeatureCommandIds.Roles];
}

/// <summary>
/// 도구 &gt; Role·기능 고르기…: 설정 창을 「기능」 탭으로 연다(RL-12). 블로커 칸·시작 화면의 「설정 열기」·「Role 고르기…」 도 이 명령을 실행한다.
/// 창은 <see cref="IShellUi.ShowSettings"/> 가 만들고, 탭 고르기는 그 창의 모델에 맡긴다(진입점 파일을 건드리지 않는다).
/// </summary>
[OrandaCommand(FeatureCommandIds.Roles, CommandCategory.Tools, Placements = ["menu:tools/roles@11"])]
public sealed class FeatureRolesCommandHandler(IShellUi ui) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ui.ShowSettings();
        if (ui is ShellController { LastWindow: SettingsWindow { DataContext: SettingsViewModel vm } })
        {
            vm.SelectedTab = SettingsViewModel.FeaturesTabIndex;
        }

        return Task.CompletedTask;
    }
}
