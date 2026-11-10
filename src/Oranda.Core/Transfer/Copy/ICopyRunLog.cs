namespace Oranda.Core.Transfer.Copy;

/// <summary>복사 실행 기록. 구현: Oranda.Platform(<c>copy-runs/*.jsonl</c>).</summary>
public interface ICopyRunLog
{
    Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct);
    Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct);
    Task MarkRecoveredAsync(string runFile, CancellationToken ct);
}

public interface ICopyRunRecord : IAsyncDisposable
{
    Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct);
    Task RecordStatementAsync(string sql, CancellationToken ct);
    Task RecordTableAsync(TableRunResult result, CancellationToken ct);
    Task RecordFailedRowAsync(FailedRow row, CancellationToken ct);
    Task CompleteAsync(CopyOutcome outcome, CancellationToken ct);

    /// <summary>
    /// 실행 중 사건 한 줄(결정 70): 테이블 적재가 끝남(<c>tableEnd</c>)·멈춤(<c>wait</c>)·대기 한도 초과(<c>waitLimit</c>) 등을 일어난 때 바로 남긴다
    /// (표 결과 줄은 끝낼 때 한꺼번에 쓰므로, 실행 중 기록만 보고도 어디까지 왔는지 알게). 글에 행 값은 없다. 기본 구현 = 남기지 않음.
    /// </summary>
    Task RecordEventAsync(string kind, string? table, string text, CancellationToken ct) => Task.CompletedTask;
}

public sealed record CopyRunHeader(string JobId, string JobName, string SourceName, string TargetName,
                                   ResolvedPeriod Period, DateTimeOffset StartedAt);

public enum DisabledKind { Trigger, ForeignKey }

public sealed record DisabledObject(DisabledKind Kind, string Owner, string Table, string Name);

public sealed record UnfinishedRun(string RunFile, DateTimeOffset StartedAt, IReadOnlyList<DisabledObject> Disabled);

/// <summary>
/// 실패 행의 키 열 값만 담는다. 값 글자 형식: 숫자 = 고정 소수점 invariant 글자,
/// DATE = <c>yyyy-MM-ddTHH:mm:ss</c>, TIMESTAMP = <c>yyyy-MM-ddTHH:mm:ss.fffffff</c>(100ns 아래 자리가 있으면 9자리,
/// TZ 는 끝에 <c>zzz</c>),
/// RAW = 대문자 16진수, 문자 = 그대로. 변환은 T9 의 <c>FailedRowKeys</c> 가 맡는다.
/// </summary>
public sealed record FailedRow(string Table, IReadOnlyDictionary<string, string?> Key, string Error);

/// <summary>
/// 표 하나의 결과. <paramref name="TargetRows"/> 가 음수(-1)면 대상 건수를 모른다(중지·실행 중 예외·건수 세기 실패로 대상 건수 비교를 건너뜀).
/// 글로 보일 때는 <see cref="TargetRowsText"/> 를 쓴다(-1 을 그대로 보이지 않는다).
/// </summary>
public sealed record TableRunResult(string Table, long SourceRows, long TargetRows, TimeSpan Elapsed,
                                    CopyOutcome Outcome, string? Message)
{
    /// <summary>적재 시간 나눔(결정 74: 읽기·변환·쓰기·행/초). 적재를 시작하지 않은 표·「실패 행만 다시 넣기」는 null.</summary>
    public CopyTableTimes? Times { get; init; }

    /// <summary>대상 건수 글: 천 단위 구분, 모르면(<see cref="TargetRows"/> 가 음수) <c>-</c>.</summary>
    public string TargetRowsText => TargetRows < 0 ? "-" : TargetRows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 테이블 하나의 적재 시간 나눔(결정 74). <paramref name="Read"/> = 원본 행을 받기까지 기다린 시간(읽기·쓰기를 겹치면 읽기 작업이 실제로 원본 DB 를 기다린 시간),
/// <paramref name="Convert"/> = 마스킹·교차 복사 값 변환(바인드 값 준비), <paramref name="Write"/> = 대상에 배치를 보낸 시간(값 변환 제외),
/// <paramref name="RowsPerSecond"/> = 넣은 행 / 적재 구간 경과(<see cref="Load"/>, 없으면 테이블 경과 시간). 겹치면 읽기 + 쓰기가 적재 구간보다 클 수 있다.
/// 글은 <see cref="CopyLogText.Times"/>.
/// </summary>
public sealed record CopyTableTimes(TimeSpan Read, TimeSpan Convert, TimeSpan Write, double RowsPerSecond)
{
    /// <summary>적재 구간 경과(원본 질의 시작 → 마지막 배치 쓰기 끝, 원격 #155). 읽기·변환·쓰기·행/초의 기준이다. null = 모름(예전 값).</summary>
    public TimeSpan? Load { get; init; }

    /// <summary>
    /// 테이블 전체 경과: 적재 구간에 대상 정리(DELETE)·세이브포인트·읽기 시점 열기·테이블 커밋·건수 비교를 더한 것.
    /// 테이블 적재 끝 작업 로그 줄은 건수 비교 전이라 그것이 빠지고, 결과·화면 값은 건수 비교까지 든다. null = 모름.
    /// </summary>
    public TimeSpan? Total { get; init; }

    /// <summary>읽기 가운데 첫 행까지 기다린 시간(원본 질의 시작·커서 열기). null = 모름.</summary>
    public TimeSpan? FirstRow { get; init; }

    /// <summary>기타 = 테이블 전체 − 적재 구간(정리·세이브포인트·커밋·건수 비교). 둘 중 하나를 모르면 null.</summary>
    public TimeSpan? Other => Total is { } total && Load is { } load ? (total > load ? total - load : TimeSpan.Zero) : null;
}
