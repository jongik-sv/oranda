using System.Text.Json.Serialization;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 작업 파일 스키마 버전. 파일의 <c>schemaVersion</c> 이 이보다 크면 읽기 전용으로 연다.
/// 2 = 규칙마다 원본·대상 계정(묶음 B). 1 → 2 변환은 <see cref="CopyJobMigrationV1"/>.
/// </summary>
public static class CopyJobSchema
{
    public const int Current = 2;
}

/// <summary>접속 간 데이터 복사 작업 한 건. 비밀번호는 담지 않는다(설계 6.1).</summary>
public sealed record CopyJob
{
    public int SchemaVersion { get; init; } = CopyJobSchema.Current;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ConnectionRef Source { get; init; }
    public required ConnectionRef Target { get; init; }
    public required string SourceOwner { get; init; }
    public required string TargetOwner { get; init; }
    public CopyPeriod Period { get; init; } = new(PeriodKind.None);
    public IReadOnlyList<TableRule> Tables { get; init; } = [];
    public CopyOptions Options { get; init; } = new();
    public IReadOnlyList<SequenceRule> Sequences { get; init; } = [];

    /// <summary>설계 8.1 「같은 시점 기준으로 읽기」.</summary>
    public bool ConsistentRead { get; init; } = true;

    /// <summary>32바이트 난수 Base64. 작업을 만들 때 한 번 정한다.</summary>
    public required string MaskSalt { get; init; }

    public LastRunInfo? LastRun { get; init; }
}

public sealed record ConnectionRef(string Name, string User, Guid? IdHint = null);

public enum PeriodKind { None, LastWeek, LastMonth, Last3Months, PreviousMonth, Custom }

/// <summary>
/// 작업의 기간. <paramref name="EndInclusive"/> 는 직접 지정에서만 쓴다: false 면 끝 날짜 미만, true 면 끝 날짜 포함(그날 끝까지).
/// 다른 종류에서는 무시한다. <paramref name="DayStartHour"/> 는 전기일(업무 날짜)이 바뀌는 시각(0~23, 0 = 자정)이고
/// DATE·TIMESTAMP 기간 열과 <c>:FROM_DT</c>·<c>:TO_DT</c> 에만 쓴다. 둘 다 기본값이면 파일에 쓰지 않는다(예전 파일과 같다).
/// </summary>
public sealed record CopyPeriod(
    PeriodKind Kind,
    DateTime? From = null,
    DateTime? To = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool EndInclusive = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int DayStartHour = 0);

/// <summary>
/// 계산한 기간(날짜, 시작 포함·끝 제외). 문자열 날짜 열은 이 날짜를 그대로 쓰고,
/// DATE·TIMESTAMP 열과 <c>:FROM_DT</c>·<c>:TO_DT</c> 는 <see cref="DayStartHour"/> 시간만큼 민 시각(<see cref="TimeFrom"/>·<see cref="TimeTo"/>)을 쓴다.
/// </summary>
public sealed record ResolvedPeriod(DateTime? From, DateTime? To)
{
    /// <summary>전기일이 바뀌는 시각(0~23). 0 이면 자정.</summary>
    public int DayStartHour { get; init; }

    public DateTime? TimeFrom => From?.AddHours(DayStartHour);

    public DateTime? TimeTo => To?.AddHours(DayStartHour);
}

public enum RuleKind { All, Period, ParentKey, Custom, Exclude }

public enum TargetAction { DeleteAllInsert, DeleteRangeInsert, AppendOnly, MergeByPk, Recreate }

/// <summary><paramref name="StringFormat"/> 이 null 이면 DATE·TIMESTAMP 열이다.</summary>
public sealed record PeriodColumn(string Column, string? StringFormat = null);

public sealed record ParentLink(string ParentTable, IReadOnlyList<string> ChildColumns,
                                IReadOnlyList<string> ParentColumns, string? ForeignKeyName = null)
{
    /// <summary>부모 표의 원본 계정. null 이면 자식 표의 원본 계정이다(<see cref="CopyOwners.ParentSource"/>).</summary>
    public string? ParentOwner { get; init; }
}

