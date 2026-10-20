using System;
using System.Globalization;
using Oranda.Core.Plans;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>계획 비교 목록 한 줄(PLAN-04). 화면 글은 만들 때 한 번만 구한다.</summary>
public sealed class PlanCompareRowItem
{
    private const string Missing = "–";

    public PlanCompareRowItem(PlanDiffRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        var pinned = row.Pinned;
        var current = row.Current;
        var shown = current ?? pinned ?? throw new ArgumentException("비교 줄에 단계가 없습니다.", nameof(row));

        (KindMark, KindTip) = row.Kind switch
        {
            PlanDiffKind.Changed => ("≠", "바뀜: 같은 자리의 연산이나 객체가 바뀌었습니다"),
            PlanDiffKind.Added => ("+", "추가: 지금 계획에만 있습니다"),
            PlanDiffKind.Removed => ("−", "빠짐: 고정한 계획에만 있습니다"),
            _ => ("=", "같음: 연산과 객체가 같습니다"),
        };

        PinnedIdText = pinned?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        CurrentIdText = current?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        var operation = PlanRowItem.OperationName(shown);
        var obj = PlanRowItem.ObjectName(shown);
        if (row.Kind == PlanDiffKind.Changed && pinned is not null)
        {
            var before = PlanRowItem.OperationName(pinned);
            if (!string.Equals(before, operation, StringComparison.Ordinal))
            {
                operation += "  ← " + before;
            }

            var beforeObject = PlanRowItem.ObjectName(pinned);
            if (!string.Equals(beforeObject, obj, StringComparison.Ordinal))
            {
                obj = (obj.Length == 0 ? Missing : obj) + " ← " + (beforeObject.Length == 0 ? Missing : beforeObject);
            }
        }

        Operation = new string(' ', shown.Depth * 2) + operation;
        ObjectText = obj;

        CostText = Pair(pinned?.Cost, current?.Cost, row.Kind);
        CostDeltaText = Delta(pinned?.Cost, current?.Cost);
        CostBig = row.CostChangedBig;
        RowsText = Pair(pinned?.EstimatedRows, current?.EstimatedRows, row.Kind);
        RowsDeltaText = Delta(pinned?.EstimatedRows, current?.EstimatedRows);
        RowsBig = row.RowsChangedBig;
        ActualRowsText = pinned?.ActualRows is null && current?.ActualRows is null ? string.Empty : Pair(pinned?.ActualRows, current?.ActualRows, row.Kind);
        ActualRowsDeltaText = Delta(pinned?.ActualRows, current?.ActualRows);
        ActualRowsBig = row.ActualRowsChangedBig;
    }

    public PlanDiffRow Row { get; }

    /// <summary>= 같음, ≠ 바뀜, + 추가, − 빠짐.</summary>
    public string KindMark { get; }

    public string KindTip { get; }

    public bool IsChanged => Row.Kind == PlanDiffKind.Changed;

    public bool IsAdded => Row.Kind == PlanDiffKind.Added;

    public bool IsRemoved => Row.Kind == PlanDiffKind.Removed;

    public string PinnedIdText { get; }

    public string CurrentIdText { get; }

    /// <summary>지금 계획 기준으로 들여쓴 연산(빠짐이면 고정 계획 기준). 바뀌었으면 「← 이전 연산」을 붙인다.</summary>
    public string Operation { get; }

    public string ObjectText { get; }

    /// <summary>「기준 → 지금」. 같으면 값 하나.</summary>
    public string CostText { get; }

    public string CostDeltaText { get; }

    public bool CostBig { get; }

    public string RowsText { get; }

    public string RowsDeltaText { get; }

    public bool RowsBig { get; }

    public string ActualRowsText { get; }

    public string ActualRowsDeltaText { get; }

    public bool ActualRowsBig { get; }

    /// <summary>기준·지금 값 한 칸. 추가·빠짐은 있는 쪽 값만, 같으면 하나만 보인다.</summary>
    internal static string Pair(long? before, long? after, PlanDiffKind kind) => kind switch
    {
        PlanDiffKind.Added => PlanRowItem.Fmt(after),
        PlanDiffKind.Removed => PlanRowItem.Fmt(before),
        _ when before == after => PlanRowItem.Fmt(after),
        _ => (before is null ? Missing : PlanRowItem.Fmt(before)) + " → " + (after is null ? Missing : PlanRowItem.Fmt(after)),
    };

    /// <summary>바뀐 비율(예: 「+150%」, 「−62%」). 기준이 0 이면 차이 값, 같거나 한쪽이 없으면 빈 문자열.</summary>
    internal static string Delta(long? before, long? after)
    {
        if (before is not { } a || after is not { } b || a == b)
        {
            return string.Empty;
        }

        if (a == 0)
        {
            return "+" + PlanRowItem.Fmt(b);
        }

        var pct = (double)(b - a) / a * 100;
        return pct.ToString("+0;−0;0", CultureInfo.InvariantCulture) + "%";
    }
}
