using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell.Menus;
using Oranda.Core.Grid;
using Oranda.Core.Monitoring;

namespace Oranda.App.Features.LockMonitor;

/// <summary>
/// Lock 모니터 문서(시안 b·c). 표 7개는 조회 결과와 같은 그리드(<see cref="MonitorGrid"/> + <see cref="MonitorGridBinder{T}"/>, TSK-14-18)라
/// 머리글 정렬·열 끌기·너비·열 필터·복사·글꼴이 조회 결과와 같고, 자동 새로 고침 뒤에도 정렬·열 순서·너비·필터·고른 행(키)·스크롤이 남는다.
/// 트리·잠금 목록·영향 순위·방치 트랜잭션·DDL 잠금 어디서 골라도 같은 세션(<see cref="LockMonitorViewModel.SelectedKey"/>)이 되고,
/// 잠금 목록·DDL 잠금은 그 세션의 줄을 모두 골라 ▶ 를 첫 줄에 둔다.
/// 키: Enter = 상세 탭 앞으로(데드락 탭은 편집기로 열기), Delete = 세션 종료 확인, Esc = 첫 읽기 취소(F5 는 키맵 oranda.monitor.locks.refresh).
/// </summary>
public partial class LockMonitorView : UserControl
{
    private readonly MonitorGridBinder<LockItem> _locks;
    private readonly MonitorGridBinder<ImpactItem> _impacts;
    private readonly MonitorGridBinder<DdlLockItem> _ddl;
    private readonly MonitorGridBinder<IdleTransactionItem> _idle;
    private readonly MonitorGridBinder<DeadlockItem> _deadlocks;
    private readonly MonitorGridBinder<IReadOnlyList<object?>> _lockedRow;
    private readonly MonitorGridBinder<IReadOnlyList<object?>> _binds;

    // 화면 모델 목록을 비추는 목록(데드락은 다른 표와 엮이지 않고, 잠긴 행·바인드는 화면 모델이 목록째 바꾼다).
    private readonly ObservableCollection<DeadlockItem> _deadlockItems = [];
    private readonly ObservableCollection<IReadOnlyList<object?>> _lockedRowCells = [];
    private readonly ObservableCollection<IReadOnlyList<object?>> _bindCells = [];
    private LockMonitorViewModel? _vm;
    private bool _syncing;
    private bool _treePending;

