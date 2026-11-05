using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell;

namespace Oranda.App.Features.ResultGrid;

public partial class SqlResultPaneView : UserControl
{
    private bool _syncing;
    private SqlResultPaneViewModel? _bound;

    public SqlResultPaneView()
    {
        InitializeComponent();
        GotFocus += (_, _) => PushGridKeys();
        AttachedToVisualTree += (_, _) => TakeOverAsTargetInResults();
        ShellContextMenus.Attach(GridControl, "grid");
        GridControl.ScrollChanged += (_, _) => SyncBars();
        GridControl.SizeChanged += (_, _) => SyncBars();
        VBar.Scroll += (_, e) =>
        {
            if (!_syncing)
            {
                GridControl.ScrollTo(GridControl.OffsetX, e.NewValue);
            }
        };
        HBar.Scroll += (_, e) =>
        {
            if (!_syncing)
            {
                GridControl.ScrollTo(e.NewValue, GridControl.OffsetY);
            }
        };
        FindNextButton.Click += async (_, _) => await FindNextAsync();
        FindCloseButton.Click += (_, _) => CloseFind();
        FindBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await FindNextAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseFind();
            }
        };
        DataContextChanged += (_, _) => Bind();
        WireFilterBar();
        WireQuickFilter();

        GridControl.ColumnFilterRequested += async (_, e) => await OpenColumnFilterAsync(e);
        GridControl.EditRequested += (_, cell) => BeginEdit(cell);
        GridControl.CellDoubleTapped += (_, cell) => OnCellDoubleTapped(cell);
        GridControl.RowOrderChanging += (_, _) => CommitOpenEditor();
        GridControl.HeaderInsertRequested += (_, ordinal) => Vm?.InsertColumnName(ordinal);
        EditHintClose.Click += (_, _) => Vm?.HideEditHint();
        EditHintRerun.Click += async (_, _) => await RunEditHintActionAsync();
        EditHintRowId.Click += async (_, _) => await RunEditHintActionAsync();
        CountCancelButton.Click += (_, _) => Vm?.CancelCountRows();
        SortCancelButton.Click += (_, _) => Vm?.CancelSort();
        FetchCancelButton.Click += (_, _) => Vm?.CancelFetchAll();
        SelectionSummaryText.Tapped += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.CopySelectionSummaryAsync();
            }
        };

        // 툴바 단추(편집 명령): Tag 의 명령을 실행한다(오른쪽 클릭 메뉴와 같은 명령, 포커스는 그리드에 둔다).
        AddHandler(Button.ClickEvent, async (_, e) =>
        {
            if (e.Source is Button { Tag: string id } && id.StartsWith("oranda.grid.edit.", StringComparison.Ordinal))
            {
                e.Handled = true;
                await RunOnThisGridAsync(id);
            }
        });

        // 칸을 고른 채 글자를 치면 바로 그 글자로 편집을 시작한다(SQL Developer·Excel 처럼).
        GridControl.TextInput += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]) && GridControl.Model is { HasSelection: true } m
                && BeginEdit(m.Active, e.Text))
            {
                e.Handled = true;
            }
        };
        CellEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitEdit(moveDown: true);
            }
            else if (e.Key == Key.Tab)
            {
                // Tab·Shift+Tab: 반영하고 옆 칸(줄 끝이면 다음·앞 줄)으로 옮겨 이어서 편집한다.
                e.Handled = true;
                CommitAndEditNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelEdit();
            }
        };
        // ↑·↓: 반영하고 같은 열의 위·아래 행 셀 편집으로 이어 간다(첫 행의 ↑·끝 행의 ↓ 는 반영만 하고 닫는다). 값에 줄바꿈이 있어도 행 이동이다.
        // 입력 칸이 이 키를 먼저 처리하므로(커서 이동) 터널링 단계에서 가로챈다.
        CellEditor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
            {
                e.Handled = true;
                CommitAndEditRow(e.Key == Key.Up ? -1 : 1);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        CellEditor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && CellEditor.IsVisible && !string.Equals(CellEditor.Text, _editBaseline, StringComparison.Ordinal))
            {
                _editTouched = true; // 열 때 넣은 글(baseline)과 다른 글이 한 번이라도 있었다 = 고쳤다
            }
        };
        CellEditor.LostFocus += (_, _) =>
        {
            if (CellEditor.IsVisible)
            {
                CommitEdit(moveDown: false);
            }
        };
    }

    private GridCell? _editing;

    // 셀 편집기를 열 때 보인 글. 확정할 때 글이 같으면(지나가기만 했으면) 값을 건드리지 않는다 —
    // 표시 글(여러 줄의 ↵, 같은 값 숨김의 빈 글자)을 값으로 읽어 손대지 않은 칸이 바뀌는 것을 막는다.
    private string _editBaseline = string.Empty;

    // 사용자가 편집기 글을 실제로 고쳤는가(열 때 넣은 글은 제외). 같은 값 숨김처럼 보이는 글이 빈 글자인 칸을 지워서 NULL 로 만들려는 뜻은 글이 같아도 살린다.
    private bool _editTouched;

    private void CommitOpenEditor()
    {
        if (_editing is not null)
        {
            CommitEdit(moveDown: false);
        }
    }

    // 값 더블클릭: 편집 모드(편집 세션이 있는 결과)면 예전처럼 셀 편집을 열고, 아니면 편집기에 SQL 리터럴로 넣는다. 넣을 편집기가 없는 영역(객체 상세 등)은 예전 동작을 유지한다.
    private void OnCellDoubleTapped(GridCell cell)
    {
        if (GridControl.Model is not { } m)
        {
            return;
        }

        if (Vm is { CanInsertToEditor: true } vm && Oranda.App.Features.GridEdit.GridEditBinding.Of(m) is null)
        {
            vm.InsertCellValue(cell);
            return;
        }

        BeginEdit(cell);
    }

    /// <summary>편집 모드 그리드에서 셀 편집기를 연다. 키 열이 아니어도 편집할 수 없는 열(대상 테이블 밖)은 열지 않는다.</summary>
    private bool BeginEdit(GridCell cell, string? typed = null)
    {
        if (GridControl.Model is not { HasResult: true } m)
        {
            return false;
        }

        if (Oranda.App.Features.GridEdit.GridEditBinding.Of(m) is not { CanEdit: true } es)
        {
            Vm?.ShowEditHint(); // 읽기 전용(또는 고칠 수 없는) 결과: 조용히 무시하지 않고 편집 모드로 다시 실행하라고 알린다
            return false;
        }

        var ord = m.VisibleColumns[cell.Col];
        var row = Oranda.App.Features.GridEdit.GridEditBinding.ToEditRow(m, es, cell.Row);
        if (!es.IsEditableColumn(ord) || es.GetRowState(row) == Oranda.App.Features.GridEdit.GridEditRowState.Deleted)
        {
            return false;
        }

        GridControl.EnsureVisible(cell);
        if (GridControl.CellBounds(cell) is not { } r)
        {
            return false;
        }

        _editing = cell;
        CellEditor.Margin = new Avalonia.Thickness(r.X, r.Y, 0, 0);
        CellEditor.Width = Math.Max(60, r.Width);
        CellEditor.Height = r.Height;
        CellEditor.Text = m.TryGetValue(cell.Row, ord, out var v) && v is not null ? m.GetCellText(cell.Row, ord) : string.Empty;
        _editBaseline = CellEditor.Text;
        _editTouched = false;
        CellEditor.IsVisible = true;
        CellEditor.Focus();
        if (typed is null)
        {
            CellEditor.SelectAll();
        }
        else
        {
            CellEditor.Text = typed; // 친 글자로 값을 바꿔 쓴다
            CellEditor.CaretIndex = typed.Length;
            _editTouched = true;
        }

        return true;
    }

    private void CommitAndEditNext(int step) => CommitAndEditAt(cell =>
    {
        var cols = GridControl.Model!.VisibleColumns.Count;
        var index = (cell.Row * cols) + cell.Col + step;
        return cols == 0 || index < 0 || index >= GridControl.Model.RowCount * cols ? null : new GridCell(index / cols, (int)(index % cols));
    });

    // ↑·↓: 같은 열의 위·아래 행. 가져온 마지막 행에서 ↓ 이고 더 가져올 행이 있으면 다음 페이지를 가져와 이어 간다.
    private void CommitAndEditRow(int step)
    {
        if (_editing is { } cell && GridControl.Model is { IsLocalFiltered: false, IsSorted: false, HasMoreRows: true } m && step > 0 && cell.Row + 1 >= m.RowCount
            && m.RowCount == m.BaseRowCount) // 반영하지 않은 새 행이 끝에 붙어 있으면 가져온 행이 그 사이로 끼어들어 이어 갈 칸이 어긋난다
        {
            CommitEdit(moveDown: false);
            _ = ContinueAfterFetchAsync(m, cell);
            return;
        }

        CommitAndEditAt(cell =>
        {
            var row = cell.Row + step;
            return row < 0 || row >= GridControl.Model!.RowCount ? null : cell with { Row = row };
        });
    }

    private async Task ContinueAfterFetchAsync(ResultGridModel m, GridCell last)
    {
        try
        {
            await m.FetchNextPageAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            m.ReportNotice("다음 행을 가져오지 못했습니다: " + e.Message.ReplaceLineEndings(" "));
            return;
        }

        // 가져오는 사이 사용자가 다른 칸을 고르거나 편집을 시작했으면 그대로 둔다(입력 중인 값을 덮어쓰지 않는다).
        var target = last with { Row = last.Row + 1 };
        if (ReferenceEquals(GridControl.Model, m) && _editing is null && m.Active == last && target.Row < m.RowCount)
        {
            m.SelectCell(target);
            GridControl.EnsureVisible(target);
            BeginEdit(target);
        }
    }

    /// <summary>편집 중인 값을 반영하고, <paramref name="next"/> 가 돌려준 칸의 편집을 이어서 연다. 돌려줄 칸이 없으면(끝) 반영만 하고 닫는다.</summary>
    private void CommitAndEditAt(Func<GridCell, GridCell?> next)
    {
        if (_editing is not { } cell || GridControl.Model is not { } m)
        {
            CommitEdit(moveDown: false);
            return;
        }

        CommitEdit(moveDown: false);
        if (next(cell) is not { } target)
        {
            return;
        }

        m.SelectCell(target);
        GridControl.EnsureVisible(target);
        BeginEdit(target);
    }

    private void CommitEdit(bool moveDown)
    {
        if (_editing is not { } cell || GridControl.Model is not { } m || Oranda.App.Features.GridEdit.GridEditBinding.Of(m) is not { } es)
        {
            CancelEdit();
            return;
        }

        var ord = m.VisibleColumns[cell.Col];
        var row = Oranda.App.Features.GridEdit.GridEditBinding.ToEditRow(m, es, cell.Row);
        if (!string.Equals(CellEditor.Text, _editBaseline, StringComparison.Ordinal) || (_editTouched && string.IsNullOrEmpty(CellEditor.Text)))
        {
            es.SetCell(row, ord, Oranda.App.Features.GridEdit.GridEditBinding.Parse(m, ord, CellEditor.Text));
        }

        CancelEdit();
        if (moveDown && cell.Row + 1 < m.RowCount)
        {
            m.SelectCell(cell with { Row = cell.Row + 1 });
            GridControl.EnsureVisible(m.Active);
        }
    }

    private void CancelEdit()
    {
        _editing = null;
        CellEditor.IsVisible = false;
        GridControl.Focus();
    }

    private SqlResultPaneViewModel? Vm => DataContext as SqlResultPaneViewModel;

    private void OnCommitEditorRequested(object? sender, EventArgs e) => CommitOpenEditor();

    private Task RunEditHintActionAsync() => Vm?.RunEditHintActionAsync() ?? Task.CompletedTask;

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnVmPropertyChanged;
            _bound.FilterFocusRequested -= OnFilterFocusRequested;
            _bound.QuickFilterFocusRequested -= OnQuickFilterFocusRequested;
            _bound.CommitEditorRequested -= OnCommitEditorRequested;
            _bound = null;
        }

        if (Vm is not { } vm)
        {
            GridControl.Model = null;
            GridControl.HeaderDoubleClickInserts = false;
            return;
        }

        GridControl.HeaderDoubleClickInserts = vm.CanInsertToEditor; // 편집기 결과 탭만: 머리글 더블클릭 = 이름 넣기(첫 클릭 정렬은 더블클릭 시간만큼 미룸)
        _bound = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.FilterFocusRequested += OnFilterFocusRequested;
        vm.QuickFilterFocusRequested += OnQuickFilterFocusRequested;
        vm.CommitEditorRequested += OnCommitEditorRequested;
        AttachModel(vm);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not SqlResultPaneViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(SqlResultPaneViewModel.Model))
        {
            AttachModel(vm);
        }
        else if (e.PropertyName == nameof(SqlResultPaneViewModel.IsFindVisible) && vm.IsFindVisible)
        {
            FindBox.Focus();
        }
    }

    /// <summary>
    /// 「결과 안에서 거르기」 칸(TSK-13-26): Esc 는 글을 지우고 빈 칸이면 칸을 닫는다. 지우기 단추는 열 필터까지 모두 지운다.
    /// 「모두 가져오기」는 가져온 행만 거르는 중일 때 나머지 행을 가져와 거르기에 넣는다.
    /// </summary>
    private void WireQuickFilter()
    {
        QuickFilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Vm is { } vm)
            {
                e.Handled = true;
                if (!vm.ClearOrCloseQuickFilter())
                {
                    GridControl.Focus();
                }
            }
        };
        QuickFilterClearButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.ClearLocalFiltersAsync();
            }
        };
        QuickFilterCloseButton.Click += (_, _) =>
        {
            if (Vm is { } vm)
            {
                vm.IsQuickFilterVisible = false;
                GridControl.Focus();
            }
        };
        FilterFetchAllButton.Click += async (_, _) => await RunOnThisGridAsync("oranda.grid.fetchAll");
    }

    /// <summary>
    /// 이 창의 단추로 그리드 명령을 실행한다. 단추는 포커스를 받지 않고(Focusable=False) 실행 뒤 포커스는 편집기에 남으므로,
    /// 포커스를 기다리지 않고 이 그리드를 명령의 대상으로 삼아 활성 조건 키를 먼저 맞춘다.
    /// </summary>
    private async Task RunOnThisGridAsync(string commandId)
    {
        ResultGridCommands.Active = GridControl.Model ?? ResultGridCommands.Active;
        PublishGridKeys();
        await ShellCommandRunner.RunAsync(commandId);
    }

    /// <summary>
    /// 머리글 ▾ 자동 필터 팝업(TSK-13-26): 가져온 행의 서로 다른 값을 모아(UI 스레드 밖) 값 목록·조건 패널을 열고, 적용하면 그 열 필터만 바꾼다.
    /// 반영하지 않은 편집이 있으면 모델이 거부하고 상태 줄에 알린다.
    /// </summary>
    private async Task OpenColumnFilterAsync(ColumnFilterRequestedEventArgs e)
    {
        if (Vm is not { Model: { } m } vm)
        {
            return;
        }

        var ordinal = e.Ordinal;
        ColumnValueList values;
        try
        {
            values = await m.CollectColumnValuesAsync(ordinal);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var panel = new ColumnFilterPanel(m.Columns[ordinal], values, m.LocalFilter.Columns.GetValueOrDefault(ordinal));
        panel.Applied += async (_, filter) =>
        {
            ColumnFilterHost.IsOpen = false;
            await vm.SetColumnFilterAsync(ordinal, filter);
            GridControl.Focus();
        };
        panel.CloseRequested += (_, _) =>
        {
            ColumnFilterHost.IsOpen = false;
            GridControl.Focus();
        };
        ColumnFilterHost.Child = new Border { Child = panel, Background = Avalonia.Media.Brushes.Transparent };
        ColumnFilterHost.PlacementRect = e.HeaderBounds;
        ColumnFilterHost.IsOpen = true;
    }

    private void OnQuickFilterFocusRequested(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => QuickFilterBox.Focus());

    private void OnFilterFocusRequested(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => WhereBox.Focus()); // 줄이 보인 뒤에 포커스를 옮긴다

    private void AttachModel(SqlResultPaneViewModel vm)
    {
        if (GridControl.Model is { } old)
        {
            old.Changed -= OnModelChanged;
        }

        GridControl.Model = vm.Model;
        if (vm.Model is { } m)
        {
            m.Changed += OnModelChanged;
            m.ClipboardWriter = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
                {
                    await cb.SetTextAsync(text);
                }
            };
            m.ConfirmLargeSort = (request, ct) => LargeSortDialog.AskAsync(TopLevel.GetTopLevel(this) as Window, request, ct);
            m.ClipboardReader = async () =>
                TopLevel.GetTopLevel(this)?.Clipboard is { } cb ? await cb.TryGetTextAsync() : null;
        }

        SyncBars();
        TakeOverAsTargetInResults();
    }

    /// <summary>
    /// 결과 창에 포커스가 있는 채 보이는 결과가 바뀌면(결과 탭 전환·다시 실행) 새 그리드가 포커스를 받기 전이라도 명령의 대상이 된다.
    /// 그러지 않으면 Alt+End 같은 단축키가 화면에 없는 옛 그리드로 가거나 옛 키로 거부된다.
    /// 포커스 자리는 자리 없는 곳(객체 상세 데이터 탭의 그리드 등)으로 옮겨도 직전 값이 남으므로, 지금 포커스가 정말 결과 창에 있는지도 본다.
    /// 포커스가 없거나 떨어져 나간 컨트롤에 남은 것은 탭 전환으로 옛 그리드가 빠진 경우다.
    /// </summary>
    private void TakeOverAsTargetInResults()
    {
        if (GridControl.IsCommandTarget && GridControl.Model is { } m && Oranda.App.Shell.Keymaps.FocusSlot.Find(this) == "sqlGrid"
            && ContextKeyService.Shared is { FocusSlot: "sqlGrid" }
            && (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Avalonia.Visual now
                || TopLevel.GetTopLevel(now) is null
                || Oranda.App.Shell.Keymaps.FocusSlot.Find(now) == "sqlGrid"))
        {
            ResultGridCommands.Active = m;
            PublishGridKeys();
        }
    }

    private void OnModelChanged(object? sender, EventArgs e) => PushGridKeys();

    /// <summary>그리드 명령(복사·열 고정 등)의 활성 조건 키를 맞춘다. 포커스가 이 그리드에 있을 때만 의미가 있다.</summary>
    private void PushGridKeys()
    {
        if (GridControl.IsFocused || IsKeyboardFocusWithin)
        {
            PublishGridKeys();
        }
    }

    /// <summary>이 그리드의 상태를 활성 조건 키로 옮긴다(포커스 검사 없음: 포커스 변화나 이 창의 단추가 부른다).</summary>
    private void PublishGridKeys()
    {
        if (ContextKeyService.Shared is not { } keys || GridControl.Model is not { } m)
        {
            return;
        }

        keys.Set("gridHasResult", m.HasResult);
        keys.Set("gridHasSelection", m.HasSelection);
        keys.Set("gridHasMoreRows", m.HasMoreRows); // 「모두 가져오기」(이 키를 아무도 넣지 않아 늘 꺼져 있었다)
        keys.Set("gridQueryResult", Vm?.CanCountRows ?? false); // 「전체 행 수 세기」: 질의 문장이 만든 결과

        // 편집 모드 키(commands.md: 행 추가·반영 등의 활성 조건). 이 그리드의 세션을 편집 명령의 대상으로 둔다.
        var es = Oranda.App.Features.GridEdit.GridEditBinding.Of(m);
        if (es is not null)
        {
            Oranda.App.Features.GridEdit.GridEditCommands.Active = es;
        }

        keys.Set("gridEditing", es is { CanEdit: true });
        keys.Set("gridRowsEditable", es is { CanEdit: true } || (es is null && (Vm?.CanStartEditMode ?? false))); // 행 삭제·복제: 편집 모드이거나 켤 수 있는 결과
        keys.Set("gridDirty", es is { IsDirty: true });
        keys.Set("gridNotEditable", es is { CanEdit: false });
        keys.Set("singleTable", es?.Capability.CanAddRowId ?? false);
    }

    private async Task FindNextAsync()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        await vm.FindNextAsync();
        if (vm.Model is { HasSelection: true } m)
        {
            GridControl.EnsureVisible(m.Active);
        }
    }

    /// <summary>
    /// 거르기 줄(GRID-10): Enter 적용, Esc 는 칸을 비우고 빈 칸이면 줄을 닫는다. 단추는 적용·취소·지우기·닫기·최근 조건.
    /// </summary>
    private void WireFilterBar()
    {
        void OnFilterKey(TextBox box, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = Vm?.ApplyFilterAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(box.Text))
                {
                    box.Text = string.Empty;
                }
                else
                {
                    CloseFilterBar();
                }
            }
        }

        WhereBox.KeyDown += (_, e) => OnFilterKey(WhereBox, e);
        OrderByBox.KeyDown += (_, e) => OnFilterKey(OrderByBox, e);
        FilterApplyButton.Click += async (_, _) => await (Vm?.ApplyFilterAsync() ?? Task.CompletedTask);
        FilterCancelButton.Click += (_, _) => Vm?.CancelFilter();
        FilterClearButton.Click += async (_, _) => await (Vm?.ClearFilterAsync() ?? Task.CompletedTask);
        FilterCloseButton.Click += (_, _) => CloseFilterBar();
        FilterHistoryButton.Click += (_, _) => ShowFilterHistory();
    }

    /// <summary>최근 조건 목록을 단추 아래에 띄운다. 고르면 WHERE 칸에 넣고 바로 거른다.</summary>
    private void ShowFilterHistory()
    {
        if (Vm is not { HasFilterHistory: true } vm)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var condition in vm.FilterHistory)
        {
            // 머리글을 글 블록으로 둔다: 문자열이면 밑줄(_)을 접근 키로 읽어 DEPT_NO 의 밑줄이 사라진다.
            var item = new MenuItem { Header = new TextBlock { Text = condition } };
            item.Click += async (_, _) =>
            {
                vm.WhereText = condition;
                await vm.ApplyFilterAsync();
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(FilterHistoryButton);
    }

    private void CloseFilterBar()
    {
        if (Vm is { } vm)
        {
            vm.IsFilterBarVisible = false;
        }

        GridControl.Focus();
    }

    private void CloseFind()
    {
        if (Vm is { } vm)
        {
            vm.IsFindVisible = false;
        }

        GridControl.Focus();
    }

    private void SyncBars()
    {
        _syncing = true;
        try
        {
            GridControl.SyncScrollBars(VBar, HBar);
        }
        finally
        {
            _syncing = false;
        }
    }
}
