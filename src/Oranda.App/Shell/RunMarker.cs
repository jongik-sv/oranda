using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;

namespace Oranda.App.Shell;

/// <summary>
/// 탭 하나의 「실행 중」 표지(TSK-14-13): 실행을 시작한 SQL·MyBatis·PL/SQL 탭 머리와 결과 영역 띠가 이것을 따른다. 끝·오류·취소 모두에서 꺼진다.
/// 겹친 실행은 수를 센다(마지막 실행이 끝날 때 꺼진다). 경과 초는 실행 중인 표지만 1초 타이머 하나로 갱신한다(탭이 많아도 도는 표지만 센다).
/// UI 스레드에서 쓴다.
/// </summary>
public sealed class RunMarker : ObservableObject
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<RunMarker> Running = [];
    private static DispatcherTimer? s_timer;

    private int _runs;
    private DateTime _startedUtc;
    private int _seconds;

    /// <summary>실행 중이다.</summary>
    public bool IsRunning => _runs > 0;

    /// <summary>시작한 뒤 지난 초(실행 중이 아니면 0).</summary>
    public int ElapsedSeconds => _seconds;

    /// <summary>시작한 시각(UTC). 실행 중이 아니면 의미 없다.</summary>
    public DateTime StartedUtc => _startedUtc;

    /// <summary>툴팁·결과 띠 글: 「실행 중 · 경과 n초」. 실행 중이 아니면 빈 글.</summary>
    public string Tip => IsRunning ? Describe(_seconds) : string.Empty;

    /// <summary>「실행 중 · 경과 n초」.</summary>
    public static string Describe(int seconds) => UiText.Format("Execution.runMarker.tip", "실행 중 · 경과 {0}초", seconds);

    public void Begin()
    {
        if (_runs++ > 0)
        {
            return; // 겹친 실행: 처음 시작한 시각을 그대로 둔다
        }

        _startedUtc = DateTime.UtcNow;
        _seconds = 0;
        lock (Gate)
        {
            Running.Add(this);
        }

        StartTimer();
        Raise(running: true);
    }

    public void End()
    {
        if (_runs == 0 || --_runs > 0)
        {
            return;
        }

        _seconds = 0;
        lock (Gate)
        {
            Running.Remove(this);
        }

        StopTimerIfIdle();
        Raise(running: true);
    }

    /// <summary>경과 초를 <paramref name="nowUtc"/> 기준으로 맞춘다. 초가 바뀔 때만 알린다(타이머·시험이 부른다).</summary>
    public void Refresh(DateTime nowUtc)
    {
        if (!IsRunning)
        {
            return;
        }

        var seconds = (int)Math.Max(0, (nowUtc - _startedUtc).TotalSeconds);
        if (seconds != _seconds)
        {
            _seconds = seconds;
            Raise(running: false);
        }
    }

    private void Raise(bool running)
    {
        if (running)
        {
            OnPropertyChanged(nameof(IsRunning));
        }

        OnPropertyChanged(nameof(ElapsedSeconds));
        OnPropertyChanged(nameof(Tip));
    }

    private static void StartTimer()
    {
        if (s_timer is not null || Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return;
        }

        s_timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickAll());
        s_timer.Start();
    }

    private static void StopTimerIfIdle()
    {
        lock (Gate)
        {
            if (Running.Count > 0)
            {
                return;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            s_timer?.Stop();
            s_timer = null;
        }
    }

    private static void TickAll()
    {
        RunMarker[] running;
        lock (Gate)
        {
            running = [.. Running];
        }

        var now = DateTime.UtcNow;
        foreach (var marker in running)
        {
            marker.Refresh(now);
        }

        if (running.Length == 0)
        {
            StopTimerIfIdle();
        }
    }
}

/// <summary>
/// 안쪽 탭의 실행 중 표지를 모아 보이는 문서(SQL·PL/SQL 작업 공간). 문서 탭 머리(<see cref="Docking.PanelDocument.IsRunning"/>)가 따른다.
/// </summary>
public interface IRunningDocument : INotifyPropertyChanged
{
    /// <summary>안쪽 탭 하나라도 실행 중이다.</summary>
    bool HasRunningTab { get; }

    /// <summary>실행 중인 안쪽 탭마다 「이름 실행 중 · 경과 n초」 한 줄. 없으면 빈 글.</summary>
    string RunTip { get; }
}

/// <summary>작업 공간의 <see cref="IRunningDocument"/> 공용 도우미.</summary>
public static class RunningTabs
{
    /// <summary>실행 중인 탭마다 「이름 실행 중 · 경과 n초」 한 줄(먼저 시작한 순).</summary>
    public static string Describe(IEnumerable<(string Title, RunMarker Run)> tabs) =>
        string.Join('\n', tabs.Where(t => t.Run.IsRunning).OrderBy(t => t.Run.StartedUtc)
            .Select(t => UiText.Format("Execution.runMarker.tabTip", "{0} 실행 중 · 경과 {1}초", t.Title, t.Run.ElapsedSeconds)));
}
