using System.ComponentModel;
using Avalonia.Controls;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;

namespace Oranda.App.Features.Execution;

public partial class ResultTabsView : UserControl
{
    private ResultTabsViewModel? _vm;
    private SqlResultPaneViewModel? _pane;

    public ResultTabsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            ApplyEnvironment();
            Rebind();
        };
        var shortcut = OperatingSystem.IsMacOS() ? KeyBindingTable.ForScreen("Alt+Cmd+E") : "Ctrl+Alt+E";
        ToolTip.SetTip(ExcelButton, $"Excel 로 내보내기 ({shortcut})");
        ToolTip.SetTip(ColumnFilterButton, "열 필터 켜기/끄기 (끄면 머리글 클릭이 열 선택이 됩니다)");
        ToolTip.SetTip(EditModeButton, "결과 편집 모드 (같은 쿼리를 편집 모드로 다시 실행, 다시 누르면 읽기 결과로 돌아갑니다)");
        ExcelButton.Click += async (_, _) => await ExportSelectedAsync();
        ColumnFilterButton.Click += async (_, _) => await ToggleColumnFilterAsync();
        EditModeButton.Click += async (_, _) => await ToggleEditModeAsync();
        SyncButtons();
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

    /// <summary>탭 줄의 필터 단추: 선택한 결과 탭의 열 필터를 켜거나 끈다(<c>oranda.grid.toggleColumnFilter</c> 와 같은 일).</summary>
    private async Task ToggleColumnFilterAsync()
    {
        if (_pane is { ColumnFilterToggleable: true } pane)
        {
            await pane.SetColumnFilterEnabledAsync(!pane.ColumnFilterEnabled);
        }

        SyncButtons(); // 끌 수 없어 거부했으면 눌린 모양을 실제 상태로 되돌린다
    }

    /// <summary>탭 줄의 편집 모드 단추: 선택한 결과 탭을 같은 쿼리의 편집 모드 결과로 다시 실행하거나, 편집 모드에서 읽기 결과로 돌아간다.</summary>
    private async Task ToggleEditModeAsync()
    {
        if (_vm is { CanToggleEditMode: true } vm)
        {
            await vm.ToggleEditModeAsync();
        }

        SyncButtons(); // 묻는 창에서 아니라고 했거나 못 했으면 눌린 모양을 실제 상태로 되돌린다
    }

    // 선택한 결과 탭과 그 결과 영역의 상태를 따라 단추의 켜짐·꺼짐을 맞춘다.
    private void Rebind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = DataContext as ResultTabsViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
        }

        HookPane(_vm?.Selected?.Pane);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultTabsViewModel.Selected))
        {
            HookPane(_vm?.Selected?.Pane);
        }
        else if (e.PropertyName is nameof(ResultTabsViewModel.IsRunning) or nameof(ResultTabsViewModel.Profile))
        {
            SyncButtons(); // 실행 중이거나 읽기 전용 접속이면 편집 모드 단추가 꺼진다
        }
    }

    private void HookPane(SqlResultPaneViewModel? pane)
    {
        if (_pane is not null)
        {
            _pane.PropertyChanged -= OnPaneChanged;
        }

        _pane = pane;
        if (_pane is not null)
        {
            _pane.PropertyChanged += OnPaneChanged;
        }

        SyncButtons();
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e) => SyncButtons();

    private void SyncButtons()
    {
        var hasResult = _pane is { Model.HasResult: true };
        ColumnFilterButton.IsEnabled = hasResult && _pane is { ColumnFilterToggleable: true };
        ColumnFilterButton.IsChecked = _pane?.ColumnFilterEnabled ?? true;
        EditModeButton.IsEnabled = _vm?.CanToggleEditMode ?? false;
        EditModeButton.IsChecked = _pane is { HasEditSession: true };
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
