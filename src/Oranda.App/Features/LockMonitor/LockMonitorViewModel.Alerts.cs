using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Language.Errors;
using Oranda.Core.History;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LockMonitor;

public sealed partial class LockMonitorViewModel
{
    // ---- 이력·임계치 알림(LK-10·11) -------------------------------------------------------------

    /// <summary>새로 고침 한 번 = 이력 한 점(대기자 수·최대 대기 초). 켜 두었으면 lock-history CSV 에도 한 줄.</summary>
    private void RecordHistory(DateTimeOffset at, int waiters, long maxWait)
    {
        if (_history is not { } history)
        {
            return;
        }

        var point = new LockHistoryPoint(at, waiters, maxWait);
        history.Add(point);
        if (SaveHistoryFile && HistoryFolder is { } folder && _session is { } session)
        {
            var account = Access.AccountText;
            _ = Task.Run(() =>
            {
                try
                {
                    LockHistoryFiles.AppendHistory(folder, account.Length > 0 ? account : session.Profile.Name, point);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PostToUi(() => StatusText = "이력 파일을 쓰지 못했습니다: " + ex.Message);
                }
            });
        }

        UpdateHistory();
    }

    private void UpdateHistory()
    {
        var points = _history?.Points ?? [];
        HistoryPoints = points;
        OnPropertyChanged(nameof(HistorySparse));
        HistoryLabel = string.Create(CultureInfo.InvariantCulture, $"최근 {HistoryMinutes} 분");
        var last = points.Count > 0 ? points[^1] : null;
        HistoryWaitersText = string.Create(CultureInfo.InvariantCulture, $"{last?.Waiters ?? 0:N0} 명");
        HistoryPeakText = _history?.Peak is { Waiters: > 0 } peak
            ? string.Create(CultureInfo.InvariantCulture, $"(최고 {peak.Waiters:N0} · {peak.At.ToLocalTime():HH:mm})")
            : string.Empty;
        HistoryMaxWaitText = LockText.Seconds(last?.MaxWaitSeconds ?? 0);
        HistoryOverThreshold = AlertEnabled && last is { } l && l.MaxWaitSeconds >= AlertWaitSeconds;
        OnPropertyChanged(nameof(ChartThreshold));
        HistoryThresholdText = AlertEnabled
            ? string.Create(CultureInfo.InvariantCulture, $"· 기준 {AlertWaitSeconds:N0} 초 ┄")
            : "· 알림 꺼짐";
        UpdateStatusRight();
    }

    /// <summary>임계치 판정: 처음 넘은 보유자가 있으면 알림을 걸어 두고(읽기가 끝난 뒤 스냅샷), 상태 줄 칩에 알린다.</summary>
    private void EvaluateThreshold(DateTimeOffset at)
    {
        if (!AlertEnabled)
        {
            ClearThreshold();
            return;
        }

        var result = _tracker.Evaluate(LockThresholdTracker.Loads(_allTree), AlertRule, at);
        if (result.NewAlert is { } alert)
        {
            _pendingAlert = alert;
        }

        if (result.IsExceeded || _thresholdPublished)
        {
            _thresholdPublished = result.IsExceeded;
            PublishThreshold?.Invoke(new LockThresholdChanged(result.IsExceeded, result.Waiters, result.MaxWaitSeconds));
        }
    }

    private void ClearThreshold()
    {
        _tracker.Reset();
        _pendingAlert = null;
        if (_thresholdPublished)
        {
            _thresholdPublished = false;
            PublishThreshold?.Invoke(new LockThresholdChanged(false, 0, 0));
        }
    }

    /// <summary>알림: 켜 두었으면 그 시점 보유자·대기자 SQL 스냅샷을 쓰고, 창 알림을 띄운다(8 초 뒤 사라짐).</summary>
    private async Task RaiseAlertAsync(LockAlert alert, CancellationToken ct)
    {
        if (SaveSnapshot && HistoryFolder is { } folder)
        {
            var path = await WriteSnapshotAsync(alert, folder, ct);
            if (path is not null)
            {
                alert = alert with { SnapshotPath = path };
            }
        }

        PostToUi(() =>
        {
            ActiveAlert = alert;
            AlertRaised?.Invoke(this, alert);
            StartAlertTimer();
        });
    }

