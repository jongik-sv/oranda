using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Binds;

namespace Oranda.App.Features.Execution;

/// <summary>바인드 변수 창 View. 실행이 새 변수를 만나면 그 줄의 값 칸으로 포커스를 옮긴다(창이 늦게 만들어져도).</summary>
public partial class BindVariablesView : UserControl
{
    private BindVariablesViewModel? _vm;

    public BindVariablesView()
    {
        InitializeComponent();
        // 가로 스크롤 안은 폭이 무한이라 * 열이 줄마다 다른 폭이 된다: 표 폭을 창 폭(최소 232)으로 고정하고,
        // 열 폭은 그 폭에서 픽셀로 계산해 머리글과 모든 줄에 같은 값을 넣는다.
        TableScroll.SizeChanged += (_, e) =>
        {
            Table.Width = Math.Max(Table.MinWidth, e.NewSize.Width);
            ApplyColumns();
        };
        RowList.AddHandler(KeyDownEvent, OnRowListKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        RowList.AddHandler(PointerPressedEvent, OnRowListPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        RowList.AddHandler(PointerMovedEvent, OnRowListPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        RowList.AddHandler(PointerReleasedEvent, OnRowListPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                if (_watched is not null)
                {
                    _watched.FocusRequested -= OnFocusRequested;
                    _watched = null;
                }
            }

            _vm = DataContext as BindVariablesViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                Watch(_vm.Store);
            }
        };
    }

    private BindVariableStore? _watched;

    // 열: 번호(고정) | 변수 2 | 값 3.5(남는 폭) | 자료형 2 | 방향(고정). 비율 열은 최소 폭을 지킨다. 최소 폭 합 = 표 MinWidth(232), 기본 오른쪽 열 폭(0.23)에서 가로 스크롤이 없게.
    private const double NumberWidth = 24, DirectionWidth = 52;
    private static readonly (double Weight, double Min)[] Flex = [(2, 44), (3.5, 48), (2, 64)];

    private double[] ColumnWidths()
    {
        var width = double.IsNaN(Table.Width) ? Table.MinWidth : Table.Width;
        var unit = Math.Max(0, width - NumberWidth - DirectionWidth) / Flex.Sum(f => f.Weight);
        return [NumberWidth, .. Flex.Select(f => Math.Max(f.Min, Math.Floor(f.Weight * unit))), DirectionWidth];
    }

    private static void SetColumns(Grid grid, double[] widths)
    {
        for (int i = 0; i < widths.Length && i < grid.ColumnDefinitions.Count; i++)
        {
            grid.ColumnDefinitions[i].Width = new GridLength(widths[i]);
        }
    }

    private void ApplyColumns()
    {
        var widths = ColumnWidths();
        SetColumns(HeaderGrid, widths);
        foreach (var container in RowList.GetRealizedContainers())
        {
            foreach (var grid in container.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("row")))
            {
                SetColumns(grid, widths);
            }
        }
    }

