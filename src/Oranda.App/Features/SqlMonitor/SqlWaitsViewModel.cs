using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>대기 분해 탭의 상태.</summary>
public enum WaitPaneState
{
    /// <summary>모니터 세션 없음.</summary>
    NoSession,

    /// <summary>목록에서 SQL 을 고르지 않음.</summary>
    NoSql,

    /// <summary>모을 준비(「표본 모으기」 단추 하나).</summary>
    Ready,

    /// <summary>표본 모으는 중(부분 결과가 채워진다).</summary>
    Sampling,

    /// <summary>ASH 읽는 중.</summary>
    Loading,

    /// <summary>끝(결과 있음).</summary>
    Done,

    /// <summary>사용자가 취소(모은 데까지 결과).</summary>
    Cancelled,

    /// <summary>표본 0(그 시간 동안 실행되지 않음).</summary>
    Empty,

    /// <summary>서버 오류.</summary>
    Error,

    /// <summary>ASH 를 골랐는데 Diagnostics Pack 선언이 없음.</summary>
    LicenseOff,
}

/// <summary>
/// 대기 분해(TN-12·14). 팩 없음: N 초 동안 1 초마다 gv$session 표본을 모아 sql_id 의 event·wait_class 비율을 센다(진행·취소).
/// Diagnostics 선언이 있으면 ASH 로 최근 N 분을 읽는다(선언이 없으면 서버로 보내지 않는다).
/// </summary>
public sealed partial class SqlWaitsViewModel : ObservableObject
{
    /// <summary>표본 시간 범위(초).</summary>
    public const int MinSeconds = 5;

    public const int MaxSeconds = 300;

