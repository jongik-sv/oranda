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

        GridControl.EditRequested += (_, cell) => BeginEdit(cell);
        EditHintClose.Click += (_, _) => Vm?.HideEditHint();
        EditHintRerun.Click += async (_, _) => await RunEditHintActionAsync();
        EditHintRowId.Click += async (_, _) => await RunEditHintActionAsync();
        CountCancelButton.Click += (_, _) => Vm?.CancelCountRows();
        SelectionSummaryText.Tapped += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.CopySelectionSummaryAsync();
            }
        };

        // 편집 툴바 단추: Tag 의 명령을 실행한다(오른쪽 클릭 메뉴와 같은 명령, 포커스는 그리드에 둔다).
        AddHandler(Button.ClickEvent, async (_, e) =>
        {
            if (e.Source is Button { Tag: string id } && id.StartsWith("oranda.grid.edit.", StringComparison.Ordinal))
            {
                e.Handled = true;
                PushGridKeys();
                await ShellCommandRunner.RunAsync(id);
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
        CellEditor.LostFocus += (_, _) =>
        {
            if (CellEditor.IsVisible)
            {
                CommitEdit(moveDown: false);
            }
        };
    }

    private GridCell? _editing;

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
        }

        return true;
    }

    private void CommitAndEditNext(int step)
    {
        if (_editing is not { } cell || GridControl.Model is not { } m)
        {
            CommitEdit(moveDown: false);
            return;
        }

        CommitEdit(moveDown: false);
        var cols = m.VisibleColumns.Count;
        var index = (cell.Row * cols) + cell.Col + step;
        if (cols == 0 || index < 0 || index >= m.RowCount * cols)
        {
            return;
        }

        var next = new GridCell(index / cols, (int)(index % cols));
        m.SelectCell(next);
        GridControl.EnsureVisible(next);
        BeginEdit(next);
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
        es.SetCell(row, ord, Oranda.App.Features.GridEdit.GridEditBinding.Parse(m, ord, CellEditor.Text));
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

    private Task RunEditHintActionAsync() => Vm?.RunEditHintActionAsync() ?? Task.CompletedTask;

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnVmPropertyChanged;
            _bound = null;
        }

        if (Vm is not { } vm)
        {
            GridControl.Model = null;
            return;
        }

        _bound = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
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
        }

        SyncBars();
    }

    private void OnModelChanged(object? sender, EventArgs e) => PushGridKeys();

    /// <summary>그리드 명령(복사·열 고정 등)의 활성 조건 키를 맞춘다. 포커스가 이 그리드에 있을 때만 의미가 있다.</summary>
    private void PushGridKeys()
    {
        if (ContextKeyService.Shared is not { } keys || GridControl.Model is not { } m || !(GridControl.IsFocused || IsKeyboardFocusWithin))
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
            VBar.Maximum = GridControl.MaxOffsetY;
            VBar.ViewportSize = GridControl.ViewportHeight;
            VBar.LargeChange = GridControl.ViewportHeight;
            VBar.SmallChange = GridControl.RowHeight;
            VBar.Value = GridControl.OffsetY;
            VBar.IsVisible = GridControl.MaxOffsetY > 0;
            HBar.Maximum = GridControl.MaxOffsetX;
            HBar.ViewportSize = GridControl.ViewportWidth;
            HBar.LargeChange = GridControl.ViewportWidth;
            HBar.SmallChange = 40;
            HBar.Value = GridControl.OffsetX;
            HBar.IsVisible = GridControl.MaxOffsetX > 0;
        }
        finally
        {
            _syncing = false;
        }
    }
}
