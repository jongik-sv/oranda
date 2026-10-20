using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Oranda.Core.Monitoring;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>SQL 모니터 문서. 단추는 <see cref="SqlMonitorViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class SqlMonitorView : UserControl
{
    public SqlMonitorView()
    {
        InitializeComponent();
        ShellContextMenus.Attach(ResultList, "monitor");
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
