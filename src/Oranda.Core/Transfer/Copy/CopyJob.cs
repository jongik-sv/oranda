using System.Text.Json.Serialization;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 작업 파일 스키마 버전. 파일의 <c>schemaVersion</c> 이 이보다 크면 읽기 전용으로 연다.
/// 2 = 규칙마다 원본·대상 계정(묶음 B). 1 → 2 변환은 <see cref="CopyJobMigrationV1"/>.
/// 3 = 작업 변수(<see cref="CopyJob.Variables"/>). 2 → 3 은 바꿀 것이 없다(<see cref="CopyJobMigrationV2"/>). 버전을 올리는 까닭: 예전 앱이 변수를 버린 채
/// 조건의 <c>:이름</c> 을 실행하지 않게(읽기 전용으로 열게) 한다.
/// 4 = 표 규칙의 「대상에 표가 없으면 만들기」(<see cref="TableRule.CreateIfMissing"/>). 3 → 4 는 바꿀 것이 없다(<see cref="CopyJobMigrationV3"/>, 옛 파일 = 꺼짐).
/// 버전을 올리는 까닭: 예전 앱이 이 칸을 버린 채 다시 저장해 선택이 사라지지 않게(읽기 전용으로 열게) 한다.
/// 5 = 테이블 규칙의 전체 기본값(<see cref="CopyJob.TableDefaults"/>)과 테이블별로 따로 바꾼 항목(<see cref="TableRule.Overrides"/>). 4 → 5 는 바꿀 것이 없다(<see cref="CopyJobMigrationV4"/>):
/// 옛 파일은 <c>tableDefaults</c> 가 없고, 그러면 화면이 모든 테이블을 「따로 바꾼 것」으로 열어 전체 기본값을 바꿔도 저장된 값이 그대로다.
/// 버전을 올리는 까닭: 예전 앱이 두 칸을 버린 채 다시 저장하면 따로 바꾼 표시가 사라져 전체 값 변경이 저장된 값을 덮어쓸 수 있다(읽기 전용으로 열게).
/// <para>
/// 버전을 올리지 않은 칸: 실행 옵션 <see cref="CopyOptions.SerialReadWrite"/>(읽기·쓰기 번갈아)·<see cref="CopyOptions.WaitLimitMinutes"/>(테이블당 대기 한도, PF1)·
/// <see cref="CopyOptions.NoLobPrefetch"/>(LOB 미리 받기 끔, PF3)·<see cref="CopyOptions.ConcurrentTables"/>(동시 테이블 수, PF2).
/// 기본값이면 파일에 쓰지 않고, 예전 앱이 버려도 무엇을 읽고 지우는지는 바뀌지 않는다(속도·멈춤 대응·동시 수만 기본으로 돌아간다 — 동시 수가 기본(1)이면 예전처럼 한 테이블씩 복사한다).
/// 버전을 올리면 새 앱이 저장한 모든 작업이 예전 앱에서 읽기 전용이 되므로 그 비용을 치르지 않는다.
/// 남는 위험(받아들임): 예전 앱이 이 작업을 열었다가 저장하면 두 칸(<c>serialReadWrite</c>·<c>waitLimitMinutes</c>)이 알림 없이 사라진다 —
/// 새 앱에서 다시 열면 읽기·쓰기 겹치기 켬·대기 한도 없음(기본)으로 돌아가 있으니 필요하면 다시 고른다.
/// </para>
/// </summary>
public static class CopyJobSchema
{
    public const int Current = 5;
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

    /// <summary>조건(직접 조건·추가 조건)에서 <c>:이름</c> 으로 쓰는 작업 변수(<see cref="CopyVariables"/>).</summary>
    public IReadOnlyList<CopyVariable> Variables { get; init; } = [];

    /// <summary>
    /// 복사 창 위쪽의 전체 기본값(규칙·대상 처리·없으면 만들기). 테이블을 더할 때 채우고, 바꾸면 따로 바꾸지 않은 테이블에만 반영한다.
    /// 없으면(옛 파일) 모든 테이블을 따로 바꾼 것으로 본다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TableDefaults? TableDefaults { get; init; }

    /// <summary>설계 8.1 「같은 시점 기준으로 읽기」.</summary>
    public bool ConsistentRead { get; init; } = true;

    /// <summary>32바이트 난수 Base64. 작업을 만들 때 한 번 정한다.</summary>
    public required string MaskSalt { get; init; }

    public LastRunInfo? LastRun { get; init; }
}

public sealed record ConnectionRef(string Name, string User, Guid? IdHint = null);

public enum CopyVariableType { Text, Number, Date }

