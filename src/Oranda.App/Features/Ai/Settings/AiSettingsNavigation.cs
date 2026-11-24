using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>설정 「AI」 탭의 묶음.</summary>
public enum AiSettingsSection
{
    /// <summary>① AI 가 볼 수 있는 접속.</summary>
    Access = 1,

    /// <summary>② Oranda 안의 AI.</summary>
    Inside = 2,

    /// <summary>③ 밖의 AI 에이전트.</summary>
    Outside = 3,
}

/// <summary>다른 화면(상태 표시줄의 외부 에이전트 띠 등)에서 설정 「AI」 탭의 한 묶음으로 가는 길.</summary>
public static class AiSettingsNavigation
{
    /// <summary>설정 창을 「AI」 탭으로 열고 ③ 「밖의 AI 에이전트」 를 펼쳐 보인다. 셸이 없으면 아무것도 안 한다.</summary>
    public static void OpenOutside() => Open(AiSettingsSection.Outside);

    /// <summary>설정 창을 「AI」 탭으로 열고 <paramref name="section"/> 을 펼쳐 보인다. 이미 열려 있으면 그 창에서.</summary>
    public static void Open(AiSettingsSection section)
    {
        if (SettingsWindow.Current is { DataContext: SettingsViewModel open })
        {
            open.SelectedTab = SettingsViewModel.AiTabIndex;
            open.AiHub.Reveal(section);
            SettingsWindow.Current.Activate();
            return;
        }

        ((Avalonia.Application.Current as App)?.Services?.GetService(typeof(ShellController)) as IShellUi)?.ShowSettings(vm =>
        {
            vm.SelectedTab = SettingsViewModel.AiTabIndex;
            vm.AiHub.Reveal(section);
        });
    }
}
