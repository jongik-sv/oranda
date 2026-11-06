using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>실행 단계 표의 한 줄: 테이블마다 진행률, 끝난 뒤에는 원본·대상 건수 비교.</summary>
public sealed partial class TableRunRowViewModel(string table) : ViewModelBase
{
    [ObservableProperty]
    private long _rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(HasEstimate))]
    private long? _estimated;

    [ObservableProperty]
    private string _phase = "대기";

    [ObservableProperty]
    private long? _sourceRows;

    [ObservableProperty]
    private long? _targetRows;

    [ObservableProperty]
    private CopyOutcome? _outcome;

    [ObservableProperty]
    private string _message = string.Empty;

    public string Table { get; } = table;

    public bool HasEstimate => Estimated is > 0;

    /// <summary>0~100. 예상 건수가 없으면 0.</summary>
    public double Percent => Estimated is > 0 ? Math.Min(100d, Rows * 100d / Estimated.Value) : 0d;

    /// <summary>끝난 뒤 원본·대상 건수가 같은지. 아직 비교 전이면 null.</summary>
    public bool? CountsMatch => SourceRows is { } s && TargetRows is { } t ? s == t : null;

    public string CountsText => SourceRows is { } s && TargetRows is { } t
        ? string.Create(CultureInfo.InvariantCulture, $"{s:N0} → {t:N0}")
        : string.Empty;

    partial void OnRowsChanged(long value) => OnPropertyChanged(nameof(Percent));

    partial void OnSourceRowsChanged(long? value) => NotifyCounts();

    partial void OnTargetRowsChanged(long? value) => NotifyCounts();

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(CountsMatch));
        OnPropertyChanged(nameof(CountsText));
    }
}

/// <summary>실패 행 목록의 한 줄(키 열 값과 오류 메시지만).</summary>
public sealed class FailedRowViewModel(FailedRow row)
{
    public FailedRow Row { get; } = row;

    public string Table => Row.Table;

    public string KeyText => string.Join(", ", Row.Key.Select(k => $"{k.Key}={k.Value ?? "NULL"}"));

    public string Error => Row.Error;
}

