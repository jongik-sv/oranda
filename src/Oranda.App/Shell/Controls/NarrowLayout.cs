using Avalonia;
using Avalonia.Controls;

namespace Oranda.App.Shell.Controls;

/// <summary>
/// 곁 패널을 좁게 붙였을 때(왼쪽·오른쪽 도구 창) 표 대신 카드 목록으로 바꾸는 도우미.
/// 붙이는 법: 화면 뿌리 컨트롤에 <c>ctl:NarrowLayout.Threshold="560"</c>(보통 <see cref="DefaultThreshold"/>).
/// 폭이 기준보다 작으면 그 컨트롤에 클래스 <c>narrow</c> 가 붙고 상속 속성 <see cref="IsNarrowProperty"/> 가 true 가 된다.
/// 화면은 스타일(<c>vm|MyView.narrow ListBox#List</c> → ItemTemplate 교체, 넓은 머리글 숨김)이나
/// 바인딩(<c>$self.(ctl:NarrowLayout.IsNarrow)</c>)으로 두 배치를 고른다. 도킹 위치는 읽지 않고 폭만 본다.
/// 폭이 바뀌면 바로 바뀐다. 두 배치는 같은 ViewModel·같은 목록 컨트롤을 쓰므로 선택·명령이 그대로 남는다.
/// </summary>
public static class NarrowLayout
{
    /// <summary>실행 이력·변경 일지가 쓰는 기준 폭(DIP).</summary>
    public const double DefaultThreshold = 560;

    /// <summary>이 폭(DIP)보다 좁으면 좁은 배치. 0 이하 = 끔.</summary>
    public static readonly AttachedProperty<double> ThresholdProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Threshold", typeof(NarrowLayout));

    /// <summary>좁은 배치인지(자손에게 상속된다). 화면이 직접 쓰지 않는다.</summary>
    public static readonly AttachedProperty<bool> IsNarrowProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsNarrow", typeof(NarrowLayout), inherits: true);

    /// <summary>좁은 배치일 때 뿌리 컨트롤에 붙는 클래스.</summary>
    public const string NarrowClass = "narrow";

    static NarrowLayout()
    {
        ThresholdProperty.Changed.AddClassHandler<Control>((host, e) =>
        {
            host.SizeChanged -= OnSizeChanged;
            if (e.NewValue is double threshold && threshold > 0)
            {
                host.SizeChanged += OnSizeChanged;
            }

            Update(host);
        });
    }

    public static double GetThreshold(Control control) => control.GetValue(ThresholdProperty);

    public static void SetThreshold(Control control, double value) => control.SetValue(ThresholdProperty, value);

    public static bool GetIsNarrow(Control control) => control.GetValue(IsNarrowProperty);

    public static void SetIsNarrow(Control control, bool value) => control.SetValue(IsNarrowProperty, value);

    /// <summary>폭이 기준보다 작은지. 아직 배치 전(폭 0)이면 넓은 배치로 둔다.</summary>
    public static bool IsNarrowWidth(double width, double threshold) => threshold > 0 && width > 0 && width < threshold;

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control host)
        {
            Update(host);
        }
    }

    private static void Update(Control host)
    {
        var narrow = IsNarrowWidth(host.Bounds.Width, host.GetValue(ThresholdProperty));
        if (host.GetValue(IsNarrowProperty) != narrow)
        {
            host.SetValue(IsNarrowProperty, narrow);
        }

        host.Classes.Set(NarrowClass, narrow);
    }
}
