using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Shell.Appearance;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridControl 의 일부: 포인터 입력(ResultGridControl.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridControl
{
    // ---- 입력 ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _model is not null)
        {
            GridFontSize += e.Delta.Y > 0 ? 1 : -1;
            _ = FontSizeSaver?.Invoke(_fontSize);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ScrollTo(_offsetX - (e.Delta.Y + e.Delta.X) * 60, _offsetY);
        }
        else
        {
            ScrollTo(_offsetX - e.Delta.X * 60, _offsetY - e.Delta.Y * RowHeight * 3);
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_model is null || !_model.HasResult)
        {
            return;
        }

        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (p.Y < HeaderHeight)
        {
            if (_model.IsTextMode)
            {
                return;
            }

            if (p.X < RowHeaderWidth)
            {
                _model.SelectAll(); // 행 머리 위 모서리: 전체 선택(Excel·SQL Developer 처럼)
                e.Handled = true;
                return;
            }

            var (col, edge) = HitColumn(p.X);
            if (col < 0)
            {
                return;
            }

            var ord = _model.VisibleColumns[col];
            if (props.IsLeftButtonPressed && IsOnFilterButton(col, edge))
            {
                // ▾: 자동 필터 팝업을 연다. 누른 열·끌기 상태를 기록하지 않으므로 놓아도 정렬하지 않고 끌어도 열이 옮겨지지 않는다.
                RequestColumnFilter(col);
                e.Handled = true;
                return;
            }

            if (edge <= ResizeGrip && props.IsLeftButtonPressed)
            {
                _resizeColumn = ord;
                _resizeStartX = p.X;
                _resizeStartWidth = _model.GetColumnWidth(ord);
                e.Pointer.Capture(this);
            }
            else if (props.IsLeftButtonPressed)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    _model.SelectColumn(col);
                }
                else
                {
                    // 누르는 순간에는 정렬하지 않는다. 끌지 않고 놓으면(OnPointerReleased) 그때 정렬한다.
                    _headerPressCol = col;
                    _headerPressPoint = p;
                    _headerDragging = false;
                    _dropIndex = -1;
                    e.Pointer.Capture(this);
                }
            }
            else if (props.IsRightButtonPressed && !_model.IsSelected(0, col))
            {
                _model.SelectColumn(col);
            }

            e.Handled = true;
            return;
        }

        var (row, c) = HitCell(p);
        if (_model.IsTextMode)
        {
            _model.SelectRow(row, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else if (p.X < RowHeaderWidth)
        {
            // 행 머리: 그 행 전체를 고르고, 끌면 행 단위로 넓힌다. 고른 행 안에서 오른쪽 클릭은 선택을 유지한다.
            if (!(props.IsRightButtonPressed && _model.IsSelected(row, 0)))
            {
                _model.SelectRow(row, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }

            _dragSelecting = _dragRows = props.IsLeftButtonPressed;
            if (_dragSelecting)
            {
                e.Pointer.Capture(this);
            }
        }
        else if (c >= 0)
        {
            var cell = new GridCell(row, c);
            if (props.IsRightButtonPressed && _model.IsSelected(row, c))
            {
                return; // 선택 안에서 오른쪽 클릭은 선택을 유지한다
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _model.ExtendTo(cell);
            }
            else
            {
                _model.SelectCell(cell);
            }

            _dragSelecting = props.IsLeftButtonPressed;
            if (_dragSelecting)
            {
                e.Pointer.Capture(this);
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_model is null)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (_resizeColumn >= 0)
        {
            _model.SetColumnWidth(_resizeColumn, _resizeStartWidth + p.X - _resizeStartX);
            return;
        }

        if (_headerPressCol >= 0)
        {
            if (!_headerDragging && _headerPressCol >= _model.FrozenCount && Math.Abs(p.X - _headerPressPoint.X) >= HeaderDragThreshold(e))
            {
                _headerDragging = true;
            }

            if (_headerDragging)
            {
                _dropIndex = DropIndexAt(p.X);
                InvalidateVisual();
            }

            return;
        }

        if (_dragSelecting)
        {
            // 가장자리에 닿으면 스크롤하면서 선택을 넓힌다.
            var dy = p.Y < HeaderHeight ? p.Y - HeaderHeight : p.Y > Bounds.Height ? p.Y - Bounds.Height : 0;
            var dx = p.X < 0 ? p.X : p.X > Bounds.Width ? p.X - Bounds.Width : 0;
            if (dx != 0 || dy != 0)
            {
                ScrollTo(_offsetX + dx, _offsetY + dy);
            }

            var (row, col) = HitCell(new Point(Math.Clamp(p.X, 0, Bounds.Width - 1), Math.Clamp(p.Y, HeaderHeight, Math.Max(HeaderHeight, Bounds.Height - 1))));
            if (_dragRows)
            {
                _model.SelectRow(row, extend: true);
                return;
            }

            if (col < 0)
            {
                col = p.X < RowHeaderWidth ? 0 : _model.VisibleColumns.Count - 1;
            }

            _model.ExtendTo(new GridCell(row, col));
            return;
        }

        var overHeader = p.Y < HeaderHeight && !_model.IsTextMode && p.X >= RowHeaderWidth;
        var (hitCol, hitEdge) = overHeader ? HitColumn(p.X) : (-1, double.MaxValue);
        SetHoverHeader(hitCol);
        Cursor = overHeader && hitEdge <= ResizeGrip
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.Arrow);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoverHeader(-1);
    }

    /// <summary>마우스가 올라간 머리글 열을 바꾸고, 바뀌었으면 ▾ 를 보이거나 감추려고 다시 그린다.</summary>
    private void SetHoverHeader(int visibleCol)
    {
        if (_hoverHeaderCol != visibleCol)
        {
            _hoverHeaderCol = visibleCol;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_headerPressCol >= 0 && _model is { } pressed)
        {
            var col = _headerPressCol;
            var dragged = _headerDragging;
            var drop = _dropIndex;
            CancelHeaderPress();
            if (dragged)
            {
                pressed.MoveColumn(col, drop); // 끌었으면 정렬하지 않고 열 순서만 바꾼다
            }
            else if (col < pressed.VisibleColumns.Count)
            {
                _ = SortAsync(pressed.VisibleColumns[col]);
            }
        }

        _dragSelecting = false;
        _dragRows = false;
        _resizeColumn = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelHeaderPress(); // 창을 옮기거나 포인터를 뺏기면 정렬도 이동도 하지 않는다
    }

    /// <summary>머리글 끌기 문턱(DIP). 플랫폼의 누르기 허용 거리, 없으면 4.</summary>
    internal static double HeaderDragThreshold(PointerEventArgs e)
    {
        var tap = Application.Current?.PlatformSettings?.GetTapSize(e.Pointer.Type).Width ?? 0;
        return tap > 0 ? tap : 4;
    }

    /// <summary>머리글 누르기·끌기 상태를 지운다(Esc 취소·놓기·포인터 잃음).</summary>
    private void CancelHeaderPress()
    {
        var was = _headerDragging;
        _headerPressCol = -1;
        _headerDragging = false;
        _dropIndex = -1;
        if (was)
        {
            InvalidateVisual();
        }
    }

    /// <summary>끄는 중인 머리글을 놓을 자리: 화면 x 가 가리키는 열 중간선 기준으로 그 열 앞(마지막 열의 오른쪽이면 열 수). 고정 열 안으로는 놓지 못한다.</summary>
    private int DropIndexAt(double x)
    {
        var m = _model!;
        double left = 0;
        for (var i = 0; i < m.VisibleColumns.Count; i++)
        {
            var w = m.GetColumnWidth(m.VisibleColumns[i]);
            if (i >= m.FrozenCount && x < RowHeaderWidth + left - _offsetX + w / 2)
            {
                return i;
            }

            left += w;
        }

        return m.VisibleColumns.Count;
    }

    /// <summary>열 위치 <paramref name="insertBefore"/> 앞 경계의 화면 x.</summary>
    private double DropLineX(int insertBefore)
    {
        var m = _model!;
        var x = ColumnLeft(insertBefore);
        return RowHeaderWidth + (insertBefore < m.FrozenCount ? x : x - _offsetX);
    }
}
