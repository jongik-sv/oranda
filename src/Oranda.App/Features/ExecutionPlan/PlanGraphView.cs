using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 그래프 캔버스(TSK-06-04). <see cref="PlanGraphLayout"/> 의 노드·직각 간선을 그린다.
/// 노드 바탕 = 강조색(청록) 투명도 단계(색 진하기 = 시간 비중), 가장 비싼 단계 = 주황 채움, 위험 = 주황 테두리, 고른 노드 = 포커스 테두리.
/// 색은 모두 테마 브러시(XAML 에서 DynamicResource 로 준다). 휠 = 확대, 끌기 = 이동, 누르기 = 고르기(접힌 노드는 펼침).
/// </summary>
public sealed class PlanGraphView : Control
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 3;

    public static readonly StyledProperty<PlanGraphLayout?> GraphProperty =
        AvaloniaProperty.Register<PlanGraphView, PlanGraphLayout?>(nameof(Graph));

    public static readonly StyledProperty<int?> SelectedStepIdProperty =
        AvaloniaProperty.Register<PlanGraphView, int?>(nameof(SelectedStepId));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PlanGraphView, double>(nameof(Zoom), 1, coerce: (_, v) => Math.Clamp(v, MinZoom, MaxZoom));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> NodeBackgroundProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(NodeBackground));

    public static readonly StyledProperty<IBrush?> NodeBorderProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(NodeBorder));

    public static readonly StyledProperty<IBrush?> EdgeBrushProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(EdgeBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> HotBrushProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(HotBrush));

    public static readonly StyledProperty<IBrush?> HotForegroundProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(HotForeground));

    public static readonly StyledProperty<IBrush?> FocusBrushProperty =
        AvaloniaProperty.Register<PlanGraphView, IBrush?>(nameof(FocusBrush));

    private Vector _offset;
    private Point? _dragStart;
    private Vector _dragOffset;
    private bool _dragged;

    static PlanGraphView()
    {
        AffectsRender<PlanGraphView>(GraphProperty, SelectedStepIdProperty, ZoomProperty, BackgroundProperty, NodeBackgroundProperty, NodeBorderProperty,
            EdgeBrushProperty, AccentBrushProperty, HotBrushProperty, HotForegroundProperty, FocusBrushProperty);
        FocusableProperty.OverrideDefaultValue<PlanGraphView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PlanGraphView>(true);
    }

    /// <summary>노드를 눌렀다(값 = 단계 Id).</summary>
    public event EventHandler<int>? NodeClicked;

    public PlanGraphLayout? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public int? SelectedStepId
    {
        get => GetValue(SelectedStepIdProperty);
        set => SetValue(SelectedStepIdProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? NodeBackground
    {
        get => GetValue(NodeBackgroundProperty);
        set => SetValue(NodeBackgroundProperty, value);
    }

    public IBrush? NodeBorder
    {
        get => GetValue(NodeBorderProperty);
        set => SetValue(NodeBorderProperty, value);
    }

    public IBrush? EdgeBrush
    {
        get => GetValue(EdgeBrushProperty);
        set => SetValue(EdgeBrushProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? HotBrush
    {
        get => GetValue(HotBrushProperty);
        set => SetValue(HotBrushProperty, value);
    }

    public IBrush? HotForeground
    {
        get => GetValue(HotForegroundProperty);
        set => SetValue(HotForegroundProperty, value);
    }

    public IBrush? FocusBrush
    {
        get => GetValue(FocusBrushProperty);
        set => SetValue(FocusBrushProperty, value);
    }

    /// <summary>지금 이동량(논리 좌표 × 확대 뒤 화면 DIP).</summary>
    public Vector Offset => _offset;

    /// <summary>그래프 전체가 보이게 확대를 맞추고 가운데에 둔다(1 배를 넘기지 않는다).</summary>
    public void Fit()
    {
        if (Graph is not { Width: > 0 } g || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            Zoom = 1;
            _offset = default;
            InvalidateVisual();
            return;
        }

        var z = Math.Clamp(Math.Min(Bounds.Width / g.Width, Bounds.Height / g.Height), MinZoom, 1);
        Zoom = z;
        _offset = new Vector(Math.Max(0, (Bounds.Width - g.Width * z) / 2), Math.Max(0, (Bounds.Height - g.Height * z) / 2));
        InvalidateVisual();
    }

    /// <summary>가운데를 기준으로 <paramref name="factor"/> 배 확대·축소.</summary>
    public void ZoomBy(double factor) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    /// <summary>화면 점을 그래프 좌표로.</summary>
    public Point ToGraph(Point p) => new((p.X - _offset.X) / Zoom, (p.Y - _offset.Y) / Zoom);

    /// <summary>노드 하나가 보이게 옮긴다(트리에서 고른 단계).</summary>
    public void BringIntoView(int stepId)
    {
        if (Graph is null || Bounds.Width <= 0)
        {
            return;
        }

        foreach (var n in Graph.Nodes)
        {
            if (n.StepId != stepId)
            {
                continue;
            }

            var left = n.X * Zoom + _offset.X;
            var top = n.Y * Zoom + _offset.Y;
            var right = left + n.Width * Zoom;
            var bottom = top + n.Height * Zoom;
            var dx = left < 0 ? -left + 12 : right > Bounds.Width ? Bounds.Width - right - 12 : 0;
            var dy = top < 0 ? -top + 12 : bottom > Bounds.Height ? Bounds.Height - bottom - 12 : 0;
            if (dx != 0 || dy != 0)
            {
                _offset += new Vector(dx, dy);
                InvalidateVisual();
            }

            return;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty && change.OldValue is null or PlanGraphLayout { Nodes.Count: 0 })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Fit, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _dragStart = e.GetPosition(this);
        _dragOffset = _offset;
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start)
        {
            return;
        }

        var d = e.GetPosition(this) - start;
        if (!_dragged && Math.Abs(d.X) + Math.Abs(d.Y) < 4)
        {
            return;
        }

        _dragged = true;
        _offset = _dragOffset + d;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var wasDrag = _dragged;
        _dragStart = null;
        _dragged = false;
        Cursor = null;
        e.Pointer.Capture(null);
        if (!wasDrag)
        {
            ClickAt(e.GetPosition(this));
        }
    }

    /// <summary>화면 점의 노드를 누른 것으로 처리(시험도 쓴다). 노드가 있으면 true.</summary>
    public bool ClickAt(Point screen)
    {
        var p = ToGraph(screen);
        if (Graph?.HitTest(p.X, p.Y) is not { } node)
        {
            return false;
        }

        NodeClicked?.Invoke(this, node.StepId);
        return true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } bg)
        {
            context.FillRectangle(bg, bounds);
        }

        if (Graph is not { Nodes.Count: > 0 } g)
        {
            return;
        }

        var fg = TextElement.GetForeground(this) ?? Brushes.Gray;
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var size = Math.Max(9, TextElement.GetFontSize(this) - 1);
        var edge = new Pen(EdgeBrush ?? Brushes.Gray, 1.3);
        var border = new Pen(NodeBorder ?? Brushes.Gray, 1);
        var accentPen = new Pen(AccentBrush ?? Brushes.Teal, 1);
        var hotPen = new Pen(HotBrush ?? Brushes.Orange, 2);
        var focusPen = new Pen(FocusBrush ?? Brushes.DodgerBlue, 2);

        using (context.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            foreach (var e in g.Edges)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(e.Points[0].X, e.Points[0].Y), false);
                    for (var i = 1; i < e.Points.Count; i++)
                    {
                        ctx.LineTo(new Point(e.Points[i].X, e.Points[i].Y));
                    }

                    ctx.EndFigure(false);
                }

                context.DrawGeometry(null, edge, geo);
            }

            foreach (var n in g.Nodes)
            {
                var rect = new Rect(n.X, n.Y, n.Width, n.Height);
                var text = fg;
                if (n.IsHottest)
                {
                    context.DrawRectangle(HotBrush, hotPen, rect, 6, 6);
                    text = HotForeground ?? fg;
                }
                else
                {
                    context.DrawRectangle(NodeBackground, null, rect, 6, 6);
                    if (n.Intensity > 0.005 && AccentBrush is { } accent)
                    {
                        using (context.PushOpacity(0.10 + 0.32 * n.Intensity))
                        {
                            context.DrawRectangle(accent, null, rect, 6, 6);
                        }
                    }

                    var pen = n.IsRisky ? hotPen : n.Intensity > 0.005 ? accentPen : border;
                    context.DrawRectangle(null, pen, rect, 6, 6);
                }

                if (SelectedStepId == n.StepId)
                {
                    context.DrawRectangle(null, focusPen, rect.Inflate(4), 8, 8);
                }

                DrawCentered(context, n.Title, typeface, size + 0.5, text, n.CenterX, n.Y + 4, n.Width - 8, bold: n.IsHottest);
                DrawCentered(context, n.Detail, typeface, size - 1, text, n.CenterX, n.Y + 19, n.Width - 8, bold: false, dim: !n.IsHottest);
            }
        }
    }

    private static void DrawCentered(DrawingContext context, string text, Typeface typeface, double size, IBrush brush, double centerX, double top, double maxWidth, bool bold, bool dim = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var face = bold ? new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold) : typeface;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
        using (context.PushOpacity(dim ? 0.7 : 1))
        {
            context.DrawText(ft, new Point(centerX - ft.MaxTextWidth / 2, top));
        }
    }

    private void ZoomAt(Point screen, double factor)
    {
        var before = ToGraph(screen);
        Zoom *= factor;
        _offset = new Vector(screen.X - before.X * Zoom, screen.Y - before.Y * Zoom);
        InvalidateVisual();
    }
}
