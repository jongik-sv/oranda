using Avalonia;
using Avalonia.Controls;
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
        // 가로 스크롤 안은 폭이 무한이라 * 열이 줄마다 다른 폭이 된다: 표 폭을 창 폭(최소 290)으로 고정하고,
        // 열 폭은 그 폭에서 픽셀로 계산해 머리글과 모든 줄에 같은 값을 넣는다.
        TableScroll.SizeChanged += (_, e) =>
        {
            Table.Width = Math.Max(Table.MinWidth, e.NewSize.Width);
            ApplyColumns();
        };
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

            _vm =DataContext as BindVariablesViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                Watch(_vm.Store);
            }
        };
    }

    private BindVariableStore? _watched;

    // 열: 번호(고정) | 변수 2 | 값 3 | 자료형 2.4 | 방향(고정). 비율 열은 최소 폭을 지킨다.
    private const double NumberWidth = 24, DirectionWidth = 58;
    private static readonly (double Weight, double Min)[] Flex = [(2, 56), (3, 64), (2.4, 84)];

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
}
