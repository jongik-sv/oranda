using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Oranda.Core.Monitoring;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>SQL 모니터 문서. 단추는 <see cref="SqlMonitorViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class SqlMonitorView : UserControl
{
    public SqlMonitorView()
    {
        InitializeComponent();
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
        PlanButton.Click += (_, _) => Run(vm => vm.ShowPlanAsync());
        BindsButton.Click += (_, _) => Run(vm => vm.ShowBindsAsync());
        ResultList.DoubleTapped += (_, _) => Run(vm => vm.ShowPlanAsync());
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

    private void Run(System.Func<SqlMonitorViewModel, System.Threading.Tasks.Task> action)
    {
        if (DataContext is SqlMonitorViewModel vm)
        {
            _ = action(vm);
        }
    }
}
