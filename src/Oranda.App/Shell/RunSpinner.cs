using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Oranda.App.Features.Localization;
using Oranda.App.Features.ResultGrid;

namespace Oranda.App.Shell;

/// <summary>
/// 「실행 중」 스피너(TSK-14-13): 열린 호(270°)가 계속 돈다. 실행한 SQL·MyBatis·PL/SQL 탭 머리, 그 탭이 든 작업 공간의 문서 탭 머리,
/// SQL 탭 목록 메뉴, 결과 영역 띠가 모두 이 컨트롤 하나를 쓴다(크기·색은 App.axaml 의 RunSpinner 스타일).
/// <para>
/// <see cref="IsActive"/> 가 켜지면 <see cref="DelayedBusyFlag.DefaultDelay"/>(150 ms, 결과 창 진행 막대와 같다) 뒤에 보이고 꺼지면 바로 숨는다 —
/// 짧은 실행에서 탭 폭이 늘었다 줄며 흔들리지 않게(<see cref="ShowAtOnce"/> 면 바로).
/// 회전은 컴포지션(렌더 스레드) 애니메이션이라 UI 스레드는 매 프레임 일하지 않고, 보이고 시각 트리에 붙어 있을 때만 돈다
/// (숨거나 떼어지면 애니메이션을 멈춘다). 그래서 탭이 많아도 실행 중인 스피너만 돈다.
/// </para>
/// </summary>
public sealed class RunSpinner : Control, ICustomHitTest
{
    /// <summary>실행 중이다(보일지·돌지는 지연·보임에 따라 이 컨트롤이 정한다).</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<RunSpinner, bool>(nameof(IsActive));

    /// <summary>호 색(스타일 = <c>OrandaIconOrangeBrush</c>, 「세션」 창 실행 중 점과 같은 주황).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<RunSpinner, IBrush?>(nameof(Foreground));

    /// <summary>호 두께(DIP).</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RunSpinner, double>(nameof(Thickness), 2);

    /// <summary>한 바퀴 시간.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    /// <summary>호가 차지하는 각도(나머지 90° 가 비어 도는 것이 보인다).</summary>
    public const double SweepDegrees = 270;

    private const string RotationAngle = nameof(CompositionVisual.RotationAngle);

    private readonly DelayedBusyFlag _shown = new();
    private CompositionVisual? _spinning;
    private StreamGeometry? _arc;
    private Size _arcSize;
    private double _arcThickness;

    static RunSpinner()
    {
        AffectsRender<RunSpinner>(ForegroundProperty, ThicknessProperty);
    }

    public RunSpinner()
    {
        SetCurrentValue(IsVisibleProperty, false);
        AutomationProperties.SetName(this, UiText.Get("Execution.runMarker.name", "실행 중"));
        _shown.PropertyChanged += (_, _) => SetCurrentValue(IsVisibleProperty, _shown.Visible);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>켜지면 지연 없이 바로 보인다(이미 지연을 거친 결과 띠, 열 때 만드는 탭 목록 메뉴).</summary>
    public bool ShowAtOnce { get; set; }

    /// <summary>지금 회전 애니메이션을 돌리고 있다(시험이 본다: 숨거나 떼어지면 false).</summary>
    public bool IsSpinning => _spinning is not null;

    /// <summary>얇은 호가 아니라 네모 전체에서 툴팁 「실행 중 · 경과 n초」 가 뜬다.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var thickness = Thickness;
        if (Foreground is not { } brush || size.Width <= thickness || size.Height <= thickness)
        {
            return;
        }

        if (_arc is null || _arcSize != size || _arcThickness != thickness)
        {
            _arc = Arc(size, thickness);
            _arcSize = size;
            _arcThickness = thickness;
        }

        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round), _arc);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            _shown.Delay = ShowAtOnce ? TimeSpan.Zero : DelayedBusyFlag.DefaultDelay;
            _shown.Set(IsActive);
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateSpin();
        }
        else if (change.Property == BoundsProperty && _spinning is { IsDisposed: false } visual)
        {
            visual.CenterPoint = Center();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSpin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopSpin();
    }

    private void UpdateSpin()
    {
        var visual = IsVisible && VisualRoot is not null ? ElementComposition.GetElementVisual(this) : null;
        if (visual is null)
        {
            StopSpin();
            return;
        }

        if (ReferenceEquals(visual, _spinning))
        {
            return;
        }

        StopSpin();
        var spin = visual.Compositor.CreateScalarKeyFrameAnimation();
        spin.Target = RotationAngle;
        spin.InsertKeyFrame(0f, 0f, new LinearEasing());
        spin.InsertKeyFrame(1f, (float)(Math.PI * 2), new LinearEasing());
        spin.Duration = Period;
        spin.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.CenterPoint = Center();
        visual.StartAnimation(RotationAngle, spin);
        _spinning = visual;
    }

    private void StopSpin()
    {
        if (_spinning is { IsDisposed: false } visual)
        {
            visual.StopAnimation(RotationAngle);
            visual.RotationAngle = 0;
        }

        _spinning = null;
    }

    private Vector3D Center() => new(Bounds.Width / 2, Bounds.Height / 2, 0);

    /// <summary>위(12시)에서 시계 방향으로 <see cref="SweepDegrees"/> 만큼의 열린 호.</summary>
    private static StreamGeometry Arc(Size size, double thickness)
    {
        var radius = (Math.Min(size.Width, size.Height) - thickness) / 2;
        var center = new Point(size.Width / 2, size.Height / 2);
        Point At(double degrees)
        {
            var rad = (degrees - 90) * Math.PI / 180;
            return new Point(center.X + (radius * Math.Cos(rad)), center.Y + (radius * Math.Sin(rad)));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(0), isFilled: false);
            ctx.ArcTo(At(SweepDegrees), new Size(radius, radius), 0, isLargeArc: SweepDegrees > 180, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        return geometry;
    }
}
