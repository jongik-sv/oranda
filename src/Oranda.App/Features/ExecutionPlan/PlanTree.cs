using System;
using System.Collections.Generic;
using Oranda.Core.Plans;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 트리 한 줄의 들여쓰기 안내선(TSK-13-14). <see cref="PlanTreeGuides"/> 가 그린다.
/// </summary>
/// <param name="Depth">깊이(뿌리 0).</param>
/// <param name="IsLast">같은 부모의 마지막 자식이면 └, 아니면 ├.</param>
/// <param name="HasChildren">바로 아래 줄이 자식이면 아이콘 밑으로 선을 내린다.</param>
/// <param name="Continues">조상 칸마다 세로선을 이어 그릴지(칸 k = 깊이 k+1 의 조상, 길이 Depth−1).</param>
public sealed record PlanTreeLines(int Depth, bool IsLast, bool HasChildren, IReadOnlyList<bool> Continues)
{
    /// <summary>
    /// Id 순(앞쪽 순회) 깊이 목록에서 줄마다 안내선을 구한다. 아래에서 위로 훑으며 깊이마다 「아래에 같은 깊이 형제가 남았나」를 기억한다.
    /// </summary>
    public static IReadOnlyList<PlanTreeLines> Compute(IReadOnlyList<int> depths)
    {
        ArgumentNullException.ThrowIfNull(depths);
        var n = depths.Count;
        var result = new PlanTreeLines[n];
        var open = new List<bool>();
        for (var i = n - 1; i >= 0; i--)
        {
            var d = Math.Max(0, depths[i]);
            while (open.Count <= d)
            {
                open.Add(false);
            }

            var continues = new bool[Math.Max(0, d - 1)];
            for (var k = 0; k < continues.Length; k++)
            {
                continues[k] = open[k + 1];
            }

            var hasChildren = i + 1 < n && depths[i + 1] > d;
            result[i] = new PlanTreeLines(d, !open[d], hasChildren, continues);

            open[d] = true;
            for (var k = d + 1; k < open.Count; k++)
            {
                open[k] = false;
            }
        }

        return result;
    }
}

/// <summary>계획 연산 종류별 아이콘 키(ShellIcons). 표 접근·인덱스·조인·정렬·집계·뷰·거르기 등.</summary>
public static class PlanOperationIcons
{
    public static string KeyFor(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var op = step.Operation.ToUpperInvariant();
        var options = step.Options?.ToUpperInvariant() ?? string.Empty;
        if (op.EndsWith(" STATEMENT", StringComparison.Ordinal))
        {
            return "fileSql";
        }

        if (op.StartsWith("TABLE ACCESS", StringComparison.Ordinal))
        {
            return "table";
        }

        if (op.StartsWith("MAT_VIEW", StringComparison.Ordinal))
        {
            return "objMview";
        }

        if (op.Contains("INDEX", StringComparison.Ordinal) || op.StartsWith("BITMAP", StringComparison.Ordinal))
        {
            return "objIndex";
        }

        if (op.Contains("JOIN", StringComparison.Ordinal) || op == "NESTED LOOPS")
        {
            return "join";
        }

        if (options.Contains("GROUP BY", StringComparison.Ordinal) || options.Contains("AGGREGATE", StringComparison.Ordinal) || op == "COUNT")
        {
            return "aggregate";
        }

        return op switch
        {
            "SORT" or "WINDOW" or "BUFFER" or "HASH" => "sort",
            "VIEW" or "VIEW PUSHED PREDICATE" => "objView",
            "FILTER" => "filter",
            "REMOTE" => "objDbLink",
            "SEQUENCE" => "objSequence",
            _ when op.StartsWith("PARTITION", StringComparison.Ordinal) => "folder",
            _ => "objOther",
        };
    }
}
