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
        CellEditor.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Tab)
            {
                e.Handled = true;
                CommitEdit(moveDown: e.Key == Key.Enter);
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
    private void BeginEdit(GridCell cell)
    {
        if (GridControl.Model is not { } m || Oranda.App.Features.GridEdit.GridEditBinding.Of(m) is not { CanEdit: true } es)
        {
            return;
        }

        var ord = m.VisibleColumns[cell.Col];
        var row = Oranda.App.Features.GridEdit.GridEditBinding.ToEditRow(m, es, cell.Row);
        if (!es.IsEditableColumn(ord) || es.GetRowState(row) == Oranda.App.Features.GridEdit.GridEditRowState.Deleted)
        {
            return;
        }

        GridControl.EnsureVisible(cell);
        if (GridControl.CellBounds(cell) is not { } r)
        {
            return;
        }

        _editing = cell;
        CellEditor.Margin = new Avalonia.Thickness(r.X, r.Y, 0, 0);
        CellEditor.Width = Math.Max(60, r.Width);
        CellEditor.Height = r.Height;
        CellEditor.Text = m.TryGetValue(cell.Row, ord, out var v) && v is not null ? m.GetCellText(cell.Row, ord) : string.Empty;
        CellEditor.IsVisible = true;
        CellEditor.Focus();
        CellEditor.SelectAll();
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
