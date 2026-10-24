using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;

namespace Oranda.App.Features.Tuning.Cursors;

/// <summary>자식 커서 문서. 자식·사유 표는 <c>DetailTable</c>, 비교 체크는 첫 칸 체크 상자(최대 2개).</summary>
public partial class ChildCursorsView : UserControl
{
    /// <summary>자식 표의 「공유 가능」 칸.</summary>
    internal const int ShareableColumn = 6;

    private readonly Dictionary<IReadOnlyList<object?>, ChildCursorItem> _rows = new(ReferenceEqualityComparer.Instance);
    private ChildCursorsViewModel? _bound;

    public ChildCursorsView()
    {
        InitializeComponent();
        ChildTable.RightAlignedColumns = [1, 2, 3, 7, 8, 9];
        ChildTable.MonospaceColumns = [3];
        ChildTable.CellFactory = (c, row) => c == 0 && _rows.TryGetValue(row, out var item) ? CompareBox(item) : null;
        ChildTable.CellEmphasis = (c, row) => c == ShareableColumn && _rows.TryGetValue(row, out var item) && !item.Row.IsShareable;
        ChildTable.CellWarning = (c, row) => _rows.TryGetValue(row, out var item) && ((c == 8 && item.GetsWorse) || (c == 9 && item.ElapsedWorse));
        ChildTable.CurrentRowChanged += (_, row) =>
        {
            if (_bound is { } vm && _rows.TryGetValue(row, out var item))
            {
                vm.CurrentItem = item;
            }
        };
        ChildTable.DoubleTapped += (_, _) => _ = _bound?.OpenCurrentPlanAsync();
        ReasonTable.MonospaceColumns = [1];
        ReasonTable.RightAlignedColumns = [0];
        DataContextChanged += (_, _) => Bind(DataContext as ChildCursorsViewModel);
        RefreshButton.Click += (_, _) => _ = _bound?.LoadAsync();
        CompareButton.Click += (_, _) => _ = _bound?.CompareAsync(reload: true);
        PlanButton.Click += (_, _) => _ = _bound?.OpenCurrentPlanAsync();
        StatsButton.Click += (_, _) => _bound?.OpenPlanTableStats();
        AccessBanner.OtherAccountRequested += (_, _) => _ = _bound?.OpenWithOtherAccountAsync();
        CopyButton.Click += async (_, _) =>
        {
            if (_bound?.BuildCopyText() is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        };
        SqlIdBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _bound is { } vm)
            {
                e.Handled = true;
                _ = vm.ApplySqlIdTextAsync();
            }
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None && _bound is { CanRefresh: true } vm)
            {
                e.Handled = true;
                _ = vm.LoadAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void Bind(ChildCursorsViewModel? vm)
    {
        if (ReferenceEquals(_bound, vm))
        {
            return;
        }

        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelChanged;
        }

        _bound = vm;
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnViewModelChanged;
        FillChildren();
        FillReasons();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChildCursorsViewModel.VisibleItems):
                FillChildren();
                break;
            case nameof(ChildCursorsViewModel.Reasons):
                FillReasons();
                break;
        }
    }

    private void FillChildren()
    {
        _rows.Clear();
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var item in _bound?.VisibleItems ?? [])
        {
            var row = item.Cells;
            _rows[row] = item;
            rows.Add(row);
        }

        ChildTable.Columns = ChildCursorItem.Headers;
        ChildTable.Rows = rows;
        if (_bound?.CurrentItem is { } current)
        {
            ChildTable.SelectRows(r => _rows.TryGetValue(r, out var i) && ReferenceEquals(i, current),
                r => _rows.TryGetValue(r, out var i) && ReferenceEquals(i, current));
        }
    }

    private void FillReasons()
    {
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var r in _bound?.Reasons ?? [])
        {
            rows.Add(r.Cells);
        }

        ReasonTable.Columns = CursorReasonItem.Headers;
        ReasonTable.Rows = rows;
        ReasonTable.IsVisible = rows.Count > 0;
    }

    // 비교 칸: 자식 줄의 IsCompared 에 묶인 체크 상자(셋째를 체크하면 ViewModel 이 가장 오래된 체크를 푼다).
    private static CheckBox CompareBox(ChildCursorItem item)
    {
        var box = new CheckBox
        {
            DataContext = item,
            MinWidth = 0,
            MinHeight = 0,
            // Fluent 체크 상자는 32 DIP 칸 가운데에 20 DIP 상자를 그린다. 위아래 6 을 겹쳐 표 줄 높이(20)에 맞춘다.
            Height = 32,
            Margin = new Avalonia.Thickness(0, -6),
            Padding = default,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(box, Stats.TuningText.T("비교에 넣기 · ", "Compare · ") + item.Label);
        box.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(ChildCursorItem.IsCompared)) { Mode = BindingMode.TwoWay });
        return box;
    }
}
