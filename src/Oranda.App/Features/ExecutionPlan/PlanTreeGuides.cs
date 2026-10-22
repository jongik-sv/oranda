using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 트리 줄 앞의 들여쓰기 안내선(├ └ │). 칸 폭은 <see cref="Indent"/>, 선은 부모 아이콘 가운데(<see cref="Offset"/>)를 지난다.
/// 줄 높이 전체에 그려서 위아래 줄의 선이 끊기지 않는다.
/// </summary>
public sealed class PlanTreeGuides : Control
{
    public static readonly StyledProperty<PlanTreeLines?> LinesProperty =
        AvaloniaProperty.Register<PlanTreeGuides, PlanTreeLines?>(nameof(Lines));

    public static readonly StyledProperty<double> IndentProperty =
        AvaloniaProperty.Register<PlanTreeGuides, double>(nameof(Indent), 16);

    public static readonly StyledProperty<double> OffsetProperty =
        AvaloniaProperty.Register<PlanTreeGuides, double>(nameof(Offset), 7);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<PlanTreeGuides, IBrush?>(nameof(Stroke));

    static PlanTreeGuides()
    {
        AffectsMeasure<PlanTreeGuides>(LinesProperty, IndentProperty);
        AffectsRender<PlanTreeGuides>(LinesProperty, IndentProperty, OffsetProperty, StrokeProperty);
    }

    public PlanTreeLines? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public double Indent
    {
        get => GetValue(IndentProperty);
        set => SetValue(IndentProperty, value);
    }

    public double Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new((Lines?.Depth ?? 0) * Indent, 0);

    public override void Render(DrawingContext context)
    {
        if (Lines is not { } lines || Stroke is not { } stroke)
        {
            return;
        }

        var pen = new Pen(stroke, 1);
        var h = Bounds.Height;
        var mid = Math.Round(h / 2) + 0.5;
        double X(int column) => Math.Round((column * Indent) + Offset) + 0.5;

        for (var k = 0; k < lines.Continues.Count; k++)
        {
            if (lines.Continues[k])
            {
                context.DrawLine(pen, new Point(X(k), 0), new Point(X(k), h));
            }
        }

        if (lines.Depth > 0)
        {
            var x = X(lines.Depth - 1);
            context.DrawLine(pen, new Point(x, 0), new Point(x, lines.IsLast ? mid : h));
            context.DrawLine(pen, new Point(x, mid), new Point((lines.Depth * Indent) - 2, mid));
        }

        if (lines.HasChildren)
        {
            // 자기 아이콘(14 DIP) 아래에서 줄 끝까지: 아래 줄 자식의 ├ 와 이어진다.
            var x = X(lines.Depth);
            context.DrawLine(pen, new Point(x, mid + 7), new Point(x, h));
        }
    }
}
