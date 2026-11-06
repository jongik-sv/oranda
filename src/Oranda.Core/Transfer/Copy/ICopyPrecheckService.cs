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

public enum CheckChoice { DisableTriggers, DisableForeignKeys, SkipColumn, ProceedSequenceGap, SwitchToMerge }

public sealed record CheckItem(string? Table, string Code, CheckLevel Level, string Message)
{
    public IReadOnlyList<CheckChoice> Choices { get; init; } = [];
    public string? Column { get; init; }
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

/// <summary><paramref name="Order"/> 는 부모 → 자식 순서다.</summary>
public sealed record PrecheckReport(IReadOnlyList<CheckItem> Items, IReadOnlyList<CopyTablePlan> Order,
                                    IReadOnlyList<PreviewStatement> Preview,
                                    IReadOnlyDictionary<string, long> EstimatedRows)
{
    public bool HasErrors => Items.Any(i => i.Level == CheckLevel.Error);
}
