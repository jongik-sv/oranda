using Avalonia.Controls;
using Avalonia.Threading;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>AI 띠 화면. 진행 중에는 1초마다 경과 시간을 다시 쓴다.</summary>
public partial class AiInlineStrip : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public AiInlineStrip()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (DataContext is AiInlineStripViewModel { IsRunning: true } model)
            {
                model.RefreshProgress();
            }
            else
            {
                _timer.Stop();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AiInlineStripViewModel model)
            {
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AiInlineStripViewModel.IsRunning) && model.IsRunning)
                    {
                        _timer.Start();
                    }
                };
            }
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }
}
