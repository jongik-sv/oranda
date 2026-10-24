using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oranda.App.Features.LockMonitor;

/// <summary>
/// 이력 띠의 작은 그래프(시안 d): 대기자 수 막대(<c>OrandaIconBlueBrush</c> 0.7), 최대 대기 초 선(<c>OrandaIconOrangeBrush</c>),
/// 알림 기준 점선(<c>OrandaDangerForegroundBrush</c>). 가로 = 마지막 점에서 <see cref="WindowMinutes"/> 분 전까지.
/// 브러시는 테마 자원에서 찾아 라이트·다크가 따라온다.
/// </summary>
public sealed class LockHistoryChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<LockHistoryPoint>?> PointsProperty =
        AvaloniaProperty.Register<LockHistoryChart, IReadOnlyList<LockHistoryPoint>?>(nameof(Points));

    public static readonly StyledProperty<int> WindowMinutesProperty =
        AvaloniaProperty.Register<LockHistoryChart, int>(nameof(WindowMinutes), 30);

    public static readonly StyledProperty<long> ThresholdSecondsProperty =
        AvaloniaProperty.Register<LockHistoryChart, long>(nameof(ThresholdSeconds));

    static LockHistoryChart()
    {
        AffectsRender<LockHistoryChart>(PointsProperty, WindowMinutesProperty, ThresholdSecondsProperty);
    }

    public LockHistoryChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<LockHistoryPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public int WindowMinutes
    {
        get => GetValue(WindowMinutesProperty);
        set => SetValue(WindowMinutesProperty, value);
    }

    /// <summary>알림 기준 초(0 이면 선을 긋지 않는다).</summary>
    public long ThresholdSeconds
    {
        get => GetValue(ThresholdSecondsProperty);
        set => SetValue(ThresholdSecondsProperty, value);
    }

    /// <summary>점 하나의 x(0~1)와 막대·선 높이 비율(0~1). 시험·그리기 공용.</summary>
    public static IReadOnlyList<(double X, double Bar, double Line)> Layout(IReadOnlyList<LockHistoryPoint> points, TimeSpan window, long threshold)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || window <= TimeSpan.Zero)
        {
            return [];
        }

        var end = points[^1].At;
        var start = end - window;
        var maxWaiters = Math.Max(1, points.Max(p => p.Waiters));
        var maxWait = Math.Max(1d, Math.Max(points.Max(p => p.MaxWaitSeconds), threshold * 1.15));
        return [.. points.Where(p => p.At >= start).Select(p => (
            (p.At - start).TotalMilliseconds / window.TotalMilliseconds,
            p.Waiters / (double)maxWaiters,
            p.MaxWaitSeconds / maxWait))];
    }

    /// <summary>기준선 높이 비율(0~1). 그릴 점이 없으면 null.</summary>
    public static double? ThresholdRatio(IReadOnlyList<LockHistoryPoint> points, long threshold)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || threshold <= 0)
        {
            return null;
        }

        var maxWait = Math.Max(1d, Math.Max(points.Max(p => p.MaxWaitSeconds), threshold * 1.15));
        return threshold / maxWait;
    }


    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 4 || bounds.Height < 4)
        {
            return;
        }

        var baseLine = Brush("OrandaSeparatorBrush");
        if (baseLine is not null)
        {
            context.DrawLine(new Pen(baseLine, 1), new Point(0, bounds.Height - 0.5), new Point(bounds.Width, bounds.Height - 0.5));
        }

        var points = Points ?? [];
        var window = TimeSpan.FromMinutes(Math.Max(1, WindowMinutes));
        var layout = Layout(points, window, ThresholdSeconds);
        if (layout.Count == 0)
        {
            return;
        }

        const double top = 3;
        var height = bounds.Height - top - 1;

        // 막대 폭: 점 사이 간격(가운데값)만큼, 최소 1.5 DIP.
        var gaps = new List<double>();
        for (var i = 1; i < layout.Count; i++)
        {
            gaps.Add(layout[i].X - layout[i - 1].X);
        }

        gaps.Sort();
        var step = gaps.Count == 0 ? 5d / window.TotalSeconds : gaps[gaps.Count / 2];
        var barWidth = Math.Clamp(step * bounds.Width * 0.8, 1.5, 12);

        if (Brush("OrandaIconBlueBrush") is { } blue)
        {
            using (context.PushOpacity(0.7))
            {
                foreach (var (x, bar, _) in layout)
                {
                    if (bar <= 0)
                    {
                        continue;
                    }

                    var h = Math.Max(1, bar * height);
                    var left = Math.Clamp(x * bounds.Width - barWidth / 2, 0, bounds.Width - barWidth);
                    context.FillRectangle(blue, new Rect(left, bounds.Height - 1 - h, barWidth, h));
                }
            }
        }

        if (ThresholdRatio(points, ThresholdSeconds) is { } ratio && Brush("OrandaDangerForegroundBrush") is { } danger)
        {
            var y = Math.Round(bounds.Height - 1 - ratio * height) + 0.5;
            context.DrawLine(new Pen(danger, 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(0, y), new Point(bounds.Width, y));
        }

        if (Brush("OrandaIconOrangeBrush") is { } orange && layout.Count > 0)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (var i = 0; i < layout.Count; i++)
                {
                    var p = new Point(Math.Clamp(layout[i].X * bounds.Width, 0, bounds.Width), bounds.Height - 1 - layout[i].Line * height);
                    if (i == 0)
                    {
                        g.BeginFigure(p, false);
                    }
                    else
                    {
                        g.LineTo(p);
                    }
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, new Pen(orange, 1.5) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
