using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>
/// Markdown 표 칸을 대화 창 폭에 맞춰 놓는 판(자식 = 칸, 행 순서대로). 칸 폭은 먼저 내용 폭이고,
/// 합이 <see cref="FitWidth"/> 를 넘으면 줄바꿈되는 칸(글 칸)만 <see cref="MinWrapWidth"/> 까지 줄인다. 숫자 칸은 줄이지 않는다.
/// 그래도 넘치면 내용 폭 그대로 두고 감싼 스크롤 뷰가 가로로 스크롤한다. 남으면 <c>stretchFirst</c> 일 때 첫 칸이 받는다.
/// </summary>
public sealed class MarkdownTablePanel : Panel
{
    /// <summary>글 칸을 줄일 때의 가장 좁은 폭(칸 안쪽 여백 포함).</summary>
    public const double MinWrapWidth = 72;

    private readonly int _columns;
    private readonly bool[] _wraps;
    private readonly bool _stretchFirst;
    private double _fitWidth = double.PositiveInfinity;
    private double[] _widths = [];
    private double[] _heights = [];

    public MarkdownTablePanel(int columns, bool[] wraps, bool stretchFirst)
    {
        _columns = Math.Max(1, columns);
        _wraps = wraps;
        _stretchFirst = stretchFirst;
    }

    /// <summary>맞출 폭(감싼 스크롤 뷰의 보이는 폭). 무한이면 내용 폭 그대로.</summary>
    public double FitWidth
    {
        get => _fitWidth;
        set
        {
            if (Math.Abs(_fitWidth - value) > 0.5)
            {
                _fitWidth = value;
                InvalidateMeasure();
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = (Children.Count + _columns - 1) / _columns;
        var natural = new double[_columns];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(Size.Infinity);
            natural[i % _columns] = Math.Max(natural[i % _columns], Children[i].DesiredSize.Width);
        }

        _widths = (double[])natural.Clone();
        var target = Math.Min(FitWidth, availableSize.Width);
        var total = _widths.Sum();
        if (total > target)
        {
            var room = Enumerable.Range(0, _columns).Select(c => WrapsColumn(c) ? Math.Max(0, natural[c] - MinWrapWidth) : 0).ToArray();
            var shrinkable = room.Sum();
            if (shrinkable > 0)
            {
                var cut = Math.Min(total - target, shrinkable);
                for (var c = 0; c < _columns; c++)
                {
                    _widths[c] -= room[c] * cut / shrinkable;
                }
            }
        }
        else if (_stretchFirst && !double.IsInfinity(target))
        {
            _widths[0] += target - total;
        }

        _heights = new double[rows];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(_widths[i % _columns], double.PositiveInfinity));
            _heights[i / _columns] = Math.Max(_heights[i / _columns], Children[i].DesiredSize.Height);
        }

        return new Size(_widths.Sum(), _heights.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        for (var r = 0; r < _heights.Length; r++)
        {
            double x = 0;
            for (var c = 0; c < _columns && (r * _columns) + c < Children.Count; c++)
            {
                Children[(r * _columns) + c].Arrange(new Rect(x, y, _widths[c], _heights[r]));
                x += _widths[c];
            }

            y += _heights[r];
        }

        return finalSize;
    }

    /// <summary>마지막으로 잰 칸 폭(시험용).</summary>
    internal IReadOnlyList<double> ColumnWidths => _widths;

    /// <summary>줄바꿈해 줄일 수 있는 칸인가(글 칸).</summary>
    internal bool WrapsColumn(int column) => column < _wraps.Length && _wraps[column];
}

/// <summary>
/// 표를 감싸는 가로 스크롤 뷰. 자기가 받은 폭(부모가 주는 유한한 폭)에서 <c>inset</c>(표 테두리)을 뺀 값을 재기 전에 표 판의
/// <see cref="MarkdownTablePanel.FitWidth"/> 로 넘긴다(스크롤 뷰 안쪽은 폭이 무한이라 표 판이 직접 알 수 없다).
/// </summary>
internal sealed class MarkdownTableScroll(MarkdownTablePanel table, double inset) : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width))
        {
            table.FitWidth = Math.Max(0, availableSize.Width - inset);
        }

        return base.MeasureOverride(availableSize);
    }
}
