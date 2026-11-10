using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

/// <summary>사전 점검. 구현: Oranda.Oracle(<c>Transfer/Copy</c>).</summary>
public interface ICopyPrecheckService
{
    Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period,
                                  IProgress<string>? progress, CancellationToken ct);
}

public sealed record CopySessions(ISession Source, ISession Target);

public enum CheckLevel { Pass, Confirm, Error }

/// <summary>
/// 사전 점검 항목 코드(<see cref="CheckItem.Code"/>) 가운데 묶음 B 에서 새로 생긴 것. 화면의 코드 표시 이름 표가 같은 글을 쓴다.
/// 나머지 코드(<c>SourceNoSelect</c>·<c>TargetNoWrite</c>·<c>TargetMissing</c>·<c>SameSourceTarget</c>·<c>RuleInvalid</c>·<c>RecreateNoPrivilege</c> …)는 글 그대로다.
/// </summary>
public static class CopyCheckCodes
{
    /// <summary>확인 필요: 대상 접속 계정이 아닌 계정의 표를 지우고 넣는다(<see cref="CheckItem.Table"/> 은 null, 작업 전체 한 항목).</summary>
    public const string OtherOwnerTargets = "OtherOwnerTargets";

    /// <summary>확인 필요: 표를 더할 때 적은 시노님(<see cref="TableRule.WrittenAs"/>)이 지금은 다른 표를 가리키거나 풀리지 않는다. 작업은 저장된 표를 그대로 복사한다.</summary>
    public const string SynonymChanged = "SynonymChanged";

    /// <summary>오류: 기간 규칙이 있는데 기간 범위가 비었다(끝 경계가 시작보다 늦지 않음). 그대로 두면 0행을 복사하고 성공으로 끝난다.</summary>
    public const string EmptyPeriod = "EmptyPeriod";

    /// <summary>확인 필요: 대상에 표가 없어 원본 구조로 만든 뒤 넣는다(<see cref="TableRule.CreateIfMissing"/>). 메시지에 만드는 것·안 만드는 것·실패 때의 정리를 적는다.</summary>
    public const string CreateTable = "CreateTable";

    /// <summary>오류: 「없으면 만들기」를 쓸 수 없다 — 대상 소유자 계정이 아니거나 CREATE TABLE 권한이 없다.</summary>
    public const string CreateNoPrivilege = "CreateNoPrivilege";

    /// <summary>오류: 원본 구조로 만들 수 없는 이유가 있다(지원하지 않는 형식 열·문자열 한도·가리키는 객체 없음·이름 겹침 …, <c>RecreateBlocked</c> 와 같은 조건).</summary>
    public const string CreateBlocked = "CreateBlocked";

    /// <summary>확인 필요(교차 복사만): 대상 DBMS 로 옮길 형식이 없는 열을 복사에서 뺀다(표·열·형식·이유). 같은 DBMS 의 <c>UnsupportedType</c> 오류와 다르다.</summary>
    public const string ColumnDropped = "ColumnDropped";

    /// <summary>오류(교차 복사만): 키(PK)·기간 조건·부모 키 연결 열을 대상 DBMS 로 옮길 형식이 없어 복사에서 빠진다 — 키가 없어지거나 조건 열이 NULL 로 들어가 다시 실행하면 행이 겹친다. 열마다 한 항목.</summary>
    public const string KeyColumnDropped = "KeyColumnDropped";

    /// <summary>교차 복사만: 형식을 바꿔 옮기는 열(표마다 한 항목). 값이 바뀌거나 행이 실패할 수 있는 변환(BINARY_* → NUMBER·시간대 지역 → 오프셋)이 있으면 확인 필요, 아니면 통과.</summary>
    public const string TypeConverted = "TypeConverted";

    /// <summary>확인 필요(교차 복사로 Tibero 에 넣을 때, 작업 전체 한 항목): 대상 문자셋 밖 글자는 실패하거나 바뀔 수 있다.</summary>
    public const string CrossCharset = "CrossCharset";

    /// <summary>통과 안내(교차 복사로 Tibero 에 BLOB 을 넣을 때): 길이가 0 인 BLOB 은 NULL 이 된다.</summary>
    public const string BlobEmptyToNull = "BlobEmptyToNull";
}

public enum CheckChoice { DisableTriggers, DisableForeignKeys, SkipColumn, ProceedSequenceGap, SwitchToMerge }

/// <summary>점검 항목 하나. <paramref name="Table"/> 은 규칙 키(<see cref="CopyOwners.Key(CopyJob, TableRule)"/>)이고, 작업 전체 항목이면 null.</summary>
public sealed record CheckItem(string? Table, string Code, CheckLevel Level, string Message)
{
    public IReadOnlyList<CheckChoice> Choices { get; init; } = [];
    public string? Column { get; init; }

    /// <summary>시퀀스 후보 항목일 때 후보 시퀀스 이름. 화면이 「연결」 체크로 작업에 저장한다.</summary>
    public string? Sequence { get; init; }
}

public sealed record CopyTablePlan(TableRule Rule, TableInfo Source, TableInfo Target,
                                   IReadOnlyList<string> Columns, IReadOnlyList<string> KeyColumns)
{
    /// <summary>
    /// 대상에 표가 없어 이번 실행이 원본 구조로 만든 뒤 넣는 표(<see cref="TableRule.CreateIfMissing"/>). <see cref="Target"/> 은 만들 구조다.
    /// 새 표라 지우기를 건너뛰고(INSERT 로 넣는다) 제약·트리거·권한 점검을 하지 않는다.
    /// </summary>
    public bool CreateMissing { get; init; }

    public bool CanDisableTriggers { get; init; }
    public IReadOnlyList<string> Triggers { get; init; } = [];
    public IReadOnlyList<DisabledObject> DisablableForeignKeys { get; init; } = [];
}

public enum PreviewSide { Source, Target }

public sealed record PreviewStatement(string Sql, PreviewSide Side, PolicyDecisionKind Policy,
                                      string? PolicyMessage, string? RetypeText);

/// <summary><paramref name="Order"/> 는 부모 → 자식 순서다. <paramref name="EstimatedRows"/> 는 규칙 키로 찾는다.</summary>
public sealed record PrecheckReport(IReadOnlyList<CheckItem> Items, IReadOnlyList<CopyTablePlan> Order,
                                    IReadOnlyList<PreviewStatement> Preview,
                                    IReadOnlyDictionary<string, long> EstimatedRows)
{
    public bool HasErrors => Items.Any(i => i.Level == CheckLevel.Error);
}