    private void OnRowGridLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            SetColumns(grid, ColumnWidths());
        }
    }

    // 활성 탭이 바뀌면 목록도 바뀐다: 새 목록의 포커스 요청을 듣는다.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BindVariablesViewModel.Store) && _vm is not null)
        {
            Watch(_vm.Store);
        }
    }

    private void Watch(BindVariableStore store)
    {
        if (_watched is not null)
        {
            _watched.FocusRequested -= OnFocusRequested;
        }

        _watched = store;
        store.FocusRequested += OnFocusRequested;
        if (store.PendingFocus is { } pending)
        {
            Dispatcher.UIThread.Post(() => FocusRow(pending), DispatcherPriority.Background);
        }
    }

    private void OnFocusRequested(object? sender, string name) =>
        Dispatcher.UIThread.Post(() => FocusRow(name), DispatcherPriority.Background);

    /// <summary>이름이 같은 줄(빈 이름이면 마지막 빈 줄)을 고르고 값 칸(빈 이름이면 이름 칸)에 포커스를 둔다.</summary>
    private void FocusRow(string name)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        var row = name.Length == 0
            ? _vm.Rows.LastOrDefault(r => r.Name.Trim().Length == 0)
            : _vm.Store.Find(name);
        if (row is null)
        {
            return;
        }

        _vm.Store.PendingFocus = null;
        _vm.Selected = row;
        RowList.ScrollIntoView(row);
        RowList.UpdateLayout();
        if (RowList.ContainerFromItem(row) is not Control container)
        {
            return;
        }

        var boxName = name.Length == 0 ? "NameBox" : "ValueBox";
        if (container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == boxName) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    // ---- 블록 선택·복사·붙여 넣기(TSK-03-09) -------------------------------------------------

    // 끌어서 고르기: 누른 줄에서 다른 줄로 끌면 그 사이 줄을 모두 고른다. 입력 칸에서 시작해도 다른 줄로 넘어가면 글 고르기를 멈추고
    // 줄 고르기로 바꾼다(칸의 글 선택을 풀고 포커스를 줄로 옮겨 Ctrl+C 가 줄을 복사하게). 콤보에서 누른 것과 Shift·Ctrl 클릭은 목록 몫이다.
    private int _dragAnchor = -1;
    private bool _dragRows;

    private int RowIndexAt(Avalonia.Input.PointerEventArgs e) =>
        (RowList.InputHitTest(e.GetPosition(RowList)) as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            ? RowList.IndexFromContainer(item)
            : -1;

    private void OnRowListPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _dragRows = false;
        _dragAnchor = e.GetCurrentPoint(RowList).Properties.IsLeftButtonPressed && e.KeyModifiers == Avalonia.Input.KeyModifiers.None
            && (e.Source as Visual)?.FindAncestorOfType<ComboBox>(includeSelf: true) is null
            ? RowIndexAt(e)
            : -1;
    }

    private void OnRowListPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_dragAnchor < 0 || !e.GetCurrentPoint(RowList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var index = RowIndexAt(e);
        if (index < 0 || (!_dragRows && index == _dragAnchor))
        {
            return;
        }

        if (!_dragRows)
        {
            _dragRows = true;
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box)
            {
                box.ClearSelection();
            }

            e.Pointer.Capture(RowList); // 입력 칸이 잡은 포인터를 넘겨받아 칸이 글을 더 고르지 않게
            RowList.ContainerFromIndex(_dragAnchor)?.Focus(); // 목록은 포커스를 받지 않는다: 줄 번호 칸을 누를 때처럼 줄에 둔다
        }

        RowList.Selection.BeginBatchUpdate();
        RowList.Selection.Clear();
        RowList.Selection.SelectRange(Math.Min(_dragAnchor, index), Math.Max(_dragAnchor, index));
        RowList.Selection.EndBatchUpdate();
        e.Handled = true;
    }

    private void OnRowListPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_dragRows)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        _dragAnchor = -1;
        _dragRows = false;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        _vm.SelectedRows = RowList.SelectedItems?.OfType<BindVariableRow>().ToList() ?? [];
        _vm.DeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>값 칸을 떠날 때 자료형에 맞게 글을 정리한다.</summary>
    private void OnValueLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BindVariableRow row })
        {
            row.Commit();
        }
    }

    private void OnRowListKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var primary = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
        if (e.KeyModifiers != primary || (e.Key != Avalonia.Input.Key.C && e.Key != Avalonia.Input.Key.V))
        {
            return;
        }

        var inText = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        if (e.Key == Avalonia.Input.Key.C)
        {
            // 입력 칸 안에서 글을 골랐으면 글 복사가 먼저다.
            if (inText && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { SelectionStart: var a, SelectionEnd: var b } && a != b)
            {
                return;
            }

            e.Handled = true;
            _ = CopyAsync(false);
        }
        else
        {
            e.Handled = true;
            _ = PasteAsync(inText);
        }
    }

    internal async Task CopyAsync(bool withHeader)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || _vm.CopyText(withHeader) is not { } text)
        {
            return;
        }

        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 클립보드를 다른 프로그램이 잡고 있으면(Windows OLE) 실패한다: 삼키지 말고 창 아래 줄에 알린다.
            System.Diagnostics.Trace.TraceWarning("바인드 변수 복사 실패: {0}", ex.Message);
            _vm.StatusText = "클립보드에 복사하지 못했습니다: " + ex.Message;
        }
    }

    /// <summary>클립보드의 TSV 를 붙여 넣는다. 입력 칸 안에서 탭·줄바꿈이 없는 한 줄 글이면 그냥 칸에 붙인다.</summary>
    internal async Task PasteAsync(bool fromTextBox = false)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        string? text;
        try
        {
            text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning("바인드 변수 붙여 넣기 실패: {0}", ex.Message);
            _vm.StatusText = "클립보드를 읽지 못했습니다: " + ex.Message;
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (fromTextBox && text.IndexOfAny(['\t', '\n', '\r']) < 0)
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box)
            {
                box.Paste();
            }

            return;
        }

        _vm.PasteText(text);
    }

    private void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = CopyAsync(false);

    private void OnCopyWithHeaderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = CopyAsync(true);

    private void OnPasteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = PasteAsync();
}
