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
    private readonly MonitorGridBinder<TopSqlItem> _grid;
    private readonly MonitorGridBinder<AwrRowItem> _awrGrid;
    private readonly MonitorGridBinder<PlanControlItem> _controlsGrid;
    private readonly MonitorGridBinder<PlanCompareRowItem> _compareGrid;
    private SqlMonitorViewModel? _bound;

    public SqlMonitorView()
    {
        InitializeComponent();
        ResultList.ExtraMenuItems = () => ShellContextMenus.Factory?.Invoke("monitor") ?? [];
        _grid = new MonitorGridBinder<TopSqlItem>(ResultList, Columns, i => i.Row.Cursor);

        // 튜닝 탭 표(TSK-06-06): 주 목록과 같은 그리드(TSK-14-18). 다시 읽어도 정렬·열·너비·필터·고른 행이 남는다.
        _awrGrid = new MonitorGridBinder<AwrRowItem>(AwrList, AwrColumns, i => (i.Row.SnapId, i.Row.PlanHashValue));
        _controlsGrid = new MonitorGridBinder<PlanControlItem>(ControlsList, ControlColumns, i => (i.Row.Kind, i.Row.Name));
        _compareGrid = new MonitorGridBinder<PlanCompareRowItem>(AwrCompareList, CompareColumns, i => (i.Row.Pinned?.Id, i.Row.Current?.Id))
        {
            CellEmphasis = (item, c) => (c == 0 && item.Row.Kind != PlanDiffKind.Same) || (c == 5 && item.CostBig) || (c == 6 && item.RowsBig),
        };

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
        ResultList.RowDoubleTapped += (_, _) => Run(vm => vm.ShowPlanAsync(), tab: SqlMonitorViewModel.PlanTab);
        OrderBox.DisplayMemberBinding = new Avalonia.Data.Binding("Key");
        OrderBox.SelectedIndex = 0;

        // 튜닝 도구 모음·탭 단추.
        ChildCursorsButton.Click += (_, _) => Run(vm => vm.OpenChildCursorsAsync());
        TuningReportButton.Click += (_, _) => Run(vm => vm.SelectedSql is { } item
            ? Tuning.Report.TuningReportNavigation.OpenAsync(item.Row.Cursor, vm.MonitorSession)
            : Task.CompletedTask);
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

    /// <summary>Top SQL 목록 열. 숫자 열은 오른쪽 맞춤·수로 정렬된다(초는 소수 둘째 자리까지).</summary>
    internal static readonly MonitorColumn<TopSqlItem>[] Columns =
    [
        new("SQL ID", Oranda.Core.Grid.ColumnDataKind.Text, i => i.SqlId),
        new("자식", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.Cursor.ChildNumber),
        new("소요(s)", Oranda.Core.Grid.ColumnDataKind.Number, i => Seconds(i.Row.ElapsedSeconds)),
        new("CPU(s)", Oranda.Core.Grid.ColumnDataKind.Number, i => Seconds(i.Row.CpuSeconds)),
        new("실행", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.Executions),
        new("읽은 블록", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.BufferGets),
        new("디스크", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.DiskReads),
        new("스키마", Oranda.Core.Grid.ColumnDataKind.Text, i => i.Schema),
        new("SQL", Oranda.Core.Grid.ColumnDataKind.Text, i => i.Preview),
    ];

    /// <summary>AWR 계획 이력 열. plan_hash 는 쉼표 없이 글로 보인다.</summary>
    internal static readonly MonitorColumn<AwrRowItem>[] AwrColumns =
    [
        new("snap", Oranda.Core.Grid.ColumnDataKind.Text, i => i.SnapText),
        new("plan_hash", Oranda.Core.Grid.ColumnDataKind.Text, i => i.PlanHashText),
        new("실행", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.Executions),
        new("경과/실행(ms)", Oranda.Core.Grid.ColumnDataKind.Number, i => i.MsPerExec is { } ms && double.IsFinite(ms) ? Math.Round((decimal)ms, 2) : null),
        new("gets/실행", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.Executions > 0 ? Math.Round((decimal)i.Row.BufferGets / i.Row.Executions) : null),
    ];

    /// <summary>계획 관리(Profile·Baseline·Patch) 열.</summary>
    internal static readonly MonitorColumn<PlanControlItem>[] ControlColumns =
    [
        new("종류", Oranda.Core.Grid.ColumnDataKind.Text, i => i.KindText),
        new("이름", Oranda.Core.Grid.ColumnDataKind.Text, i => i.Row.Name),
        new("상태", Oranda.Core.Grid.ColumnDataKind.Text, i => i.StatusText),
        new("만든 때", Oranda.Core.Grid.ColumnDataKind.Text, i => i.CreatedText),
        new("SQL", Oranda.Core.Grid.ColumnDataKind.Text, i => i.SqlPreview),
    ];

    /// <summary>두 plan_hash 비교 열. 연산은 깊이만큼 들여 쓴다.</summary>
    internal static readonly MonitorColumn<PlanCompareRowItem>[] CompareColumns =
    [
        new("", Oranda.Core.Grid.ColumnDataKind.Text, i => i.KindMark),
        new("기준 Id", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.Pinned?.Id),
        new("대상 Id", Oranda.Core.Grid.ColumnDataKind.Number, i => i.Row.Current?.Id),
        new("연산", Oranda.Core.Grid.ColumnDataKind.Text, i => new string(' ', i.Depth * 2) + i.OperationText),
        new("객체", Oranda.Core.Grid.ColumnDataKind.Text, i => i.ObjectText),
        new("비용", Oranda.Core.Grid.ColumnDataKind.Text, i => Pair(i.CostText, i.CostDeltaText)),
        new("E-Rows", Oranda.Core.Grid.ColumnDataKind.Text, i => Pair(i.RowsText, i.RowsDeltaText)),
    ];

    private static decimal Seconds(double s) => double.IsFinite(s) ? Math.Round((decimal)s, 2) + 0.00m : 0.00m; // 늘 소수 둘째 자리까지 보인다

    // 대기 표 줄의 「Lock 모니터」 바로 가기.
    private void OnLockMonitorClick(object? sender, RoutedEventArgs e) => Run(vm => vm.OpenLockMonitorAsync());

    private static string Pair(string value, string delta) => delta.Length == 0 ? value : value + "  " + delta;

    private void AttachTuning(SqlMonitorViewModel vm)
    {
        // 느린 plan_hash(가장 빠른 것의 2배 이상) 줄은 plan_hash·경과/실행 칸을, 미승인 baseline 은 상태 칸을 강조한다(표를 채우기 전에 정한다).
        _awrGrid.CellWarning = (item, c) => c is 1 or 3 && vm.Awr.IsSlowPlan(item.Row.PlanHashValue);
        _controlsGrid.CellWarning = (item, c) => c == 2 && item.IsUnaccepted;
        _awrGrid.Attach(vm.Awr.Rows, vm.Awr, nameof(AwrHistoryViewModel.SelectedRow), () => vm.Awr.SelectedRow, i => vm.Awr.SelectedRow = i);
        _controlsGrid.Attach(vm.Controls.Items);
        _compareGrid.Attach(vm.Awr.CompareRows);
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
