using Avalonia.Controls;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>세션 모니터 「튜닝 추천」 탭. 단추는 <see cref="SessionTuningHintsViewModel"/> 의 동작을 부른다.</summary>
public partial class SessionTuningHintsView : UserControl
{
    public SessionTuningHintsView()
    {
        InitializeComponent();
        CheckButton.Click += (_, _) => _ = (DataContext as SessionTuningHintsViewModel)?.CheckAsync();
        OpenButton.Click += (_, _) => _ = (DataContext as SessionTuningHintsViewModel)?.OpenInSqlMonitorAsync();
        HintList.DoubleTapped += (_, _) => _ = (DataContext as SessionTuningHintsViewModel)?.OpenInSqlMonitorAsync();
    }
}