    public LockMonitorView()
    {
        InitializeComponent();
        foreach (var grid in new[] { LockTable, ImpactTable, DdlTable, IdleTable, DeadlockTable, LockedRowTable, BindTable })
        {
            grid.ExtraMenuItems = () => ShellContextMenus.Factory?.Invoke("lockMonitor") ?? [];
        }

        _locks = new MonitorGridBinder<LockItem>(LockTable, LockColumns, l => (l.Key, l.Row.LockType, l.Row.Id1, l.Row.Id2, l.Row.RequestedMode))
        {
            SelectionGroup = l => l.Key, // 고른 세션의 잠금을 모두 고른다
            CellEmphasis = (l, c) => LockItem.IsDanger(c, l.Cells),
        };
        _impacts = new MonitorGridBinder<ImpactItem>(ImpactTable, ImpactColumns, i => i.Key) { CellEmphasis = (_, c) => c == 4 };
        _ddl = new MonitorGridBinder<DdlLockItem>(DdlTable, DdlColumns, d => (d.Key, d.Row.Owner, d.Row.Name, d.Row.Type, d.Row.ModeHeld, d.Row.ModeRequested))
        {
            SelectionGroup = d => d.Key,
            CellEmphasis = (d, c) => c == 4 && d.IsWaiting,
        };
        _idle = new MonitorGridBinder<IdleTransactionItem>(IdleTable, IdleColumns, i => i.Key) { CellEmphasis = (i, c) => c == 9 && i.Row.BlockedCount > 0 };
        _deadlocks = new MonitorGridBinder<DeadlockItem>(DeadlockTable, DeadlockColumns, d => (d.Row.At, d.Row.InstanceId));
        _lockedRow = new MonitorGridBinder<IReadOnlyList<object?>>(LockedRowTable, LockedRowColumns, r => r[0] ?? string.Empty);
        _binds = new MonitorGridBinder<IReadOnlyList<object?>>(BindTable, BindColumns, r => (r[0], r.Count > 1 ? r[1] : null));
        _deadlocks.Attach(_deadlockItems);
        _lockedRow.Attach(_lockedRowCells);
        _binds.Attach(_bindCells);
        ShellContextMenus.Attach(TreeList, "lockMonitor");

        DeadlockTable.RowDoubleTapped += (_, _) => OpenDeadlock(_deadlocks.CurrentItem);
        TreeList.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _vm is not null && TreeList.SelectedItem is LockTreeItem t)
            {
                Pick(t.Key);
            }
        };
        TreeList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { DataContext: LockTreeItem item } b && b.Classes.Contains("expander"))
            {
                _vm?.ToggleExpanded(item);
                e.Handled = true;
            }
        });
        HintList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { DataContext: LockHint { CopySql: { } sql } } b && b.Classes.Contains("hint-copy"))
            {
                _ = CopyAsync(sql);
            }
        });

        DataContextChanged += (_, _) => Bind(DataContext as LockMonitorViewModel);
        RefreshButton.Click += (_, _) => Run(vm => vm.RefreshAsync());
        RetryButton.Click += (_, _) => Run(vm => vm.RefreshAsync());
        ReconnectButton.Click += (_, _) => Run(vm => vm.ReconnectAsync());
        KillButton.Click += (_, _) => Run(vm => vm.KillAsync());
        DisconnectButton.Click += (_, _) => Run(vm => vm.DisconnectAsync());
        GoToSessionButton.Click += (_, _) => Run(vm => vm.GoToSessionAsync());
        CopyButton.Click += (_, _) => Run(vm => CopyAsync(vm.BuildCopyText()));
        CsvButton.Click += (_, _) => Run(SaveCsvAsync);
        OtherAccountButton.Click += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
        EmptyOtherAccountButton.Click += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
        AccessBanner.OtherAccountRequested += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
        ConnectionManagerButton.Click += (_, _) => Run(vm => vm.OpenConnectionManager?.Invoke() ?? Task.CompletedTask);
        CancelLoadButton.Click += (_, _) => _vm?.CancelLoad();
        ThreeSecondsButton.Click += (_, _) => _vm?.SetMinWaitThree();
        ClearFiltersButton.Click += (_, _) => Run(vm => vm.ClearFiltersAsync());
        ShowAllButton.Click += (_, _) => Run(vm => vm.ShowAllLocksAsync());
        OpenEditorButton.Click += (_, _) => _vm?.OpenSqlInEditor();
        CopySqlButton.Click += (_, _) => Run(vm => CopyAsync(vm.SqlForCopy()));
        PlanButton.Click += (_, _) => Run(vm => vm.ShowPlanAsync());
        CopyRowSelectButton.Click += (_, _) => Run(vm => CopyAsync(vm.LockedRowSelect));
        CopyDdlGrantButton.Click += (_, _) => Run(vm => CopyAsync(vm.DdlGrantSql));
        CycleTreeButton.Click += (_, _) => _vm?.ShowCycleInTree();
        OpenSnapshotFolderButton.Click += (_, _) => _ = OpenSnapshotFolderAsync();
        DetachedFromVisualTree += (_, _) => _floatingToast?.Invoke();
        UserFilterBox.KeyDown += OnFilterKey;
        SchemaFilterBox.KeyDown += OnFilterKey;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    // ── 그리드 열. 숫자 열은 원시 값(오른쪽 맞춤·수로 정렬, 천 단위 쉼표)이고 단위는 머리글에 둔다.
    //    열 순서는 항목의 Cells(복사·CSV 글)와 같아 위험색 칸 번호가 같다. ──

    private static MonitorColumn<T> Text<T>(string name, Func<T, object?> value) => new(name, ColumnDataKind.Text, value);

    private static MonitorColumn<T> Number<T>(string name, Func<T, object?> value) => new(name, ColumnDataKind.Number, value);

    /// <summary>잠금 목록 열.</summary>
    internal static readonly MonitorColumn<LockItem>[] LockColumns =
    [
        Text<LockItem>("인스턴스·SID", l => LockText.Key(l.Key)),
        Text<LockItem>("사용자", l => l.Row.Username ?? "-"),
        Text<LockItem>("유형", l => l.Row.LockType),
        Text<LockItem>("객체 / 자원", l => l.Cells[3]),
        Text<LockItem>("보유", l => LockText.Mode(l.Row.HeldMode)),
        Text<LockItem>("요청", l => LockText.Mode(l.Row.RequestedMode)),
        Number<LockItem>("ctime(초)", l => l.Row.CtimeSeconds),
        Text<LockItem>("block", l => l.Row.IsBlocking ? "차단" : LockText.Dash),
        Text<LockItem>("트랜잭션 시작", l => l.Cells[8]),
        Number<LockItem>("undo(블록)", l => l.Row.UndoBlocks),
        Number<LockItem>("id1", l => l.Row.Id1),
        Number<LockItem>("id2", l => l.Row.Id2),
    ];

    /// <summary>보유자 영향 순위 열.</summary>
    internal static readonly MonitorColumn<ImpactItem>[] ImpactColumns =
    [
        Number<ImpactItem>("#", i => i.Rank),
        Text<ImpactItem>("보유자", i => LockText.Key(i.Key)),
        Text<ImpactItem>("사용자", i => i.Cells[2]),
        Text<ImpactItem>("상태", i => i.Cells[3]),
        Number<ImpactItem>("대기자", i => i.Impact.WaiterCount),
        Number<ImpactItem>("최대 대기(초)", i => i.Impact.MaxWaitSeconds),
        Number<ImpactItem>("합계 대기(초)", i => i.Impact.TotalWaitSeconds),
        Number<ImpactItem>("트랜잭션 경과(초)", i => i.TransactionSeconds),
        Text<ImpactItem>("프로그램", i => i.Cells[8]),
    ];

    /// <summary>DDL 잠금 열(모두 글).</summary>
    internal static readonly MonitorColumn<DdlLockItem>[] DdlColumns =
        [.. DdlLockItem.Columns.Select((name, n) => Text<DdlLockItem>(name, d => d.Cells[n]))];

    /// <summary>방치 트랜잭션 열.</summary>
    internal static readonly MonitorColumn<IdleTransactionItem>[] IdleColumns =
    [
        Text<IdleTransactionItem>("인스턴스·SID", i => LockText.Key(i.Key)),
        Text<IdleTransactionItem>("사용자", i => i.Row.Username ?? "-"),
        Text<IdleTransactionItem>("머신", i => i.Row.Machine),
        Text<IdleTransactionItem>("프로그램", i => i.Row.Program),
        Number<IdleTransactionItem>("마지막 호출 뒤(초)", i => i.Row.IdleSeconds),
        Text<IdleTransactionItem>("트랜잭션 시작", i => i.Cells[5]),
        Number<IdleTransactionItem>("경과(초)", i => i.TransactionSeconds),
        Number<IdleTransactionItem>("undo(블록)", i => i.Row.UndoBlocks),
        Number<IdleTransactionItem>("잠금", i => i.Row.LockCount),
        Number<IdleTransactionItem>("차단", i => i.Row.BlockedCount > 0 ? i.Row.BlockedCount : null),
    ];

    /// <summary>데드락 기록 열. 시각은 날짜까지 적어 글로 정렬해도 시간 순이다.</summary>
    internal static readonly MonitorColumn<DeadlockItem>[] DeadlockColumns =
    [
        Text<DeadlockItem>("시각", d => d.Row.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        Number<DeadlockItem>("인스턴스", d => d.Row.InstanceId),
        Text<DeadlockItem>("내용", d => d.Summary),
        Text<DeadlockItem>("트레이스 파일", d => d.TraceFile),
    ];

    /// <summary>잠긴 행 열(열 이름·값).</summary>
    internal static readonly MonitorColumn<IReadOnlyList<object?>>[] LockedRowColumns =
    [
        Text<IReadOnlyList<object?>>("열", r => r[0]),
        Text<IReadOnlyList<object?>>("값", r => r.Count > 1 ? r[1] : null),
    ];

    /// <summary>바인드 열. 위치는 수로 정렬한다.</summary>
    internal static readonly MonitorColumn<IReadOnlyList<object?>>[] BindColumns =
    [
        Text<IReadOnlyList<object?>>("이름", r => r[0]),
        Number<IReadOnlyList<object?>>("위치", r => r.Count > 1 && long.TryParse(r[1] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null),
        Text<IReadOnlyList<object?>>("형", r => r.Count > 2 ? r[2] : null),
        Text<IReadOnlyList<object?>>("값", r => r.Count > 3 ? r[3] : null),
        Text<IReadOnlyList<object?>>("캡처 시각", r => r.Count > 4 ? r[4] : null),
    ];

    /// <summary>데드락 기록 표의 현재 행(시험용).</summary>
    internal DeadlockItem? CurrentDeadlock => _deadlocks.CurrentItem;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LockMonitorCommandHandler.SaveCsv = SaveCsvAsync;
    }

    private void Bind(LockMonitorViewModel? vm)
    {
        // 그리드 어댑터는 처음 받은 화면 모델 하나에만 잇는다(문서 하나 = 화면 모델 하나).
        if (vm is null || _vm is not null)
        {
            return;
        }

        _vm = vm;
        vm.PropertyChanged += OnVmChanged;
        vm.TreeRows.CollectionChanged += OnTreeChanged;
        vm.Deadlocks.CollectionChanged += (_, _) => Fill(_deadlockItems, vm.Deadlocks);
        vm.AlertRaised += OnAlertRaised;
        const string selected = nameof(LockMonitorViewModel.SelectedKey);
        _locks.Attach(vm.Locks, vm, selected, () => vm.SelectedKey is { } k ? vm.Locks.FirstOrDefault(l => l.Key == k) : null, l => Pick(l?.Key));
        _impacts.Attach(vm.Impacts, vm, selected, () => vm.SelectedKey is { } k ? vm.Impacts.FirstOrDefault(i => i.Key == k) : null, i => Pick(i?.Key));
        _ddl.Attach(vm.DdlLocks, vm, selected, () => vm.SelectedKey is { } k ? vm.DdlLocks.FirstOrDefault(d => d.Key == k) : null, d => Pick(d?.Key));
        _idle.Attach(vm.IdleTransactions, vm, selected, () => vm.SelectedKey is { } k ? vm.IdleTransactions.FirstOrDefault(i => i.Key == k) : null, i => Pick(i?.Key));
        Fill(_deadlockItems, vm.Deadlocks);
        Fill(_lockedRowCells, vm.LockedRowCells);
        Fill(_bindCells, vm.BindCells);
        SyncSelection();
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LockMonitorViewModel.SelectedKey):
                SyncSelection();
                break;
            case nameof(LockMonitorViewModel.LockedRowCells):
                Fill(_lockedRowCells, _vm!.LockedRowCells);
                break;
            case nameof(LockMonitorViewModel.BindCells):
                Fill(_bindCells, _vm!.BindCells);
                break;
            case nameof(LockMonitorViewModel.ActiveAlert) when _vm!.ActiveAlert is null:
                _floatingToast?.Invoke();
                _floatingToast = null;
                break;
        }
    }

    // 트리(ListBox): 새로 고침은 Clear + Add 여러 번이라 끝난 뒤 한 번에 고른 세션을 다시 고른다.
    private void OnTreeChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_treePending)
        {
            return;
        }

        _treePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _treePending = false;
            SyncSelection();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>표를 모두 다시 만들고 트리의 고른 세션을 다시 고른다(시험이 바로 부른다). 그리드는 한 번에 몰아 뒤에서 바꿔 낀다.</summary>
    internal void Rebuild()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        Fill(_deadlockItems, vm.Deadlocks);
        _locks.Refresh();
        _impacts.Refresh();
        _ddl.Refresh();
        _idle.Refresh();
        SyncSelection();
    }

    private void SyncSelection()
    {
        if (_vm is not { } vm || _treePending)
        {
            return;
        }

        _syncing = true;
        try
        {
            TreeList.SelectedItem = vm.SelectedKey is { } k ? vm.TreeRows.FirstOrDefault(t => t.Key == k) : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Pick(SessionKey? key)
    {
        if (key is { } k && _vm is { } vm && vm.SelectedKey != k)
        {
            vm.SelectedKey = k;
        }
    }

    private void OnFilterKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Run(vm => vm.RefreshAsync());
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm || e.Handled || e.Source is TextBox || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when vm.TopTabIndex == LockMonitorViewModel.DeadlockTab && _deadlocks.CurrentItem is { } dl:
                OpenDeadlock(dl);
                e.Handled = true;
                break;
            case Key.Enter when vm.HasSelection:
                Run(v => v.ShowDetailAsync(v.DetailTabIndex == LockMonitorViewModel.HintTab ? LockMonitorViewModel.SqlTab : v.DetailTabIndex));
                e.Handled = true;
                break;
            case Key.Delete when vm.CanKill:
                Run(v => v.KillAsync());
                e.Handled = true;
                break;
            case Key.Escape when vm.ShowLoading:
                vm.CancelLoad();
                e.Handled = true;
                break;
        }
    }

    private async Task SaveCsvAsync(LockMonitorViewModel vm)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "잠금 목록 CSV 저장",
            SuggestedFileName = LockMonitorViewModel.SuggestedCsvName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(vm.BuildLocksCsv());
        vm.StatusText = "잠금 목록을 CSV 로 저장했습니다: " + file.Name;
    }

    /// <summary>탭이 뒤에 있으면(이 View 가 창에 없거나 보이지 않으면) 설정에 따라 메인 창 오버레이에 알림을 띄운다.</summary>
    private void OnAlertRaised(object? sender, LockAlert alert)
    {
        if (_vm is not { NotifyWhenHidden: true } vm || (VisualRoot is not null && IsEffectivelyVisible))
        {
            return;
        }

        _floatingToast?.Invoke();
        _floatingToast = (LockMonitorCommandHandler.MainWindow?.Invoke() ?? TopLevel.GetTopLevel(this)) is { } top
            ? LockAlertToast.ShowFloating(top, vm)
            : null;
    }

    private Action? _floatingToast;

    private void OpenDeadlock(DeadlockItem? item)
    {
        if (item is not null && _vm is { } vm)
        {
            vm.OpenTextInEditor(item.FullText, "데드락 기록");
        }
    }

    private async Task OpenSnapshotFolderAsync()
    {
        if (_vm?.HistoryFolder is not { } folder || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
        catch (IOException ex)
        {
            _vm.StatusText = "폴더를 열지 못했습니다: " + ex.Message;
        }
    }

    private async Task CopyAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void Run(Func<LockMonitorViewModel, Task> action)
    {
        if (_vm is { } vm)
        {
            _ = action(vm);
        }
    }
}
