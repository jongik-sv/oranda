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

// ResultGridControl 의 일부: 배치(ResultGridControl.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridControl
{
    // ---- 배치 ----

    private double TotalColumnsWidth() => _model!.VisibleColumns.Sum(c => _model.GetColumnWidth(c));

    private double TextExtentWidth() => (_model!.GetTextHeader().Split(Environment.NewLine)[0].Length + 2) * Factory.CharWidth;

    /// <summary>스크롤하는 열이 시작하는 화면 x(행 머리 + 고정 열). 그 왼쪽은 스크롤해도 움직이지 않는다.</summary>
    private double ScrollLeft() => RowHeaderWidth + FrozenWidth();

    private double FrozenWidth()
    {
        if (_model is null || _model.IsTextMode)
        {
            return 0;
        }

        double w = 0;
        for (var i = 0; i < _model.FrozenCount && i < _model.VisibleColumns.Count; i++)
        {
            w += _model.GetColumnWidth(_model.VisibleColumns[i]);
        }

        return w;
    }

    /// <summary>
    /// 셀의 화면 사각형(이 컨트롤 좌표). 셀 편집기가 그 위에 입력 칸을 띄운다. 화면 밖이면 null.
    /// </summary>
    public Rect? CellBounds(GridCell cell)
    {
        if (_model is null || _model.IsTextMode || cell.Col < 0 || cell.Col >= _model.VisibleColumns.Count)
        {
            return null;
        }

        var left = RowHeaderWidth + ColumnLeft(cell.Col) - (cell.Col < _model.FrozenCount ? 0 : _offsetX);
        var top = HeaderHeight + (cell.Row * RowHeight) - _offsetY;
        var w = _model.GetColumnWidth(_model.VisibleColumns[cell.Col]);
        return top < HeaderHeight || top > Bounds.Height ? null : new Rect(left, top, w, RowHeight);
    }

    /// <summary>바깥 스크롤바 둘을 이 그리드의 스크롤 범위·위치에 맞춘다(결과 영역·모니터 표가 함께 쓴다). 범위가 없으면 숨긴다.</summary>
    public void SyncScrollBars(Avalonia.Controls.Primitives.ScrollBar vertical, Avalonia.Controls.Primitives.ScrollBar horizontal)
    {
        ArgumentNullException.ThrowIfNull(vertical);
        ArgumentNullException.ThrowIfNull(horizontal);
        vertical.Maximum = MaxOffsetY;
        vertical.ViewportSize = ViewportHeight;
        vertical.LargeChange = ViewportHeight;
        vertical.SmallChange = RowHeight;
        vertical.Value = OffsetY;
        vertical.IsVisible = MaxOffsetY > 0;
        horizontal.Maximum = MaxOffsetX;
        horizontal.ViewportSize = ViewportWidth;
        horizontal.LargeChange = ViewportWidth;
        horizontal.SmallChange = 40;
        horizontal.Value = OffsetX;
        horizontal.IsVisible = MaxOffsetX > 0;
    }

    /// <summary>셀 편집을 열어 달라는 요청(두 번 누르기·F2). 편집 모드인지는 받는 쪽이 판단한다.</summary>
    public event EventHandler<GridCell>? EditRequested;

    protected override void OnDoubleTapped(Avalonia.Input.TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (_model is { HasSelection: true } m && !m.IsTextMode && e.GetPosition(this).Y >= HeaderHeight)
        {
            EditRequested?.Invoke(this, m.Active);
        }
    }

    /// <summary>보이는 열 <paramref name="col"/> 의 왼쪽 x(스크롤 전 절대 좌표).</summary>
    private double ColumnLeft(int col)
    {
        double x = 0;
        for (var i = 0; i < col; i++)
        {
            x += _model!.GetColumnWidth(_model.VisibleColumns[i]);
        }

        return x;
    }

    /// <summary>
    /// 화면 x 좌표 → (보이는 열 위치, 열 오른쪽 경계까지의 거리). 행 머리·고정 열 영역은 스크롤하지 않는다.
    /// 스크롤 열이 고정 열 밑으로 반쯤 들어가 있으면 보이는 부분만 맞힌다. 행 머리 위면 -1.
    /// </summary>
    private (int Col, double RightEdgeDistance) HitColumn(double x)
    {
        if (_model is null)
        {
            return (-1, double.MaxValue);
        }

        var head = RowHeaderWidth;
        var frozen = ScrollLeft();
        double left = 0;
        for (var i = 0; i < _model.VisibleColumns.Count; i++)
        {
            var w = _model.GetColumnWidth(_model.VisibleColumns[i]);
            var inScrollRegion = i >= _model.FrozenCount;
            var screenLeft = head + (inScrollRegion ? left - _offsetX : left);
            var visibleLeft = inScrollRegion ? Math.Max(screenLeft, frozen) : screenLeft;
            if (x >= visibleLeft && x < screenLeft + w)
            {
                return (i, screenLeft + w - x);
            }

            left += w;
        }

        return (-1, double.MaxValue);
    }

    private (long Row, int Col) HitCell(Point p)
    {
        var col = _model!.IsTextMode ? 0 : HitColumn(p.X).Col;
        var row = (long)Math.Floor((p.Y - HeaderHeight + _offsetY) / RowHeight);
        return (Math.Clamp(row, 0, Math.Max(0, _model.RowCount - 1)), col);
    }
}
