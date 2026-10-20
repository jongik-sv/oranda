using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Oranda.Oracle.Schema;

namespace Oranda.App.Features.ColumnReorder;

/// <summary>열 순서 바꾸기 대화상자. 열 목록은 끌어서 옮기고(<c>b5b23cf</c> 의 탭 끌기와 같은 방식), 위·아래 단추로도 옮긴다.</summary>
public partial class ColumnReorderWindow : Window
{
    private const double DragThreshold = 6;

    private ColumnReorderItem? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    public ColumnReorderWindow()
    {
        InitializeComponent();
        InvisibleRadio.Click += (_, _) => Vm?.Method = ColumnReorderMethod.InvisibleVisible;
        RebuildRadio.Click += (_, _) => Vm?.Method = ColumnReorderMethod.Rebuild;
        UpButton.Click += (_, _) => MoveSelected(up: true);
        DownButton.Click += (_, _) => MoveSelected(up: false);
        PreviewButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.ShowPreviewAsync();
            }
        };
        RunButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.RunAsync();
            }
        };
        OpenScriptButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.OpenScriptAsync();
            }
        };
        CloseButton.Click += (_, _) => Close();
        ColumnList.AddHandler(PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ColumnList.AddHandler(PointerMovedEvent, OnPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ColumnList.AddHandler(PointerReleasedEvent, OnPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => TableText.Text = Vm?.Table is { } t ? $"{t.Owner}.{t.Name}" : string.Empty;
        Closed += (_, _) => Vm?.Dispose();
    }

    private ColumnReorderViewModel? Vm => DataContext as ColumnReorderViewModel;

    /// <summary>표 이름 줄을 새로 그린다(대화상자를 열고 열을 읽은 뒤 부른다).</summary>
    public void RefreshHeader() => TableText.Text = Vm?.Table is { } t ? $"{t.Owner}.{t.Name}" : string.Empty;

    private void MoveSelected(bool up)
    {
        if (Vm is not { } vm || ColumnList.SelectedItem is not ColumnReorderItem item)
        {
            return;
        }

        if (up ? vm.MoveUp(item) : vm.MoveDown(item))
        {
            ColumnList.SelectedItem = item;
        }
    }

    private static ColumnReorderItem? ItemOf(object? source)
        => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ColumnReorderItem;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ColumnList).Properties.IsLeftButtonPressed && ItemOf(e.Source) is { } item)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(ColumnList);
            _dragging = false;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null)
        {
            return;
        }

        var p = e.GetPosition(ColumnList);
        if (!_dragging && Math.Abs(p.Y - _dragStart.Y) >= DragThreshold)
        {
            _dragging = true;
        }

        if (_dragging)
        {
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var item = _dragItem;
        var dragging = _dragging;
        _dragItem = null;
        _dragging = false;
        Cursor = Cursor.Default;
        if (!dragging || item is null || Vm is not { } vm)
        {
            return;
        }

        if (IndexAt(e.GetPosition(ColumnList)) is { } index && vm.MoveTo(item, index))
        {
            ColumnList.SelectedItem = item;
        }
    }

    /// <summary>목록 안의 이 위치에 있는 항목의 순번(목록 밖이면 맨 앞·맨 뒤).</summary>
    private int? IndexAt(Point p)
    {
        var count = ColumnList.ItemCount;
        for (var i = 0; i < count; i++)
        {
            if (ColumnList.ContainerFromIndex(i) is { } c && c.Bounds.Height > 0)
            {
                var top = c.TranslatePoint(new Point(0, 0), ColumnList)?.Y ?? c.Bounds.Y;
                if (p.Y < top + c.Bounds.Height)
                {
                    return i;
                }
            }
        }

        return count > 0 ? count - 1 : null;
    }
}
