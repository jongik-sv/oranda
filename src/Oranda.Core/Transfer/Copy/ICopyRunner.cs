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

/// <summary>
/// 사전 점검에서 사용자가 고른 실행 결정. 트리거 끄기는 규칙 키, FK 끄기는 (FK 계정, FK 이름)이다:
/// FK 이름은 계정마다 따로라 이름만 담으면 다른 계정의 같은 이름 FK 까지 꺼진다.
/// </summary>
public sealed record CopyDecisions(IReadOnlySet<string> DisableTriggerTables, IReadOnlySet<ForeignKeyRef> DisableForeignKeys,
                                   IReadOnlySet<string> PreConfirmedSql, bool ProceedSequenceGap);

/// <summary>끌 FK 하나: 제약의 계정(자식 표 계정, <see cref="DisabledObject.Owner"/>)과 이름. 글자 그대로 비교한다(문장에 따옴표로 들어간다).</summary>
public sealed record ForeignKeyRef(string Owner, string Name)
{
    public static ForeignKeyRef Of(DisabledObject fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        return new ForeignKeyRef(fk.Owner, fk.Name);
    }
}

/// <summary>
/// 진행 보고. <see cref="Log"/> 가 있으면 작업 로그 한 줄이고 진행 값(<see cref="Rows"/> 등)은 뜻이 없다(받는 쪽은 진행 줄을 바꾸지 않는다).
/// </summary>
public sealed record CopyProgress(string Table, long Rows, long? Estimated, string Phase)
{
    /// <summary>작업 로그 한 줄(진행 보고면 null).</summary>
    public CopyLogEntry? Log { get; init; }

    public static CopyProgress ForLog(CopyLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new CopyProgress(entry.Table ?? string.Empty, 0, null, entry.Phase) { Log = entry };
    }
}

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
