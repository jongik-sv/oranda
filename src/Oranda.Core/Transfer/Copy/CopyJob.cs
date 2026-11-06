namespace Oranda.Core.Transfer.Copy;

/// <summary>작업 파일 스키마 버전. 파일의 <c>schemaVersion</c> 이 이보다 크면 읽기 전용으로 연다.</summary>
public static class CopyJobSchema
{
    public const int Current = 1;
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

public sealed record CopyPeriod(PeriodKind Kind, DateTime? From = null, DateTime? To = null);

public sealed record ResolvedPeriod(DateTime? From, DateTime? To);

public enum RuleKind { All, Period, ParentKey, Custom, Exclude }

public enum TargetAction { DeleteAllInsert, DeleteRangeInsert, AppendOnly, MergeByPk, Recreate }

/// <summary><paramref name="StringFormat"/> 이 null 이면 DATE·TIMESTAMP 열이다.</summary>
public sealed record PeriodColumn(string Column, string? StringFormat = null);

public sealed record ParentLink(string ParentTable, IReadOnlyList<string> ChildColumns,
                                IReadOnlyList<string> ParentColumns, string? ForeignKeyName = null);

public enum MaskKind { Fixed, KeepFirst, KeepLast, Hash, Null }

public sealed record MaskRule(string Column, MaskKind Kind, string? Value = null, int Count = 0);

public sealed record TableRule(string Table, RuleKind Kind, TargetAction Action)
{
    /// <summary>0~2개. 2개면 OR 로 묶는다.</summary>
    public IReadOnlyList<PeriodColumn> PeriodColumns { get; init; } = [];
    public ParentLink? Parent { get; init; }
    public string? CustomWhere { get; init; }
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

public sealed record SequenceRule(string Sequence, string Table, string Column);

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
