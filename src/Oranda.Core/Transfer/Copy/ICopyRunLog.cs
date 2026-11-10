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
    /// <summary>대상 건수 글: 천 단위 구분, 모르면(<see cref="TargetRows"/> 가 음수) <c>-</c>.</summary>
    public string TargetRowsText => TargetRows < 0 ? "-" : TargetRows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
