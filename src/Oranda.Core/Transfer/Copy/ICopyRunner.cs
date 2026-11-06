namespace Oranda.Core.Transfer.Copy;

/// <summary>복사 실행기. 구현: Oranda.Oracle(<c>Transfer/Copy</c>). 중지는 <c>ct</c> 취소로 한다.</summary>
public interface ICopyRunner
{
    Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions,
                                 ResolvedPeriod period, IProgress<CopyProgress>? progress, CopyRunControl control,
                                 CancellationToken ct);

    Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report,
                                             IReadOnlyList<FailedRow> rows, IProgress<CopyProgress>? progress,
                                             CancellationToken ct);
}

public sealed record CopyDecisions(IReadOnlySet<string> DisableTriggerTables, IReadOnlySet<string> DisableForeignKeys,
                                   IReadOnlySet<string> PreConfirmedSql, bool ProceedSequenceGap);

public sealed record CopyProgress(string Table, long Rows, long? Estimated, string Phase);

public sealed record CopyRunResult(CopyOutcome Outcome, IReadOnlyList<TableRunResult> Tables,
                                   IReadOnlyList<FailedRow> FailedRows, IReadOnlyList<string> Notices,
                                   DateTimeOffset ReadStartedAt);

/// <summary>일시 정지·재개 신호. 중지는 취소 토큰으로 한다.</summary>
public sealed class CopyRunControl
{
    private readonly object _gate = new();
    private TaskCompletionSource? _paused;

    public bool IsPaused
    {
        get { lock (_gate) { return _paused is not null; } }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? toRelease;
        lock (_gate)
        {
            toRelease = _paused;
            _paused = null;
        }
        toRelease?.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task? wait;
        lock (_gate) { wait = _paused?.Task; }
        return wait is null ? Task.CompletedTask : wait.WaitAsync(ct);
    }
}
