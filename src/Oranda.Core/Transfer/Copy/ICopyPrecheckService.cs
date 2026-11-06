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