/// <summary>
/// 실행·검증 단계(3단계): 표마다 진행률, 일시 정지·계속·중지, 끝나면 건수 비교와 실패 행, 「실패 행만 다시 넣기」, 「CSV로 저장」.
/// 중지는 실행기에 넘긴 취소 토큰으로 한다. CSV 는 <see cref="SaveCsvAsync"/> 를 불렀을 때만 쓴다.
/// </summary>
public sealed partial class RunViewModel(DataCopyServices services) : ViewModelBase
{
    private CancellationTokenSource? _cts;
    private CopyRunControl? _control;
    private CopySessions? _sessions;
    private CopyJob? _job;
    private PrecheckReport? _report;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume), nameof(CanStop), nameof(CanRetry))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume))]
    private bool _isPaused;

    [ObservableProperty]
    private CopyOutcome? _outcome;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _readStartedAt;

    public ObservableCollection<TableRunRowViewModel> Rows { get; } = [];

    public ObservableCollection<FailedRowViewModel> FailedRows { get; } = [];

    public ObservableCollection<string> Notices { get; } = [];

    public bool CanPause => IsRunning && !IsPaused && _control is not null;

    public bool CanResume => IsRunning && IsPaused;

    public bool CanStop => IsRunning;

    public bool HasFailedRows => FailedRows.Count > 0;

    internal void NotifyFailedRowsChanged()
    {
        OnPropertyChanged(nameof(HasFailedRows));
        OnPropertyChanged(nameof(CanRetry));
    }

    /// <summary>끝난 실행에 실패 행이 있고, 세션·점검 결과가 남아 있을 때만.</summary>
    public bool CanRetry => !IsRunning && FailedRows.Count > 0 && _sessions is not null;

    /// <summary>화면에 보이는 「읽기를 시작한 시각」 글자. 같은 시점 기준으로 읽었을 때 그 시점이다(SCN 은 보이지 않는다).</summary>
    public string ReadStartedText => ReadStartedAt is { } at ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>복사를 실행한다. 끝나면(또는 중지되면) 결과를 화면에 채우고 작업의 마지막 실행 정보를 저장한다.</summary>
    public async Task StartAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions, ResolvedPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(decisions);
        if (services.Runner is not { } runner)
        {
            StatusText = DataCopyText.RunnerMissing;
            return;
        }

        if (IsRunning)
        {
            return; // 이미 도는 중이면 겹쳐 시작하지 않는다
        }

        _sessions = sessions;
        _job = job;
        _report = report;
        Rows.Clear();
        FailedRows.Clear();
        Notices.Clear();
        Outcome = null;
        ReadStartedAt = null;
        foreach (var plan in report.Order.Where(p => p.Rule.Kind != RuleKind.Exclude))
        {
            Rows.Add(new TableRunRowViewModel(plan.Rule.Table)
            {
                Estimated = report.EstimatedRows.TryGetValue(plan.Rule.Table, out var n) ? n : null,
            });
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var control = new CopyRunControl();
        _cts = cts;
        _control = control;
        IsPaused = false;
        IsRunning = true;
        StatusText = "복사하는 중…";
        CopyRunResult? result = null;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = running.Task;
        try
        {
            result = await runner.RunAsync(sessions, job, report, decisions, period, new UiProgress<CopyProgress>(OnProgress), control, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Outcome = CopyOutcome.Stopped;
            StatusText = DataCopyText.RunStopped;
        }
        catch (Exception e)
        {
            Outcome = CopyOutcome.Failed;
            StatusText = "복사를 마치지 못했습니다: " + e.Message;
        }
        finally
        {
            control.Resume();
            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _control = null;
            }

            IsPaused = false;
            IsRunning = false;
        }

        try
        {
            if (result is not null)
            {
                Apply(result);
                await SaveLastRunAsync(job, result, ct).ConfigureAwait(true);
            }

            OnPropertyChanged(nameof(CanRetry));
        }
        finally
        {
            running.TrySetResult();
        }
    }

    /// <summary>지금 도는(또는 마지막) 실행이 끝나면 완료되는 작업. 창을 닫을 때 세션을 닫기 전에 기다린다.</summary>
    public Task? Completion { get; private set; }

    /// <summary>작업 파일에 마지막 실행 정보를 저장할지(읽기 전용 작업 파일은 저장하지 않는다).</summary>
    public bool SaveLastRun { get; set; } = true;

    /// <summary>방금 끝난 실행의 요약. 열린 작업에 합쳐 다음 저장이 옛 값으로 덮어쓰지 않게 한다.</summary>
    public LastRunInfo? LastRun { get; private set; }

    private void OnProgress(CopyProgress p)
    {
        var row = Rows.FirstOrDefault(r => r.Table == p.Table);
        if (row is null)
        {
            return;
        }

        row.Rows = p.Rows;
        row.Estimated = p.Estimated ?? row.Estimated;
        row.Phase = p.Phase;
    }

    // 실행기 결과로 표를 맞춘다. 진행 보고가 늦게 도착해도 최종 값은 결과가 정한다.
    private void Apply(CopyRunResult result)
    {
        foreach (var table in result.Tables)
        {
            var row = Rows.FirstOrDefault(r => r.Table == table.Table);
            if (row is null)
            {
                row = new TableRunRowViewModel(table.Table);
                Rows.Add(row);
            }

            row.Estimated ??= table.SourceRows; // 끝난 표는 진행 막대가 가득 차게
            row.SourceRows = table.SourceRows;
            row.TargetRows = table.TargetRows;
            row.Outcome = table.Outcome;
            row.Message = table.Message ?? string.Empty;
            row.Phase = table.Outcome == CopyOutcome.Succeeded ? "완료" : "문제 있음";
            row.Rows = table.SourceRows;
        }

        FailedRows.Clear();
        foreach (var failed in result.FailedRows)
        {
            FailedRows.Add(new FailedRowViewModel(failed));
        }

        Notices.Clear();
        foreach (var notice in result.Notices)
        {
            Notices.Add(notice);
        }

        ReadStartedAt = result.ReadStartedAt;
        OnPropertyChanged(nameof(ReadStartedText));
        Outcome = result.Outcome;
        StatusText = result.Outcome switch
        {
            CopyOutcome.Succeeded => "복사를 마쳤습니다.",
            CopyOutcome.PartiallyFailed => "복사를 마쳤지만 실패한 행이 있습니다.",
            CopyOutcome.Stopped => DataCopyText.RunStopped,
            _ => "복사에 실패했습니다.",
        };
    }

    private async Task SaveLastRunAsync(CopyJob job, CopyRunResult result, CancellationToken ct)
    {
        var rows = result.Tables.Sum(t => t.TargetRows);
        LastRun = new LastRunInfo(services.Time.GetUtcNow(), result.Outcome, rows);
        if (!SaveLastRun || services.Store is not { } store)
        {
            return;
        }

        try
        {
            await store.SaveAsync(job with { LastRun = LastRun }, ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notices.Add("마지막 실행 정보를 저장하지 못했습니다: " + e.Message);
        }
    }

    /// <summary>다른 작업을 열 때: 이전 실행 결과(표·실패 행·상태)를 비운다. 도는 중이면 호출하지 않는다.</summary>
    public void Reset()
    {
        if (IsRunning)
        {
            return;
        }

        Rows.Clear();
        FailedRows.Clear();
        Notices.Clear();
        Outcome = null;
        ReadStartedAt = null;
        StatusText = string.Empty;
        LastRun = null;
        _sessions = null;
        _job = null;
        _report = null;
        OnPropertyChanged(nameof(ReadStartedText));
        OnPropertyChanged(nameof(CanRetry));
    }

    public void Pause()
    {
        if (CanPause && _control is { } control)
        {
            control.Pause();
            IsPaused = true;
            StatusText = "일시 정지했습니다. 지금 배치가 끝난 뒤에 멈춥니다.";
        }
    }

    public void Resume()
    {
        if (CanResume && _control is { } control)
        {
            control.Resume();
            IsPaused = false;
            StatusText = "복사하는 중…";
        }
    }

    /// <summary>중지: 현재 배치를 되돌리고 끈 트리거·FK 를 다시 켠 뒤 끝낸다(실행기가 한다). 일시 정지 중이면 먼저 풀어 취소가 닿게 한다.</summary>
    public void Stop()
    {
        if (_cts is { } cts)
        {
            StatusText = "중지하는 중…";
            cts.Cancel();
            _control?.Resume();
        }
    }

    /// <summary>실패 행의 키로 원본을 다시 읽어 넣는다(설계 8.2). 성공한 행은 목록에서 빠지고 결과는 표에 합쳐진다.</summary>
    public async Task RetryFailedAsync(CancellationToken ct = default)
    {
        if (!CanRetry || _sessions is null || _job is null || _report is null)
        {
            return;
        }

        if (services.Runner is not { } runner)
        {
            StatusText = DataCopyText.RunnerMissing;
            return;
        }

        var rows = FailedRows.Select(f => f.Row).ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        _control = null; // 다시 넣기에는 일시 정지가 없다(중지만)
        IsRunning = true;
        StatusText = "실패 행을 다시 넣는 중…";
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = running.Task;
        try
        {
            var result = await runner.RetryFailedRowsAsync(_sessions, _job, _report, rows, new UiProgress<CopyProgress>(OnProgress), cts.Token).ConfigureAwait(true);
            FailedRows.Clear();
            foreach (var failed in result.FailedRows)
            {
                FailedRows.Add(new FailedRowViewModel(failed));
            }

            foreach (var table in result.Tables)
            {
                if (Rows.FirstOrDefault(r => r.Table == table.Table) is { } row)
                {
                    var sent = rows.Count(r => r.Table == table.Table);
                    var remaining = result.FailedRows.Count(f => f.Table == table.Table);
                    row.TargetRows = AddRetriedRows(row.TargetRows, table.TargetRows, sent, remaining);
                    row.Message = table.Message ?? string.Empty;

                    // 실패 행이 모두 들어갔으면 표 줄도 성공으로 바꾼다(남았으면 그대로). 표 전체가 실패한 줄은 건드리지 않는다.
                    if (row.Outcome == CopyOutcome.PartiallyFailed && !result.FailedRows.Any(f => f.Table == table.Table))
                    {
                        row.Outcome = CopyOutcome.Succeeded;
                        row.Phase = "완료";
                    }
                }
            }

            foreach (var notice in result.Notices)
            {
                Notices.Add(notice);
            }

            Outcome = result.FailedRows.Count == 0 ? CopyOutcome.Succeeded : CopyOutcome.PartiallyFailed;
            StatusText = result.FailedRows.Count == 0 ? "실패했던 행을 모두 넣었습니다." : $"실패한 행이 {result.FailedRows.Count:N0}개 남았습니다.";
        }
        catch (OperationCanceledException)
        {
            StatusText = DataCopyText.RunStopped;
        }
        catch (Exception e)
        {
            StatusText = "실패 행을 다시 넣지 못했습니다: " + e.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            IsRunning = false;
            OnPropertyChanged(nameof(CanRetry));
            running.TrySetResult();
        }
    }

    /// <summary>
    /// 다시 넣기 결과의 <c>TargetRows</c> 는 기간 조건 없이 키로 읽어 이번에 넣은 행 수다(실행기가 대상 건수를 다시 세지 않는다).
    /// 그래서 앞선 대상 건수에 더해 원본·대상 건수 비교를 이어 간다. 앞선 건수를 모르면(없거나 음수) 그대로 둔다.
    /// 실행기는 커밋하지 못해 되돌린 표의 넣은 수도 담고 그 표의 입력 행을 모두 실패 행으로 돌려주므로,
    /// 「넣은 수 + 남은 실패 수 = 보낸 수」가 맞을 때만(확정된 표) 더한다.
    /// </summary>
    internal static long? AddRetriedRows(long? before, long retried, int sent, int remaining) =>
        before is >= 0 && retried >= 0 && retried + remaining == sent ? before.Value + retried : before;

    /// <summary>
    /// 「CSV로 저장」: 사용자가 누를 때만 호출한다. <paramref name="open"/> 이 저장 위치를 물어 스트림을 열어 주고, 취소하면 null 을 돌려준다.
    /// 실행이 끝났을 때 자동으로 쓰는 경로는 없다.
    /// </summary>
    public async Task SaveCsvAsync(Func<Task<Stream?>> open, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (FailedRows.Count == 0)
        {
            return;
        }

        var stream = await open().ConfigureAwait(true);
        if (stream is null)
        {
            return;
        }

        await using (stream.ConfigureAwait(true))
        {
            await FailedRowsCsv.WriteAsync(stream, FailedRows.Select(f => f.Row).ToList(), ct).ConfigureAwait(true);
        }
    }
}