public enum MaskKind { Fixed, KeepFirst, KeepLast, Hash, Null }

public sealed record MaskRule(string Column, MaskKind Kind, string? Value = null, int Count = 0);

public sealed record TableRule(string Table, RuleKind Kind, TargetAction Action)
{
    /// <summary>원본 표 계정. null 이면 작업의 기본 원본 계정(<see cref="CopyJob.SourceOwner"/>)이다(<see cref="CopyOwners.Source"/>).</summary>
    public string? SourceOwner { get; init; }

    /// <summary>
    /// 대상 표 계정. null 이면 원본 계정의 대상 기본값(<see cref="CopyOwners.MapTarget"/>: 기본 원본 계정의 표면 작업 기본 대상 계정, 아니면 원본 계정 그대로)이다
    /// (<see cref="CopyOwners.Target"/>). 대상 표 이름은 <see cref="Table"/> 과 같다.
    /// </summary>
    public string? TargetOwner { get; init; }

    /// <summary>
    /// 표시용: 사용자가 시노님으로 적어 더했을 때 적은 이름(예 <c>ORDERS</c>, <c>APP.ORDERS_SYN</c>). 실행은 이 값을 쓰지 않고 풀린 계정·표(<see cref="SourceOwner"/>·<see cref="Table"/>)만 쓴다.
    /// 사전 점검은 이 이름이 지금 다른 표를 가리키면 확인 항목으로 알리기만 한다(자동으로 바꾸지 않는다).
    /// </summary>
    public string? WrittenAs { get; init; }

    /// <summary>0~2개. 2개면 OR 로 묶는다.</summary>
    public IReadOnlyList<PeriodColumn> PeriodColumns { get; init; } = [];
    public ParentLink? Parent { get; init; }
    public string? CustomWhere { get; init; }
    /// <summary>기간 규칙에만 쓰는 추가 조건(<c>WHERE</c> 뒤 식). 기간식과 AND 로 묶는다. 비었거나 기간이 아닌 규칙이면 무시한다.
    /// <see cref="CustomWhere"/> 와 따로 두어, 예전 작업 파일에 남은 직접 조건이 기간 규칙에 갑자기 붙지 않게 한다.</summary>
    public string? ExtraWhere { get; init; }
    public IReadOnlyList<MaskRule> Masks { get; init; } = [];
    public IReadOnlyList<string> SkipColumns { get; init; } = [];
}

public enum CommitMode { PerTable, PerBatch, AtEnd }

public sealed record CopyOptions
{
    public int BatchSize { get; init; } = 5000;
    public CommitMode Commit { get; init; } = CommitMode.PerTable;
    public bool DisableTriggersByDefault { get; init; }
    public int MaxFailedRows { get; init; } = 100;
    public long SequenceGapConfirm { get; init; } = 10_000_000;
}

/// <summary>
/// 시퀀스 보정 연결. <paramref name="Table"/> 은 규칙 키(<see cref="CopyOwners.Key(CopyJob, TableRule)"/>: 기본 원본 계정의 표는 이름만, 다른 계정이면 <c>계정.표</c>).
/// </summary>
public sealed record SequenceRule(string Sequence, string Table, string Column)
{
    /// <summary>시퀀스 계정. null 이면 그 표 규칙의 대상 계정(<see cref="CopyOwners.SequenceOwner"/>)이다.</summary>
    public string? Owner { get; init; }
}

public enum CopyOutcome { Succeeded, PartiallyFailed, Failed, Stopped }

public sealed record LastRunInfo(DateTimeOffset At, CopyOutcome Outcome, long Rows);

public static class TargetActions
{
    /// <summary>규칙 종류별 기본 대상 처리. <c>Exclude</c> 는 값을 돌려주되 실행기가 쓰지 않는다.</summary>
    public static TargetAction DefaultFor(RuleKind kind, bool hasPrimaryKey) => kind switch
    {
        RuleKind.All => hasPrimaryKey ? TargetAction.MergeByPk : TargetAction.DeleteAllInsert,
        _ => TargetAction.DeleteRangeInsert,
    };
}
