using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
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
using Oranda.App.Shell.Controls;
using Oranda.App.Shell.Menus;
using Oranda.Core.Monitoring;

namespace Oranda.App.Features.LockMonitor;

/// <summary>
/// Lock 모니터 문서(시안 b·c). 표는 C안 격자(<see cref="DetailTable"/>)이고 행 끝에 항목을 숨겨 붙여 고른 세션과 잇는다.
/// 트리·잠금 목록·영향 순위·DDL 잠금 어디서 골라도 같은 세션(<see cref="LockMonitorViewModel.SelectedKey"/>)이 되고,
/// 잠금 목록은 그 세션의 잠금을 모두 골라 ▶ 를 첫 줄에 둔다. 자동 새로 고침 때 선택·스크롤을 남긴다.
/// 키: Enter = 상세 탭 앞으로, Delete = 세션 종료 확인, Esc = 첫 읽기 취소(F5 는 키맵 oranda.monitor.locks.refresh).
/// </summary>
public partial class LockMonitorView : UserControl
{
    private LockMonitorViewModel? _vm;
    private bool _syncing;
    private bool _pending;

    public LockMonitorView()
    {
        InitializeComponent();
        Setup(LockTable, LockItem.Columns, right: [6, 9, 10, 11], mono: [0, 8, 10, 11], LockItem.IsDanger);
        Setup(ImpactTable, ImpactItem.Columns, right: [4, 5, 6, 7], mono: [0, 1], (c, _) => c == 4);
        ImpactTable.RowNumbers = false;
        Setup(DdlTable, DdlLockItem.Columns, right: [], mono: [0, 1],
            (c, r) => c == 4 && r[^1] is DdlLockItem d && d.IsWaiting);
        Setup(LockedRowTable, ["열", "값"], right: [], mono: [1], null);
        Setup(BindTable, ["이름", "위치", "형", "값", "캡처 시각"], right: [1], mono: [0, 3, 4], null);
        Setup(IdleTable, IdleTransactionItem.Columns, right: IdleTransactionItem.NumberColumns, mono: [0, 5], IdleTransactionItem.IsDanger);
        Setup(DeadlockTable, DeadlockItem.Columns, right: [1], mono: [0, 3], null);
        IdleTable.StatusLineVisible = DeadlockTable.StatusLineVisible = false;
        LockedRowTable.KeepScroll = BindTable.KeepScroll = false;
        LockedRowTable.StatusLineVisible = BindTable.StatusLineVisible = false;
        LockTable.StatusLineVisible = ImpactTable.StatusLineVisible = DdlTable.StatusLineVisible = false;
        ShellContextMenus.Attach(TreeList, "lockMonitor");

        LockTable.CurrentRowChanged += (_, row) => PickFromRow(row);
        ImpactTable.CurrentRowChanged += (_, row) => PickFromRow(row);
        DdlTable.CurrentRowChanged += (_, row) => PickFromRow(row);
        IdleTable.CurrentRowChanged += (_, row) => PickFromRow(row);
        DeadlockTable.RowActivated += (_, row) => OpenDeadlock(row);
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

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LockMonitorCommandHandler.SaveCsv = SaveCsvAsync;
    }

    private static void Setup(DetailTable table, IReadOnlyList<string> columns, int[] right, int[] mono, Func<int, IReadOnlyList<object?>, bool>? danger)
    {
        table.Columns = columns;
        table.KeepScroll = true;
        table.RightAlignedColumns = right;
        table.MonospaceColumns = mono;
        table.CellEmphasis = danger;
        table.ExtraMenuItems = () => ShellContextMenus.Factory?.Invoke("lockMonitor") ?? [];
    }

    private void Bind(LockMonitorViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Locks.CollectionChanged -= OnListChanged;
            _vm.Impacts.CollectionChanged -= OnListChanged;
            _vm.DdlLocks.CollectionChanged -= OnListChanged;
            _vm.TreeRows.CollectionChanged -= OnListChanged;
            _vm.IdleTransactions.CollectionChanged -= OnListChanged;
            _vm.Deadlocks.CollectionChanged -= OnListChanged;
            _vm.AlertRaised -= OnAlertRaised;
        }

