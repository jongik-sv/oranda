using CommunityToolkit.Mvvm.ComponentModel;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 얇은 진행 막대(실행 중 표시)의 보임 상태. 켜져도 <see cref="Delay"/> 가 지나서야 보이게 해 짧은 쿼리의 깜빡임을 막고,
/// 꺼지면 바로 숨긴다. 지연 중에 꺼지면 보이지 않는다.
/// </summary>
public sealed class DelayedBusyFlag : ObservableObject
{
    /// <summary>새로 만드는 막대의 기본 지연(시험은 0 으로 둘 수 있다).</summary>
    public static TimeSpan DefaultDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    private bool _visible;
    private bool _wanted;
    private int _generation;

    public TimeSpan Delay { get; set; } = DefaultDelay;

    public bool Visible
    {
        get => _visible;
        private set => SetProperty(ref _visible, value);
    }

    public void Set(bool busy)
    {
        if (busy == _wanted)
        {
            return;
        }

        _wanted = busy;
        var generation = ++_generation;
        if (!busy)
        {
            Visible = false;
        }
        else if (Delay <= TimeSpan.Zero)
        {
            Visible = true;
        }
        else
        {
            _ = ShowLaterAsync(generation);
        }
    }

    private async Task ShowLaterAsync(int generation)
    {
        await Task.Delay(Delay).ConfigureAwait(true);
        if (generation == _generation && _wanted)
        {
            Visible = true;
        }
    }
}
