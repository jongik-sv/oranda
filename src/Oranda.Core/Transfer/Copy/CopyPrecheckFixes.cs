using Oranda.Core.Connection;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 사전 점검 항목을 그 자리에서 고치는 처리의 종류(PC1, 결정 D-066). 앞의 것은 작업 규칙 값을 바꾸고(<see cref="CopyPrecheckFix.ChangesJob"/>, 다시 점검한다),
/// 뒤의 셋(<see cref="DisableTriggers"/>·<see cref="DisableForeignKeys"/>·<see cref="ProceedSequenceGap"/>)은 이번 실행의 결정(<see cref="CheckChoice"/>)만 고른다.
/// </summary>
public enum CopyFixKind
{
    /// <summary>그 테이블의 「없으면 만들기」를 켠다(<see cref="TableRule.CreateIfMissing"/>, 따로 바꾼 항목으로 표시).</summary>
    CreateIfMissing,

    /// <summary>그 열을 규칙의 건너뛸 열(<see cref="TableRule.SkipColumns"/>)에 넣는다(「대상에 열 추가」에 있으면 뺀다).</summary>
    SkipColumn,

    /// <summary>그 열을 「대상에 열 추가」(<see cref="TableRule.AddColumns"/>)에 넣는다: 실행이 적재 전에 <c>ALTER TABLE … ADD</c> 를 보낸다.</summary>
    AddColumn,

    /// <summary>대상 처리를 PK 기준 MERGE 로 바꾼다(따로 바꾼 항목으로 표시).</summary>
    SwitchToMerge,

    /// <summary>시퀀스 후보를 작업의 시퀀스 보정 연결(<see cref="CopyJob.Sequences"/>)에 넣는다. 대상에 없으면 실행이 적재 전에 만든다(SQ3).</summary>
    LinkSequence,

    /// <summary>동시 테이블 수를 1(한 테이블씩, <see cref="CopyOptions.ConcurrentTables"/> = null)로 둔다.</summary>
    OneTableAtATime,

    /// <summary>같은 시점 읽기(<see cref="CopyJob.ConsistentRead"/>)를 끈다.</summary>
    ConsistentReadOff,

    /// <summary>실행 결정: 복사하는 동안 그 테이블 트리거를 끈다.</summary>
    DisableTriggers,

    /// <summary>실행 결정: 복사하는 동안 지우기를 막는 자식 FK 를 끈다.</summary>
    DisableForeignKeys,

    /// <summary>실행 결정: 시간이 걸려도 시퀀스를 보정한다.</summary>
    ProceedSequenceGap,
}

/// <summary>
/// 점검 항목 하나를 고치는 처리 설명(순수 데이터, DB 를 부르지 않는다). <paramref name="Id"/> 는 점검 결과 안에서 하나뿐인 이름,
/// <paramref name="Title"/> 은 화면 글(종류마다 고정, <see cref="CopyPrecheckFixes.TitleOf"/>), <paramref name="TableKey"/> 는 규칙 키(작업 전체 처리면 null).
/// </summary>
public sealed record CopyPrecheckFix(string Id, string Title, string? TableKey, CopyFixKind Kind)
{
    /// <summary>열 처리(건너뛰기·열 추가)의 열, 시퀀스 연결의 키 열.</summary>
    public string? Column { get; init; }

    /// <summary>시퀀스 연결의 시퀀스 이름.</summary>
    public string? Sequence { get; init; }

    /// <summary>실행 때 보낼 문장 미리 보기(「대상에 열 추가」의 <c>ALTER TABLE … ADD</c>). 없으면 null.</summary>
    public string? Preview { get; init; }

    /// <summary>작업 규칙 값을 바꾸는가(바꾸면 저장하고 다시 점검한다). 거짓이면 이번 실행 결정만 고른다.</summary>
    public bool ChangesJob => Decision is null;

    /// <summary>실행 결정 처리면 그 선택지, 아니면 null.</summary>
    public CheckChoice? Decision => Kind switch
    {
        CopyFixKind.DisableTriggers => CheckChoice.DisableTriggers,
        CopyFixKind.DisableForeignKeys => CheckChoice.DisableForeignKeys,
        CopyFixKind.ProceedSequenceGap => CheckChoice.ProceedSequenceGap,
        _ => null,
    };
}

/// <summary>처리 설명을 만들 때 쓰는 점검 밖의 사실: 원본·대상 DBMS, 대상이 개발 접속인지(표·열을 만들 수 있는지), 원본 코덱(옮길 수 있는 형식 판정).</summary>
public sealed record CopyFixContext(Dbms SourceDbms, Dbms TargetDbms, bool TargetIsDevelopment)
{
    public IBulkValueCodec? SourceCodec { get; init; }
}