    private readonly SqlMonitorViewModel _owner;
    private readonly List<WaitSample> _samples = [];
    private CancellationTokenSource? _cts;
    private bool _resetAfterCancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsBusy), nameof(IsSampling), nameof(HasResults), nameof(HasMessage), nameof(IsLicenseOff),
        nameof(CanStart), nameof(StartText), nameof(HeadlineText), nameof(ShowStart), nameof(IsLoading))]
    private WaitPaneState _state = WaitPaneState.NoSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ModeHintText))]
    private int _durationSeconds = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText), nameof(ModeHintText), nameof(HeadlineText))]
    private bool _useAsh;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHintText))]
    private int _ashMinutes = 60;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    /// <summary>표본을 모으는 sql_id(모으기 시작할 때 정해진다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadlineText))]
    private string? _targetSqlId;

    internal SqlWaitsViewModel(SqlMonitorViewModel owner)
    {
        _owner = owner;
        State = Ready();
    }

    /// <summary>SQL 모니터(바로 가기 단추·팩 상태).</summary>
    public SqlMonitorViewModel Owner => _owner;

    /// <summary>보기 고르기(0 = 지금 표본, 1 = ASH).</summary>
    public int ModeIndex
    {
        get => UseAsh ? 1 : 0;
        set => UseAsh = value == 1;
    }

    /// <summary>이벤트 표(많은 순).</summary>
    public ObservableCollection<WaitEventItem> Events { get; } = [];

    /// <summary>대기 분류 막대·범례(많은 순, 기타 끝).</summary>
    public ObservableCollection<WaitClassItem> Classes { get; } = [];

    /// <summary>표본 간격. 시험이 줄인다.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>표본 사이 기다리기. 시험이 바꾼다.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>지금 시각(ASH 구간 시작 계산). 시험이 바꾼다.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public bool IsReady => State == WaitPaneState.Ready;

    public bool IsSampling => State == WaitPaneState.Sampling;

    public bool IsBusy => State is WaitPaneState.Sampling or WaitPaneState.Loading;

    public bool HasResults => Events.Count > 0 && State is WaitPaneState.Sampling or WaitPaneState.Loading or WaitPaneState.Done or WaitPaneState.Cancelled;

    public bool HasMessage => State is WaitPaneState.NoSession or WaitPaneState.NoSql or WaitPaneState.Empty or WaitPaneState.Error;

    public bool IsLicenseOff => State == WaitPaneState.LicenseOff;

    public bool IsLoading => State == WaitPaneState.Loading;

    /// <summary>머리 줄의 모으기 단추(처음 화면에는 가운데 큰 단추가 따로 있다).</summary>
    public bool ShowStart => !IsBusy && State != WaitPaneState.LicenseOff;

    /// <summary>모으기·불러오기 단추를 누를 수 있는가.</summary>
    public bool CanStart => _owner.MonitorSession is not null && _owner.SelectedSqlId is not null && !IsBusy;

    public string StartText => UseAsh ? "ASH 불러오기" : State is WaitPaneState.Done or WaitPaneState.Cancelled or WaitPaneState.Empty or WaitPaneState.Error ? "다시 모으기" : "표본 모으기";

    /// <summary>「18 / 30」.</summary>
    public string ProgressText => string.Create(CultureInfo.InvariantCulture, $"{Done} / {Total}");

    /// <summary>모드 설명 한 줄(무엇을 읽는지).</summary>
    public string ModeHintText => UseAsh
        ? string.Create(CultureInfo.InvariantCulture, $"최근 {AshMinutes} 분 · gv$active_session_history · Diagnostics Pack")
        : string.Create(CultureInfo.InvariantCulture, $"{DurationSeconds} 초 · 1 초 간격 · gv$session · 팩 없음");

    /// <summary>결과 위 제목 줄.</summary>
    public string HeadlineText => TargetSqlId is null
        ? string.Empty
        : (UseAsh ? "ASH " : "표본 ") + TargetSqlId + State switch
        {
            WaitPaneState.Sampling => " · 모으는 중",
            WaitPaneState.Cancelled => " · 취소함(모은 데까지)",
            _ => string.Empty,
        };

    /// <summary>모은 표본 줄 수(시험·상태 줄).</summary>
    public int SampleRows => _samples.Count;

    /// <summary>ASH 를 쓸 수 있는가(Diagnostics 선언).</summary>
    public bool CanUseAsh => _owner.HasDiagnostics;

    /// <summary>ASH 단추 툴팁(꺼졌으면 이유).</summary>
    public string AshTip => _owner.DiagnosticsOff() is { } off
        ? "Diagnostics Pack 이 필요합니다. " + off.Reason
        : "gv$active_session_history 로 지난 구간의 대기를 읽습니다(Diagnostics Pack).";

    /// <summary>표본을 모은다(팩 없음). 이미 모으는 중이면 아무것도 안 한다.</summary>
    public async Task StartAsync()
    {
        if (UseAsh)
        {
            await LoadAshAsync();
            return;
        }

        if (!CanStart || _owner.SelectedSqlId is not { } sqlId)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        _samples.Clear();
        Apply(WaitBreakdown.FromSamples(_samples));
        TargetSqlId = sqlId;
        Total = Math.Clamp(DurationSeconds, MinSeconds, MaxSeconds);
        Done = 0;
        State = WaitPaneState.Sampling;
        _owner.StatusText = $"대기 분해: {sqlId} 표본 모으는 중 (0 / {Total} 초)";
        Exception? failure = null;
        try
        {
            for (var i = 0; i < Total; i++)
            {
                IReadOnlyList<WaitSample>? got = null;
                var ok = await _owner.RunTuningAsync(async (tuning, session) => got = await tuning.SampleWaitsAsync(session, sqlId, ct), ex => failure = ex, ct);
                if (!ok)
                {
                    break;
                }

                var sample = got ?? [];
                _owner.PostToUi(() =>
                {
                    _samples.AddRange(sample);
                    Apply(WaitBreakdown.FromSamples(_samples));
                    Done++;
                    _owner.StatusText = $"대기 분해: {sqlId} 표본 모으는 중 ({Done} / {Total} 초)";
                });
                if (i < Total - 1)
                {
                    await Delay(Interval, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _owner.PostToUi(() =>
            {
                if (_resetAfterCancel)
                {
                    // 다른 SQL 을 골라 멈췄다: 그 SQL 의 처음 화면으로.
                    _resetAfterCancel = false;
                    _samples.Clear();
                    Events.Clear();
                    Classes.Clear();
                    TargetSqlId = null;
                    State = Ready();
                    _owner.StatusText = "대기 분해: 다른 SQL 을 골라 표본 모으기를 멈췄습니다.";
                }
                else if (ReferenceEquals(_cts, cts))
                {
                    State = _samples.Count > 0 ? WaitPaneState.Cancelled : Ready();
                    _owner.StatusText = $"대기 분해: 취소했습니다 ({Done} / {Total} 초, 표본 {_samples.Count}건)";
                }
            });
            return;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }

        _owner.PostToUi(() => Finish(sqlId, failure));
    }

    /// <summary>모으기를 멈춘다(모은 데까지 결과는 남는다).</summary>
    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>ASH 로 최근 <see cref="AshMinutes"/> 분을 읽는다. Diagnostics 선언이 없으면 서버로 보내지 않고 이유를 보인다.</summary>
    public async Task LoadAshAsync()
    {
        if (_owner.SelectedSqlId is not { } sqlId || _owner.MonitorSession is null || IsBusy)
        {
            return;
        }

        if (!_owner.HasDiagnostics)
        {
            ShowLicenseOff();
            return;
        }

        TargetSqlId = sqlId;
        Events.Clear();
        Classes.Clear();
        State = WaitPaneState.Loading;
        var since = Now().AddMinutes(-Math.Clamp(AshMinutes, 1, 24 * 60));
        IReadOnlyList<AshWaitRow>? rows = null;
        Exception? failure = null;
        try
        {
            await _owner.RunTuningAsync(async (tuning, session) => rows = await tuning.GetAshWaitsAsync(session, sqlId, since, CancellationToken.None), ex => failure = ex, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _owner.PostToUi(() => State = Ready());
            return;
        }

        _owner.PostToUi(() =>
        {
            if (failure is not null)
            {
                Fail(failure);
                return;
            }

            Apply(WaitBreakdown.FromAsh(rows ?? []));
            if (Events.Count == 0)
            {
                State = WaitPaneState.Empty;
                Message = $"ASH 에 이 sql_id 의 표본이 최근 {AshMinutes} 분 동안 없습니다";
                HintText = "구간을 늘리거나 「지금 표본」 으로 실행 중인 대기를 모으세요.";
            }
            else
            {
                State = WaitPaneState.Done;
            }

            _owner.StatusText = $"대기 분해(ASH): {sqlId} 최근 {AshMinutes} 분 표본 {SumSamples():N0}건";
        });
    }

    /// <summary>세션이 바뀌면 처음으로.</summary>
    internal void Reset()
    {
        Cancel();
        _samples.Clear();
        Events.Clear();
        Classes.Clear();
        TargetSqlId = null;
        Done = 0;
        Total = 0;
        OnPropertyChanged(nameof(CanUseAsh));
        OnPropertyChanged(nameof(AshTip));
        OnPropertyChanged(nameof(CanStart));
        if (!CanUseAsh && UseAsh)
        {
            UseAsh = false;
        }

        State = Ready();
    }

    /// <summary>고른 SQL 이 바뀌었다. 다른 sql_id 를 고르면 모으기를 멈추고 처음으로(목록 새로 고침 중 잠깐 비는 것은 무시).</summary>
    internal void OnTargetChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        var id = _owner.SelectedSqlId;
        if (IsBusy)
        {
            if (id is not null && !string.Equals(id, TargetSqlId, StringComparison.Ordinal))
            {
                _resetAfterCancel = true;
                Cancel();
            }

            return;
        }

        if (id is not null && string.Equals(id, TargetSqlId, StringComparison.Ordinal))
        {
            return;
        }

        _samples.Clear();
        Events.Clear();
        Classes.Clear();
        TargetSqlId = null;
        State = Ready();
    }

    partial void OnUseAshChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeIndex));
        if (IsBusy)
        {
            return;
        }

        Events.Clear();
        Classes.Clear();
        TargetSqlId = null;
        if (value && !_owner.HasDiagnostics)
        {
            ShowLicenseOff();
            return;
        }

        State = Ready();
    }

    partial void OnDurationSecondsChanged(int value)
    {
        var clamped = Math.Clamp(value, MinSeconds, MaxSeconds);
        if (clamped != value)
        {
            DurationSeconds = clamped;
        }
    }

    private void ShowLicenseOff()
    {
        var off = _owner.DiagnosticsOff();
        Message = "ASH 과거 구간은 Diagnostics Pack 이 필요합니다";
        HintText = (off?.Reason ?? string.Empty) + " " + (off?.HowTo ?? string.Empty) + " 팩 없이 보려면 「지금 표본」 을 쓰세요.";
        State = WaitPaneState.LicenseOff;
        _owner.StatusText = "대기 분해(ASH): Diagnostics Pack 선언이 없어 서버로 보내지 않았습니다.";
    }

    private void Finish(string sqlId, Exception? failure)
    {
        if (failure is not null && _samples.Count == 0)
        {
            Fail(failure);
            return;
        }

        if (_samples.Count == 0)
        {
            State = WaitPaneState.Empty;
            Message = $"이 sql_id 가 {Total} 초 동안 실행되지 않았습니다";
            HintText = "실행 중일 때 다시 모으거나, 표본 시간을 늘리세요. 이미 끝난 실행은 ASH(Diagnostics Pack)로 볼 수 있습니다.";
            _owner.StatusText = $"대기 분해: {sqlId} 표본 0건 ({Done} / {Total} 초)";
            return;
        }

        State = failure is null ? WaitPaneState.Done : WaitPaneState.Cancelled;
        _owner.StatusText = failure is null
            ? $"대기 분해: {sqlId} 표본 {_samples.Count:N0}건 ({Done} / {Total} 초)"
            : $"대기 분해: 중간에 멈췄습니다 — {SqlMonitorViewModel.Describe(failure)}";
    }

    private void Fail(Exception ex)
    {
        Message = "대기를 읽지 못했습니다";
        HintText = SqlMonitorViewModel.Describe(ex);
        State = WaitPaneState.Error;
        _owner.StatusText = "대기 분해: " + HintText;
    }

    private WaitPaneState Ready()
    {
        if (_owner.MonitorSession is null)
        {
            Message = "모니터 세션이 열려 있지 않습니다";
            HintText = "SQL 모니터를 열면 이 탭에서 고른 SQL 의 대기를 모을 수 있습니다.";
            return WaitPaneState.NoSession;
        }

        if (_owner.SelectedSqlId is null)
        {
            Message = "SQL 을 고르세요";
            HintText = "위 목록에서 SQL 을 고르면 그 sql_id 를 실행 중인 세션의 대기 이벤트를 모읍니다.";
            return WaitPaneState.NoSql;
        }

        if (UseAsh && !_owner.HasDiagnostics)
        {
            ShowLicenseOff();
            return WaitPaneState.LicenseOff;
        }

        Message = string.Empty;
        HintText = string.Empty;
        return WaitPaneState.Ready;
    }

    private void Apply(IReadOnlyList<WaitEventShare> events)
    {
        var lockLink = _owner.CanOpenLockMonitor;
        Events.Clear();
        foreach (var e in events)
        {
            var item = new WaitEventItem(e);
            item.ShowLockLink = lockLink && item.IsLockWait;
            Events.Add(item);
        }

        Classes.Clear();
        foreach (var c in WaitBreakdown.Classes(events))
        {
            Classes.Add(new WaitClassItem(c));
        }

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(SampleRows));
    }

    private long SumSamples()
    {
        long sum = 0;
        foreach (var e in Events)
        {
            sum += e.Share.Samples;
        }

        return sum;
    }
}
