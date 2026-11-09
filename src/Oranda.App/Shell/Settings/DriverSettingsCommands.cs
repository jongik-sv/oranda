using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Settings;

/// <summary>설정 「드라이버」 탭으로 여는 명령의 ID(접속 대화상자의 「드라이버 지정…」 단추가 실행한다).</summary>
public static class DriverSettingsCommands
{
    /// <summary>설정 창을 「드라이버」 탭으로 연다.</summary>
    public const string OpenDrivers = "oranda.settings.openDrivers";

    /// <summary>이 파일이 등록하는 명령 ID 전체.</summary>
    public static readonly string[] Ids = [OpenDrivers];
}

/// <summary>
/// 설정 &gt; 드라이버 &gt; 「Tibero 드라이버(TDP.NET Core) 위치」 칸을 연다. 설정 창을 <see cref="SettingsViewModel.DriversTabIndex"/> 탭으로 연다
/// (<see cref="IShellUi.ShowSettings(System.Action{SettingsViewModel})"/> 의 준비 단계에서 탭을 고른다). 메뉴에는 두지 않고 명령 팔레트·단추로만 닿는다.
/// </summary>
[OrandaCommand(DriverSettingsCommands.OpenDrivers, CommandCategory.Tools)]
public sealed class OpenDriverSettingsCommandHandler(IShellUi ui) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ui.ShowSettings(vm => vm.SelectedTab = SettingsViewModel.DriversTabIndex);
        return Task.CompletedTask;
    }
}
