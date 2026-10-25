using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Oranda.App.Shell.Settings;

/// <summary>설정 창 「기능」 탭 본문(TSK-07-04 → TSK-09-08). 모양은 <c>FeatureSettingsView.axaml</c>, 상태는 <see cref="FeatureSettingsPage"/>.</summary>
public partial class FeatureSettingsView : UserControl
{
    public FeatureSettingsView()
    {
        InitializeComponent();
        // 「고급」 을 펼치면 펼친 내용이 스크롤 아래에 숨어 있지 않게 머리를 본문 맨 위로 올린다.
        AdvancedExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty && e.NewValue is true)
            {
                Dispatcher.UIThread.Post(ScrollAdvancedToTop, DispatcherPriority.Background);
            }
        };
    }

    private void ScrollAdvancedToTop()
    {
        if (AdvancedExpander.TranslatePoint(default, Body.Content as Visual ?? Body) is { } top)
        {
            Body.Offset = new Vector(Body.Offset.X, top.Y);
        }
    }
}
