using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Monitoring;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>SQL 모니터 문서. 단추는 <see cref="SqlMonitorViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class SqlMonitorView : UserControl
{
    private readonly MonitorTableBinder<TopSqlItem> _grid;
    private SqlMonitorViewModel? _bound;

    public SqlMonitorView()
    {
        InitializeComponent();
        ResultList.ExtraMenuItems = () => ShellContextMenus.Factory?.Invoke("monitor") ?? [];
        ResultList.RightAlignedColumns = [2, 3, 4, 5, 6];
        ResultList.MonospaceColumns = [0]; // SQL ID
        _grid = new MonitorTableBinder<TopSqlItem>(ResultList,
            ["SQL ID", "자식", "소요(s)", "CPU(s)", "실행", "읽은 블록", "디스크", "스키마", "SQL"],
            i => [i.SqlId, i.ChildText, i.ElapsedText, i.CpuText, i.ExecutionsText, i.BlocksText, i.DiskText, i.Schema, i.Preview],
            i => i.Row.Cursor);
        OrderBox.SelectionChanged += (_, _) =>
        {
            if (DataContext is SqlMonitorViewModel vm && OrderBox.SelectedItem is KeyValuePair<string, TopSqlOrder> pick)
            {
                vm.OrderBy = pick.Value;
            }
        };
        DataContextChanged += (_, _) =>
        {
            SyncOrder();
            if (DataContext is SqlMonitorViewModel vm)
            {
                if (!ReferenceEquals(_bound, vm))
                {
                    _bound = vm;
                    _grid.AttachStatusLine(StatusLine, vm, () => vm.StatusText);
                    _grid.Attach(vm.Results, vm, nameof(SqlMonitorViewModel.SelectedSql), () => vm.SelectedSql, i => vm.SelectedSql = i);
                }

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SqlMonitorViewModel.OrderBy))
                    {
                        SyncOrder();
                    }
                };
            }
        };
        SearchButton.Click += (_, _) => Run(vm => vm.SearchAsync());
        OtherAccountButton.Click += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
        AccessBanner.OtherAccountRequested += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
        // 계획·바인드를 읽으면 아래 탭도 그 내용으로 넘긴다(결과가 숨은 탭에 들어가 안 보이지 않게).
        PlanButton.Click += (_, _) => Run(vm => vm.ShowPlanAsync(), tab: 1);
        BindsButton.Click += (_, _) => Run(vm => vm.ShowBindsAsync(), tab: 0);
        ResultList.DoubleTapped += (_, _) => Run(vm => vm.ShowPlanAsync(), tab: 1);
        OrderBox.DisplayMemberBinding = new Avalonia.Data.Binding("Key");
        OrderBox.SelectedIndex = 0;
    }

    private void SyncOrder()
    {
        if (DataContext is SqlMonitorViewModel vm)
        {
            OrderBox.SelectedItem = SqlMonitorViewModel.Orders.FirstOrDefault(o => o.Value == vm.OrderBy);
        }
    }

    private void Run(System.Func<SqlMonitorViewModel, System.Threading.Tasks.Task> action, int? tab = null)
    {
        if (DataContext is SqlMonitorViewModel vm)
        {
            if (tab is { } index)
            {
                DetailTabs.SelectedIndex = index;
            }

            _ = action(vm);
        }
    }
}