/// <summary>
/// 사전 점검 결과의 항목마다 그 자리에서 고칠 처리(PC1, 결정 D-066)를 정하고(<see cref="For"/>·<see cref="Attach"/>), 고른 처리를 작업에 적용한다(<see cref="Apply"/>).
/// 둘 다 순수 함수다: 점검은 대상에 DDL 을 보내지 않고, 처리는 규칙 값(테이블별로 따로 바꾼 값)·실행 결정만 바꾼다 — 규칙 편집에서 같은 값을 되돌릴 수 있다.
/// 표·열을 만드는 처리(<see cref="CopyFixKind.CreateIfMissing"/>·<see cref="CopyFixKind.AddColumn"/>)는 대상이 개발 접속일 때만 낸다(<see cref="CopyTargetGuard.CanRecreate"/>).
/// </summary>
public static class CopyPrecheckFixes
{
    /// <summary>처리 종류의 화면 글(한국어, 영어는 화면의 문구 표).</summary>
    public static string TitleOf(CopyFixKind kind) => kind switch
    {
        CopyFixKind.CreateIfMissing => "없으면 만들기 켜기",
        CopyFixKind.SkipColumn => "그 열 빼고 복사",
        CopyFixKind.AddColumn => "대상에 열 추가",
        CopyFixKind.SwitchToMerge => "PK 기준 MERGE 로 바꾸기",
        CopyFixKind.LinkSequence => "시퀀스 연결",
        CopyFixKind.OneTableAtATime => "한 테이블씩 복사",
        CopyFixKind.ConsistentReadOff => "같은 시점 읽기 끄기",
        CopyFixKind.DisableTriggers => "복사하는 동안 트리거 끄기",
        CopyFixKind.DisableForeignKeys => "복사하는 동안 자식 FK 끄기",
        _ => "시간이 걸려도 시퀀스 보정",
    };

    /// <summary>점검 결과의 모든 항목에 처리 설명을 붙인 결과(<see cref="CheckItem.Fixes"/>). 고칠 것이 없는 항목은 그대로다.</summary>
    public static PrecheckReport Attach(CopyJob job, PrecheckReport report, CopyFixContext context)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(context);
        if (report.Items.Count == 0)
        {
            return report;
        }

