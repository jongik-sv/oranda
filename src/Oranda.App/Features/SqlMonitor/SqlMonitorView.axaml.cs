using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Monitoring;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>SQL 모니터 문서. 단추는 <see cref="SqlMonitorViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class SqlMonitorView : UserControl
{
    private readonly MonitorTableBinder<TopSqlItem> _grid;
    private readonly MonitorTableBinder<AwrRowItem> _awrGrid;
    private readonly MonitorTableBinder<PlanControlItem> _controlsGrid;
    private readonly MonitorTableBinder<PlanCompareRowItem> _compareGrid;
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

        // 튜닝 탭 표(TSK-06-06): 숫자 칸 오른쪽 맞춤·단위, 고정폭 칸.
        AwrList.RightAlignedColumns = [1, 2, 3, 4];
        AwrList.MonospaceColumns = [0, 1];
        _awrGrid = new MonitorTableBinder<AwrRowItem>(AwrList,
            ["snap", "plan_hash", "실행", "경과/실행", "gets/실행"],
            i => [i.SnapText, i.PlanHashText, i.ExecutionsText, i.MsPerExecText, i.GetsPerExecText],
            i => (i.Row.SnapId, i.Row.PlanHashValue));
        ControlsList.MonospaceColumns = [1];
        _controlsGrid = new MonitorTableBinder<PlanControlItem>(ControlsList,
            ["종류", "이름", "상태", "만든 때", "SQL"],
            i => [i.KindText, i.Row.Name, i.StatusText, i.CreatedText, i.SqlPreview],
            i => (i.Row.Kind, i.Row.Name));
        AwrCompareList.RightAlignedColumns = [1, 2, 5, 6];
        AwrCompareList.RowNumbers = false;
        _compareGrid = new MonitorTableBinder<PlanCompareRowItem>(AwrCompareList,
            ["", "기준 Id", "대상 Id", "연산", "객체", "비용", "E-Rows"],
            i => [i.KindMark, i.PinnedIdText, i.CurrentIdText, new string(' ', i.Depth * 2) + i.OperationText, i.ObjectText, Pair(i.CostText, i.CostDeltaText), Pair(i.RowsText, i.RowsDeltaText)],
            i => i);
        AwrCompareList.StatusLineVisible = false;
        AwrCompareList.CellEmphasis = (c, row) => row.Count > 0 && row[^1] is PlanCompareRowItem item
            && ((c == 0 && item.Row.Kind != PlanDiffKind.Same) || (c == 5 && item.CostBig) || (c == 6 && item.RowsBig));

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
                    AttachTuning(vm);
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
        PlanButton.Click += (_, _) => Run(vm => vm.ShowPlanAsync(), tab: SqlMonitorViewModel.PlanTab);
        BindsButton.Click += (_, _) => Run(vm => vm.ShowBindsAsync(), tab: SqlMonitorViewModel.DetailTab);
        ResultList.DoubleTapped += (_, _) => Run(vm => vm.ShowPlanAsync(), tab: SqlMonitorViewModel.PlanTab);
        OrderBox.DisplayMemberBinding = new Avalonia.Data.Binding("Key");
        OrderBox.SelectedIndex = 0;

        // 튜닝 도구 모음·탭 단추.
        ChildCursorsButton.Click += (_, _) => Run(vm => vm.OpenChildCursorsAsync());
        AdvisorScriptButton.Click += (_, _) => Run(vm =>
        {
            vm.CreateTuningAdvisorScript();
            return System.Threading.Tasks.Task.CompletedTask;
        });
        BaselineScriptButton.Click += (_, _) => Run(vm => vm.CreateBaselineScriptAsync());
        WaitStartButton.Click += (_, _) => Run(vm => vm.Waits.StartAsync());
        WaitStartBigButton.Click += (_, _) => Run(vm => vm.Waits.StartAsync());
        AshLoadButton.Click += (_, _) => Run(vm => vm.Waits.LoadAshAsync());
        WaitCancelButton.Click += (_, _) => _bound?.Waits.Cancel();
        WaitLicenseManagerButton.Click += (_, _) => Run(vm => vm.OpenConnectionManagerAsync());
        WaitUseSampleButton.Click += (_, _) =>
        {
            if (_bound is { } vm)
            {
                vm.Waits.UseAsh = false;
            }
        };
        AwrLicenseManagerButton.Click += (_, _) => Run(vm => vm.OpenConnectionManagerAsync());
        AwrChildCursorsButton.Click += (_, _) => Run(vm => vm.OpenChildCursorsAsync());
        AwrRetryButton.Click += (_, _) => Run(vm => vm.Awr.LoadAsync());
        AwrCompareButton.Click += (_, _) => Run(vm => vm.Awr.CompareAsync());
        AwrCompareCloseButton.Click += (_, _) => _bound?.Awr.ClearComparison();
        ControlsCopyGrantButton.Click += async (_, _) =>
        {
            if (_bound?.Controls.GrantSql is { Length: > 0 } sql && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(sql).ConfigureAwait(true);
            }
        };
        ClassBar.SizeChanged += (_, _) => RebuildClassBar();
    }

    // 대기 표 줄의 「Lock 모니터」 바로 가기.
    private void OnLockMonitorClick(object? sender, RoutedEventArgs e) => Run(vm => vm.OpenLockMonitorAsync());

    private static string Pair(string value, string delta) => delta.Length == 0 ? value : value + "  " + delta;

    private void AttachTuning(SqlMonitorViewModel vm)
    {
        // 느린 plan_hash(가장 빠른 것의 2배 이상) 줄은 plan_hash·경과/실행 칸을, 미승인 baseline 은 상태 칸을 강조한다(표를 채우기 전에 정한다).
        AwrList.CellWarning = (c, row) => (c is 1 or 3) && row.Count > 0 && row[^1] is AwrRowItem item && vm.Awr.IsSlowPlan(item.Row.PlanHashValue);
        ControlsList.CellWarning = (c, row) => c == 2 && row.Count > 0 && row[^1] is PlanControlItem item && item.IsUnaccepted;
        AwrList.StatusLineVisible = false;
        ControlsList.StatusLineVisible = false;
        _awrGrid.Attach(vm.Awr.Rows, vm.Awr, nameof(AwrHistoryViewModel.SelectedRow), () => vm.Awr.SelectedRow, i => vm.Awr.SelectedRow = i);
        _controlsGrid.Attach(vm.Controls.Items, vm.Controls, "None", () => null, _ => { });
        _compareGrid.Attach(vm.Awr.CompareRows, vm.Awr, "None", () => null, _ => { });
        vm.Waits.Classes.CollectionChanged += (_, _) => RebuildClassBar();
        RebuildClassBar();
    }

    /// <summary>대기 분류 비율 막대: 칸 너비 = 비율(별 단위), 색 = 분류 브러시.</summary>
    private void RebuildClassBar()
    {
        ClassBar.Children.Clear();
        ClassBar.ColumnDefinitions.Clear();
        if (_bound is not { } vm)
        {
            return;
        }

        var items = vm.Waits.Classes.Where(c => c.Share.Share > 0).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            ClassBar.ColumnDefinitions.Add(new ColumnDefinition(item.Share.Share, GridUnitType.Star));
            var cell = new Border
            {
                Classes = { "tone" },
                CornerRadius = new Avalonia.CornerRadius(i == 0 ? 3 : 0, i == items.Count - 1 ? 3 : 0, i == items.Count - 1 ? 3 : 0, i == 0 ? 3 : 0),
                Margin = new Avalonia.Thickness(0, 0, i == items.Count - 1 ? 0 : 1, 0),
            };
            cell.Classes.Set("cpu", item.IsCpu);
            cell.Classes.Set("userio", item.IsUserIo);
            cell.Classes.Set("application", item.IsApplication);
            cell.Classes.Set("cluster", item.IsCluster);
            cell.Classes.Set("concurrency", item.IsConcurrency);
            ToolTip.SetTip(cell, item.LegendText);
            Grid.SetColumn(cell, i);
            ClassBar.Children.Add(cell);
        }
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
                vm.DetailTabIndex = index;
            }

            _ = action(vm);
        }
    }
}