/// <summary>
/// 작업 변수 하나. 값이 하나면 <c>:이름</c> 그대로 바인드하고, 여럿이면 목록으로 펼친다(<c>IN (:이름)</c> 으로 쓴다, <see cref="CopyVariables.Expand"/>).
/// 값은 적은 글 그대로 둔다(숫자는 <c>123.4</c>, 날짜는 <c>yyyy-MM-dd</c> 또는 <c>yyyy-MM-dd HH:mm:ss</c>).
/// </summary>
public sealed record CopyVariable(string Name, CopyVariableType Type, IReadOnlyList<string> Values);

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

/// <summary>
/// 테이블 규칙의 전체 기본값. <paramref name="Kind"/> 가 null 이면 「자동」(기간이 있고 기간 열이 있는 테이블은 기간, 아니면 모든 행),
/// <paramref name="Action"/> 이 null 이면 「자동」(규칙 종류별 기본 대상 처리, <see cref="TargetActions.DefaultFor"/>).
/// </summary>
public sealed record TableDefaults(RuleKind? Kind, TargetAction? Action, bool CreateIfMissing);

/// <summary>테이블이 전체 기본값을 따르지 않고 따로 바꾼 항목.</summary>
[Flags]
public enum RuleOverrides
{
    None = 0,
    Kind = 1,
    Action = 2,
    CreateIfMissing = 4,
    All = Kind | Action | CreateIfMissing,
}

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

    /// <summary>
    /// 대상 계정에 같은 이름의 표가 없으면 원본 구조로 만든 뒤 넣는다(개발 접속만). 대상 표가 있으면 무시한다(지금 동작 그대로).
    /// <see cref="TargetAction.Recreate"/> 와는 뜻이 없다(이미 만든다). 꺼짐이면 파일에 쓰지 않는다(예전 파일과 같다).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CreateIfMissing { get; init; }

    /// <summary>전체 기본값(<see cref="CopyJob.TableDefaults"/>)을 따르지 않고 이 테이블에서 따로 바꾼 항목. 없음이면 파일에 쓰지 않는다.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RuleOverrides Overrides { get; init; }
}

public enum CommitMode { PerTable, PerBatch, AtEnd }

public sealed record CopyOptions
{
    public int BatchSize { get; init; } = 5000;
    public CommitMode Commit { get; init; } = CommitMode.PerTable;
    public bool DisableTriggersByDefault { get; init; }
    public int MaxFailedRows { get; init; } = 100;
    public long SequenceGapConfirm { get; init; } = 10_000_000;

    /// <summary>
    /// 테이블 안에서 읽기와 쓰기를 겹치지 않고 번갈아 한다(옛 동작: 원본을 읽다 배치가 차면 쓰기를 기다린 뒤 다시 읽는다, 결정 69).
    /// 꺼짐(기본) = 원본 읽기와 대상 쓰기를 겹친다(사이에 배치 몇 개 크기의 버퍼). 겹치기에 문제가 있을 때 켠다. 꺼짐이면 파일에 쓰지 않는다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SerialReadWrite { get; init; }

    /// <summary>
    /// 테이블당 대기 한도(분, 결정 73). 원본 읽기나 대상 쓰기 명령 하나가 이 시간 넘게 응답이 없으면 그 명령을 취소하고 그 테이블을 되돌려 실패로 둔 뒤
    /// 다음 테이블로 간다. 0(기본) = 한도 없음. 0 이면 파일에 쓰지 않는다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WaitLimitMinutes { get; init; }

    /// <summary>
    /// 원본 읽기에서 LOB 값을 행과 함께 미리 받지 않는다(옛 동작: 위치자만 받고 LOB 마다 따로 읽는다, PF3). 꺼짐(기본) = 테이블 원본 읽기가
    /// LOB 을 <see cref="CopyRunner.LobPrefetchSize"/> 까지 행과 함께 받는다. 미리 받기에 문제가 있을 때 켠다. 꺼짐이면 파일에 쓰지 않는다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NoLobPrefetch { get; init; }

    /// <summary>
    /// 동시 테이블 수(PF2, 조정 결정 71·72). null(기본) = 1 = 한 테이블씩(예전 동작). 2~4 면 FK 단계마다 테이블을 그 수까지 동시에 복사한다
    /// (테이블마다 원본·대상 세션 한 쌍). 실제로 동시에 도는지는 <see cref="CopyConcurrency.Effective"/> 가 정한다(같은 시점 읽기·「모두 끝난 뒤 한 번 커밋」이면 끈다).
    /// null 이면 파일에 쓰지 않는다(스키마 버전은 올리지 않는다, <see cref="CopyJobSchema"/>).
    /// </summary>
    public int? ConcurrentTables { get; init; }
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