        _vm = vm;
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnVmChanged;
        vm.Locks.CollectionChanged += OnListChanged;
        vm.Impacts.CollectionChanged += OnListChanged;
        vm.DdlLocks.CollectionChanged += OnListChanged;
        vm.TreeRows.CollectionChanged += OnListChanged;
        vm.IdleTransactions.CollectionChanged += OnListChanged;
        vm.Deadlocks.CollectionChanged += OnListChanged;
        vm.AlertRaised += OnAlertRaised;
        LockedRowTable.Rows = vm.LockedRowCells;
        BindTable.Rows = vm.BindCells;
        Rebuild();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LockMonitorViewModel.SelectedKey):
                SyncSelection();
                break;
            case nameof(LockMonitorViewModel.LockedRowCells):
                LockedRowTable.Rows = _vm!.LockedRowCells;
                break;
            case nameof(LockMonitorViewModel.BindCells):
                BindTable.Rows = _vm!.BindCells;
                break;
            case nameof(LockMonitorViewModel.ActiveAlert) when _vm!.ActiveAlert is null:
                _floatingToast?.Invoke();
                _floatingToast = null;
                break;
        }
    }

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 새로 고침은 Clear + Add 여러 번이라 한 번에 몰아 다시 그린다.
        if (_pending)
        {
            return;
        }

        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Rebuild();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>표 행을 다시 만들고 고른 세션을 다시 고른다(시험이 바로 부른다).</summary>
    internal void Rebuild()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        LockTable.Rows = [.. vm.Locks.Select(l => (IReadOnlyList<object?>)[.. l.Cells, l])];
        ImpactTable.Rows = [.. vm.Impacts.Select(i => (IReadOnlyList<object?>)[.. i.Cells, i])];
        DdlTable.Rows = [.. vm.DdlLocks.Select(d => (IReadOnlyList<object?>)[.. d.Cells, d])];
        IdleTable.Rows = [.. vm.IdleTransactions.Select(i => (IReadOnlyList<object?>)[.. i.Cells, i])];
        DeadlockTable.Rows = [.. vm.Deadlocks.Select(d => (IReadOnlyList<object?>)[.. d.Cells, d])];
        SyncSelection();
    }

    private void SyncSelection()
    {
        if (_vm is not { } vm || _pending)
        {
            return;
        }

        _syncing = true;
        try
        {
            var key = vm.SelectedKey;
            TreeList.SelectedItem = key is { } k ? vm.TreeRows.FirstOrDefault(t => t.Key == k) : null;
            if (key is null)
            {
                LockTable.ClearSelection();
                ImpactTable.ClearSelection();
                IdleTable.ClearSelection();
                return;
            }

            bool Mine(IReadOnlyList<object?> r) => KeyOf(r) == key;
            LockTable.SelectRows(Mine, Mine);
            ImpactTable.SelectRows(Mine, Mine);
            IdleTable.SelectRows(Mine, Mine);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static SessionKey? KeyOf(IReadOnlyList<object?> row) => row.Count == 0 ? null : row[^1] switch
    {
        LockItem l => l.Key,
        ImpactItem i => i.Key,
        DdlLockItem d => d.Key,
        IdleTransactionItem i => i.Key,
        _ => null,
    };

    private void PickFromRow(IReadOnlyList<object?> row)
    {
        if (!_syncing && KeyOf(row) is { } key)
        {
            Pick(key);
        }
    }

    private void Pick(SessionKey key)
    {
        if (_vm is { } vm && vm.SelectedKey != key)
        {
            vm.SelectedKey = key;
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
            case Key.Enter when vm.TopTabIndex == LockMonitorViewModel.DeadlockTab && DeadlockTable.CurrentRow is { } dl:
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

    private void OpenDeadlock(IReadOnlyList<object?> row)
    {
        if (row.Count > 0 && row[^1] is DeadlockItem item && _vm is { } vm)
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
