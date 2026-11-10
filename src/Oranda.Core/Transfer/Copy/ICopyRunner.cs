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

    /// <summary>
    /// 대상에 넣은 행 수(적재 단계). 읽기·쓰기를 겹치면 읽은 수(<see cref="Rows"/>)보다 늦게 따라온다. 모르면 null(받는 쪽은 앞 값을 둔다).
    /// </summary>
    public long? Written { get; init; }

    /// <summary>
    /// 멈춤 표시(결정 70): 지금 응답을 오래 기다리는 단계(원본 읽기·대상 쓰기 따로). null = 이 보고는 멈춤 표시와 상관없다(받는 쪽은 앞 표시를 둔다),
    /// 빈 목록 = 멈춤이 풀렸다(표시를 지운다).
    /// </summary>
    public IReadOnlyList<CopyWait>? Waits { get; init; }

    /// <summary>
    /// 동시 복사(PF2)의 단계 알림: 이 보고는 테이블 진행이 아니라 「동시 n개 · 단계 i/k」 상태 글이다(<see cref="Table"/> 은 빈 글자).
    /// 한 테이블씩 복사하면 보내지 않는다.
    /// </summary>
    public CopyStage? Stage { get; init; }

    public static CopyProgress ForLog(CopyLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new CopyProgress(entry.Table ?? string.Empty, 0, null, entry.Phase) { Log = entry };
    }
}

/// <summary>
/// 동시 복사(PF2)의 지금 단계: <paramref name="Lanes"/> = 이 단계에서 동시에 도는 테이블 수, <paramref name="Index"/> = 몇째 단계(1부터), <paramref name="Count"/> = 단계 수.
/// 글은 <see cref="CopyLogText.Stage"/>(「동시 2개 · 단계 1/3」).
/// </summary>
public sealed record CopyStage(int Lanes, int Index, int Count);

/// <summary>멈춤 표시의 쪽: 원본 읽기 또는 대상 쓰기.</summary>
public enum CopyWaitSide { Source, Target }

/// <summary>
/// 멈춤 표시가 기다리는 명령의 종류. <see cref="Load"/>(기본) = 적재(원본 읽기·배치 쓰기), <see cref="Delete"/> = 대상 정리(DELETE),
/// <see cref="Commit"/> = 「테이블마다 커밋」의 COMMIT. 둘 다 대상 명령이다(리뷰 PF1-4: 흔한 멈춤인 DELETE 행 잠금 대기도 감시한다).
/// </summary>
public enum CopyWaitStep { Load, Delete, Commit }

/// <summary>
/// 멈춤 표시 하나(결정 70): 명령 하나가 응답 없이 기다린 시간. <paramref name="Batch"/> 는 대상 쓰기의 배치 번호(원본 읽기면 null).
/// 글은 <see cref="CopyLogText.Wait"/>(「배치 n 쓰는 중 · m분째 대기」·「원본 읽는 중 · m분째」·「대상 정리(DELETE) 중 · m분째 대기」·「테이블 커밋 중 · m분째 대기」).
/// </summary>
public sealed record CopyWait(CopyWaitSide Side, int? Batch, TimeSpan Waited)
{
    /// <summary>몇 분째인지(1부터: 1분이 안 됐으면 1분째).</summary>
    public int Minute => (int)Waited.TotalMinutes + 1;

    /// <summary>기다리는 명령의 종류(기본 = 적재).</summary>
    public CopyWaitStep Step { get; init; }

    /// <summary>
    /// 대기 원인(WH1, <see cref="CopyWaitCause.Describe"/>): 30초 넘게 기다린 대상 명령을 감시 세션으로 본 글(「행 잠금(enq: TX) · 막는 세션 123 (APPUSER@was01)」).
    /// 감시하지 않거나 아직 모르면 null.
    /// </summary>
    public string? Cause { get; init; }
}

public sealed record CopyRunResult(CopyOutcome Outcome, IReadOnlyList<TableRunResult> Tables,
                                   IReadOnlyList<FailedRow> FailedRows, IReadOnlyList<string> Notices,
                                   DateTimeOffset ReadStartedAt)
{
    /// <summary>
    /// 원본 명령이 취소에도 돌아오지 않아 실행기가 원본 세션을 버렸다(리뷰 PF1-3). 그 세션은 명령을 쥐고 있어 더 쓸 수 없다:
    /// 앱은 세션 쌍을 닫고 새로 열어야 한다(다시 점검).
    /// </summary>
    public bool SourceAbandoned { get; init; }

    /// <summary>대상 명령이 취소에도 돌아오지 않아 실행기가 대상 세션을 버렸다(커밋·롤백을 보내지 않았다). 앱은 세션 쌍을 닫는다(닫으면 커밋하지 않은 변경은 되돌아간다).</summary>
    public bool TargetAbandoned { get; init; }

    /// <summary>버린 세션이 있다(<see cref="SourceAbandoned"/> 또는 <see cref="TargetAbandoned"/>).</summary>
    public bool SessionsAbandoned => SourceAbandoned || TargetAbandoned;
}

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