        var items = report.Items.Select(i => For(job, report, i, context) is { Count: > 0 } fixes ? i with { Fixes = fixes } : i).ToList();
        return report with { Items = items };
    }

    /// <summary>항목 하나를 고칠 처리(둘 이상이면 대안). 고칠 것이 없으면 빈 목록.</summary>
    public static IReadOnlyList<CopyPrecheckFix> For(CopyJob job, PrecheckReport report, CheckItem item, CopyFixContext context)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        var rule = item.Table is { } key ? CopyOwners.Find(job, key) : null;
        var plan = item.Table is null ? null : report.Order.FirstOrDefault(p => string.Equals(CopyOwners.Key(job, p.Rule), item.Table, StringComparison.Ordinal));
        var fixes = new List<CopyPrecheckFix>();

        void Add(CopyFixKind kind, string? column = null, string? sequence = null, string? preview = null) =>
            fixes.Add(new CopyPrecheckFix($"{item.Code}:{item.Table}:{column}:{kind}", TitleOf(kind), item.Table, kind)
            {
                Column = column,
                Sequence = sequence,
                Preview = preview,
            });

        switch (item.Code)
        {
            case "TargetMissing" when rule is { CreateIfMissing: false } && rule.Action != TargetAction.Recreate && context.TargetIsDevelopment:
                Add(CopyFixKind.CreateIfMissing);
                break;

            case "ColumnNotInTarget" when rule is not null && item.Column is { } column:
                Add(CopyFixKind.SkipColumn, column);
                if (context.TargetIsDevelopment && plan is not null && !plan.CreateMissing && rule.Action != TargetAction.Recreate
                    && plan.Source.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.Ordinal)) is { } sourceColumn
                    && CopyAddColumnDdl.Plan(context.SourceDbms, context.TargetDbms, sourceColumn, plan.Target.Table.Owner, plan.Target.Table.Name, context.SourceCodec, out _) is { } add)
                {
                    Add(CopyFixKind.AddColumn, column, preview: add.Ddl);
                }

                break;

            case "UnsupportedType" or CopyCheckCodes.AddColumnBlocked when rule is not null && item.Column is { } column:
                Add(CopyFixKind.SkipColumn, column);
                break;

            case "ChildRowsMayBlock" when rule is not null:
                if (item.Choices.Contains(CheckChoice.SwitchToMerge))
                {
                    Add(CopyFixKind.SwitchToMerge);
                }

                if (item.Choices.Contains(CheckChoice.DisableForeignKeys) && plan is { DisablableForeignKeys.Count: > 0 })
                {
                    Add(CopyFixKind.DisableForeignKeys);
                }

                break;

            case "TargetTriggers" when item.Choices.Contains(CheckChoice.DisableTriggers) && plan is { CanDisableTriggers: true }:
                Add(CopyFixKind.DisableTriggers);
                break;

            case "SequenceGap" when item.Choices.Contains(CheckChoice.ProceedSequenceGap):
                Add(CopyFixKind.ProceedSequenceGap);
                break;

            case "SequenceGap" when item is { Table: { } table, Column: { } column, Sequence: { } sequence }
                                    && !job.Sequences.Any(s => s.Table == table && s.Column == column):
                Add(CopyFixKind.LinkSequence, column, sequence);
                break;

            case CopyCheckCodes.ConcurrentCopy when item.Level == CheckLevel.Confirm:
                Add(CopyFixKind.OneTableAtATime);
                break;

            case CopyCheckCodes.FlashbackNoPrivilege:
            case CopyCheckCodes.ReadPoint when item.Level == CheckLevel.Error:
                Add(CopyFixKind.OneTableAtATime);
                if (job.ConsistentRead)
                {
                    Add(CopyFixKind.ConsistentReadOff);
                }

                break;

            default:
                break;
        }

        return fixes;
    }

    /// <summary>
    /// 고른 처리(규칙을 바꾸는 것만, <see cref="CopyPrecheckFix.ChangesJob"/>)를 작업에 적용한 새 작업. 실행 결정 처리는 무시한다.
    /// 규칙 키로 테이블을 찾지 못하는 처리도 무시한다. 바뀐 것이 없으면 <paramref name="job"/> 그대로(같은 참조)를 돌려준다.
    /// </summary>
    public static CopyJob Apply(CopyJob job, IEnumerable<CopyPrecheckFix> fixes)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(fixes);
        var result = job;
        foreach (var fix in fixes.Where(f => f.ChangesJob))
        {
            result = fix.Kind switch
            {
                CopyFixKind.OneTableAtATime when result.Options.ConcurrentTables is not null
                    => result with { Options = result.Options with { ConcurrentTables = null } },
                CopyFixKind.ConsistentReadOff when result.ConsistentRead => result with { ConsistentRead = false },
                CopyFixKind.LinkSequence when fix is { TableKey: { } table, Column: { } column, Sequence: { } sequence }
                                              && CopyOwners.Find(result, table) is not null
                                              && !result.Sequences.Any(s => s.Table == table && s.Column == column && s.Sequence == sequence)
                    => result with { Sequences = [.. result.Sequences.Where(s => !(s.Table == table && s.Column == column)), new SequenceRule(sequence, table, column)] },
                _ when fix.TableKey is { } key => WithRule(result, key, rule => ApplyToRule(rule, fix)),
                _ => result,
            };
        }

        return result;
    }

    private static CopyJob WithRule(CopyJob job, string key, Func<TableRule, TableRule> change)
    {
        var changed = false;
        var tables = job.Tables.Select(rule =>
        {
            if (!string.Equals(CopyOwners.Key(job, rule), key, StringComparison.Ordinal))
            {
                return rule;
            }

            var next = change(rule);
            changed |= !ReferenceEquals(next, rule);
            return next;
        }).ToList();
        return changed ? job with { Tables = tables } : job;
    }

    private static TableRule ApplyToRule(TableRule rule, CopyPrecheckFix fix)
    {
        switch (fix.Kind)
        {
            case CopyFixKind.CreateIfMissing when !rule.CreateIfMissing && rule.Action != TargetAction.Recreate:
                return rule with { CreateIfMissing = true, Overrides = rule.Overrides | RuleOverrides.CreateIfMissing };

            case CopyFixKind.SwitchToMerge when rule.Action != TargetAction.MergeByPk:
                return rule with { Action = TargetAction.MergeByPk, Overrides = rule.Overrides | RuleOverrides.Action };

            case CopyFixKind.SkipColumn when fix.Column is { } column:
            {
                var skips = rule.SkipColumns.Contains(column, StringComparer.OrdinalIgnoreCase) ? rule.SkipColumns : [.. rule.SkipColumns, column];
                var adds = Without(rule.AddColumns, column);
                return ReferenceEquals(skips, rule.SkipColumns) && ReferenceEquals(adds, rule.AddColumns) ? rule : rule with { SkipColumns = skips, AddColumns = adds };
            }

            case CopyFixKind.AddColumn when fix.Column is { } column:
            {
                var adds = rule.AddColumns?.Contains(column, StringComparer.Ordinal) == true ? rule.AddColumns : [.. rule.AddColumns ?? [], column];
                var skips = rule.SkipColumns.Contains(column, StringComparer.OrdinalIgnoreCase)
                    ? rule.SkipColumns.Where(c => !string.Equals(c, column, StringComparison.OrdinalIgnoreCase)).ToList()
                    : rule.SkipColumns;
                return ReferenceEquals(skips, rule.SkipColumns) && ReferenceEquals(adds, rule.AddColumns) ? rule : rule with { SkipColumns = skips, AddColumns = adds };
            }

            default:
                return rule;
        }
    }

    // 목록에서 그 열을 뺀다(없으면 같은 참조, 비면 null — 파일에 쓰지 않는다).
    private static IReadOnlyList<string>? Without(IReadOnlyList<string>? list, string column)
    {
        if (list is null || !list.Contains(column, StringComparer.Ordinal))
        {
            return list;
        }

        var rest = list.Where(c => !string.Equals(c, column, StringComparison.Ordinal)).ToList();
        return rest.Count == 0 ? null : rest;
    }
}
