using Avalonia.Controls;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;

namespace Oranda.App.Features.Execution;

public partial class ResultTabsView : UserControl
{
    public ResultTabsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyEnvironment();
        var shortcut = OperatingSystem.IsMacOS() ? KeyBindingTable.ForScreen("Alt+Cmd+E") : "Ctrl+Alt+E";
        ToolTip.SetTip(ExcelButton, $"Excel 로 내보내기 ({shortcut})");
        ExcelButton.Click += async (_, _) => await ExportSelectedAsync();
    }

    /// <summary>탭 줄의 Excel 단추: 포커스가 어디 있든 선택한 결과 탭의 결과를 <c>oranda.grid.exportExcel</c> 로 내보낸다.</summary>
    private async Task ExportSelectedAsync()
    {
        if ((DataContext as ResultTabsViewModel)?.Selected?.Pane?.Model is not { } model)
        {
            return;
        }

        ResultGridCommands.Active = model;
        ContextKeyService.Shared?.Set("gridHasResult", model.HasResult); // 포커스가 편집기에 있어도 명령이 활성이게 한다
        await ShellCommandRunner.RunAsync("oranda.grid.exportExcel");
    }

    private void ApplyEnvironment()
    {
        // 색은 App.axaml 의 env-frame 스타일(테마 리소스)에서만 받는다.
        var vm = DataContext as ResultTabsViewModel;
        var env = vm?.Profile?.Environment;
        BannerBorder.Classes.Set("env-frame", env is not null);
        BannerBorder.Classes.Set("env-production", env == Oranda.Core.Connection.ConnectionEnvironment.Production);
        BannerBorder.Classes.Set("env-test", env == Oranda.Core.Connection.ConnectionEnvironment.Test);
        BannerBorder.Classes.Set("env-development", env == Oranda.Core.Connection.ConnectionEnvironment.Development);
    }
}