    private async Task<string?> WriteSnapshotAsync(LockAlert alert, string folder, CancellationToken ct)
    {
        var holder = _allTree.FirstOrDefault(t => t.Key == alert.Holder);
        if (holder is null)
        {
            return null;
        }

        var entries = new List<LockSnapshotText.Entry>();
        string? path = null;
        await RunAsync(async (monitor, session) =>
        {
            var holderSql = holder.CurrentOrPreviousSqlId;
            entries.Add(new("보유자", holder, holderSql, await SqlTextAsync(monitor, session, holderSql, ct)));
            var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var w in Descendants(holder).OrderByDescending(w => w.WaitSeconds).Take(20))
            {
                var id = w.Node.SqlId;
                if (id is { Length: > 0 } && !texts.ContainsKey(id))
                {
                    texts[id] = await SqlTextAsync(monitor, session, id, ct);
                }

                entries.Add(new("대기자", w, id, id is null ? null : texts.GetValueOrDefault(id)));
            }

            var text = LockSnapshotText.Build(alert, Access.AccountText, AlertRule, entries);
            path = await Task.Run(() =>
            {
                try
                {
                    return LockHistoryFiles.WriteSnapshot(folder, alert, text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PostToUi(() => StatusText = "스냅샷을 쓰지 못했습니다: " + ex.Message);
                    return null;
                }
            }, ct);
        }, ct, reportAsBanner: false);
        return path;
    }

    private void StartAlertTimer()
    {
        Interlocked.Exchange(ref _alertTimer, null)?.Cancel();
        if (AlertDuration <= TimeSpan.Zero || AlertDuration == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _alertTimer = cts;
        var shown = ActiveAlert;
        _ = Task.Delay(AlertDuration, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    PostToUi(() =>
                    {
                        if (ReferenceEquals(ActiveAlert, shown))
                        {
                            ActiveAlert = null;
                        }
                    });
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>창 알림 닫기(✕). 상태 줄 칩은 남는다.</summary>
    public void DismissAlert()
    {
        Interlocked.Exchange(ref _alertTimer, null)?.Cancel();
        ActiveAlert = null;
    }

    /// <summary>「대기 트리 보기」: 탭을 앞으로, 대기 트리에서 알린 보유자를 고른다.</summary>
    public async Task ShowAlertHolderAsync()
    {
        var alert = ActiveAlert;
        DismissAlert();
        if (BringToFront is { } bring)
        {
            await bring();
        }

        TopTabIndex = TreeTab;
        if (alert is not null && _allTree.Any(t => t.Key == alert.Holder))
        {
            SelectedKey = alert.Holder;
        }
    }

    /// <summary>「트리에서 보기」(순환 띠): 대기 트리 탭에서 순환의 첫 세션을 고른다.</summary>
    public void ShowCycleInTree()
    {
        TopTabIndex = TreeTab;
        if (Cycles.Count > 0 && Cycles[0].Count > 0)
        {
            var first = Cycles[0][0];
            SelectedKey = _allTree.FirstOrDefault(t => t.Key.InstanceId == first.InstanceId && t.Key.Sid == first.Sid)?.Key ?? first;
        }
    }

    /// <summary>설정 파일 값을 화면에 넣는다(저장하지 않는다). 범위를 맞춘다.</summary>
    public void ApplySettings(LockAlertSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _applyingSettings = true;
        try
        {
            AlertEnabled = settings.Enabled;
            AlertWaitSeconds = settings.WaitSeconds;
            AlertWaiters = settings.Waiters;
            SaveSnapshot = settings.SaveSnapshot;
            NotifyWhenHidden = settings.NotifyWhenHidden;
            HistoryMinutes = settings.HistoryMinutes;
            SaveHistoryFile = settings.SaveHistoryFile;
        }
        finally
        {
            _applyingSettings = false;
        }

        UpdateHistory();
    }

    partial void OnAlertEnabledChanged(bool value)
    {
        if (!value)
        {
            ClearThreshold();
        }

        AlertSettingChanged();
    }

    partial void OnAlertWaitSecondsChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 86_400);
        if (clamped != value)
        {
            AlertWaitSeconds = clamped;
            return;
        }

        AlertSettingChanged();
    }

    partial void OnAlertWaitersChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 1000);
        if (clamped != value)
        {
            AlertWaiters = clamped;
            return;
        }

        AlertSettingChanged();
    }

    partial void OnSaveSnapshotChanged(bool value) => AlertSettingChanged();

    partial void OnNotifyWhenHiddenChanged(bool value) => AlertSettingChanged();

    partial void OnSaveHistoryFileChanged(bool value) => AlertSettingChanged();

    partial void OnHistoryMinutesChanged(int value)
    {
        var clamped = Math.Clamp(value, 5, 720);
        if (clamped != value)
        {
            HistoryMinutes = clamped;
            return;
        }

        if (_history is { } h)
        {
            h.Retention = TimeSpan.FromMinutes(clamped);
        }

        AlertSettingChanged();
    }

    private void AlertSettingChanged()
    {
        if (_applyingSettings)
        {
            return;
        }

        UpdateHistory();
        SaveSettings?.Invoke(CurrentAlertSettings);
    }
}
