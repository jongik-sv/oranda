using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>두 계획을 맞춘 한 줄의 종류.</summary>
public enum PlanDiffKind
{
    /// <summary>연산·옵션·객체가 같다(수치는 다를 수 있다).</summary>
    Same,

    /// <summary>같은 자리에서 연산이나 객체가 바뀌었다.</summary>
    Changed,

    /// <summary>지금 계획에만 있다.</summary>
    Added,

    /// <summary>고정한 계획에만 있다.</summary>
    Removed,
}

/// <summary>맞춘 한 줄. <see cref="Pinned"/>·<see cref="Current"/> 중 하나는 없을 수 있다(추가·빠짐).</summary>
public sealed record PlanDiffRow(PlanDiffKind Kind, PlanStep? Pinned, PlanStep? Current)
{
    /// <summary>비용이 크게 바뀌었다(<see cref="PlanComparer.IsBigChange"/>).</summary>
    public bool CostChangedBig => PlanComparer.IsBigChange(Pinned?.Cost, Current?.Cost);

    /// <summary>예상 행 수(E-Rows)가 크게 바뀌었다.</summary>
    public bool RowsChangedBig => PlanComparer.IsBigChange(Pinned?.EstimatedRows, Current?.EstimatedRows);

    /// <summary>실제 행 수(A-Rows)가 크게 바뀌었다(둘 다 실제 계획일 때만).</summary>
    public bool ActualRowsChangedBig => PlanComparer.IsBigChange(Pinned?.ActualRows, Current?.ActualRows);
}

/// <summary>두 계획의 비교 결과(PLAN-04).</summary>
public sealed record PlanComparison(IReadOnlyList<PlanDiffRow> Rows, long? PinnedHash, long? CurrentHash, long? PinnedCost, long? CurrentCost)
{
    /// <summary>plan_hash_value 가 다르면 true, 같으면 false, 한쪽이라도 모르면 null.</summary>
    public bool? HashDiffers => PinnedHash is { } a && CurrentHash is { } b ? a != b : null;

    /// <summary>단계 수가 너무 많아 최장 공통 부분열 대신 자리 순서로만 맞췄다.</summary>
    public bool Positional { get; init; }

    public int Count(PlanDiffKind kind) => Rows.Count(r => r.Kind == kind);
}

/// <summary>
/// 두 계획 비교(PLAN-04). 맞추는 규칙:
/// <list type="number">
/// <item>각 계획을 Id 순(Oracle 의 위에서 아래 순서)으로 펼치고, 단계마다 열쇠 = 연산 + 옵션 + 객체(소유자.이름)를 만든다. 깊이는 열쇠에 넣지 않는다 —
/// 위에 단계 하나가 끼면 그 아래가 모두 한 칸씩 깊어지기 때문이다.</item>
/// <item>열쇠 열의 최장 공통 부분열(LCS)로 짝을 지은 단계는 「같음」이다. 앞뒤가 같은 부분은 먼저 떼어 계산량을 줄인다.</item>
/// <item>두 짝 사이에 남은 단계들은 자리 순서대로 하나씩 짝지어 「바뀜」(연산·객체가 바뀐 자리)으로 보고, 짝이 없는 나머지는
/// 고정 계획 쪽이면 「빠짐」, 지금 계획 쪽이면 「추가」다.</item>
/// <item>가운데 부분이 <see cref="MaxLcsCells"/> 칸을 넘을 만큼 크면 LCS 없이 자리 순서로만 맞춘다(<see cref="PlanComparison.Positional"/>).</item>
/// </list>
/// 수치(비용·E-Rows·A-Rows)는 짝지은 줄마다 비교하고, <see cref="BigChangeFactor"/> 배 이상 달라지면 크게 바뀐 것으로 강조한다.
/// </summary>
public static class PlanComparer
{
    /// <summary>이 배수 이상 달라지면 크게 바뀐 수치로 본다.</summary>
    public const double BigChangeFactor = 2;

    /// <summary>두 값 중 큰 쪽이 이 값 미만이면 강조하지 않는다(작은 수 잡음).</summary>
    public const long BigChangeMinimum = 10;

    /// <summary>LCS 표 칸 수 한도(약 16 MB). 넘으면 자리 순서로만 맞춘다.</summary>
    public const int MaxLcsCells = 4_000_000;

    /// <summary>두 값이 모두 있고, 큰 쪽이 <see cref="BigChangeMinimum"/> 이상이며 <see cref="BigChangeFactor"/> 배 이상 차이 나면 true.</summary>
    public static bool IsBigChange(long? before, long? after)
    {
        if (before is not { } a || after is not { } b)
        {
            return false;
        }

        var big = Math.Max(a, b);
        var small = Math.Max(Math.Min(a, b), 1);
        return big >= BigChangeMinimum && (double)big / small >= BigChangeFactor;
    }

    /// <summary>계획의 plan_hash_value. DBMS_XPLAN 글의 「Plan hash value」를 먼저 보고, 없으면 서비스가 준 값.</summary>
    public static long? PlanHashOf(CorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ExecutionPlanText.ParsePlanHashValue(plan.Text) ?? plan.PlanHashValue;
    }

    /// <summary>계획 전체 비용(단계 비용 중 가장 큰 값 = 보통 Id 0).</summary>
    public static long? TotalCost(CorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long? max = null;
        foreach (var s in plan.Steps)
        {
            if (s.Cost is { } c && (max is null || c > max))
            {
                max = c;
            }
        }

        return max;
    }

    public static PlanComparison Compare(CorePlan pinned, CorePlan current)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(current);
        var a = pinned.Steps;
        var b = current.Steps;
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        var ka = Keys(a, keys);
        var kb = Keys(b, keys);

        var rows = new List<PlanDiffRow>(Math.Max(a.Count, b.Count));
        var prefix = 0;
        while (prefix < ka.Length && prefix < kb.Length && ka[prefix] == kb[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < ka.Length - prefix && suffix < kb.Length - prefix && ka[ka.Length - 1 - suffix] == kb[kb.Length - 1 - suffix])
        {
            suffix++;
        }

        for (var i = 0; i < prefix; i++)
        {
            rows.Add(new PlanDiffRow(PlanDiffKind.Same, a[i], b[i]));
        }

        var midA = ka.Length - prefix - suffix;
        var midB = kb.Length - prefix - suffix;
        var positional = (long)(midA + 1) * (midB + 1) > MaxLcsCells;
        if (positional)
        {
            AddGap(rows, a, b, ka, kb, prefix, prefix + midA, prefix, prefix + midB);
        }
        else
        {
            AlignMiddle(rows, a, b, ka, kb, prefix, midA, midB);
        }

        for (var i = 0; i < suffix; i++)
        {
            rows.Add(new PlanDiffRow(PlanDiffKind.Same, a[ka.Length - suffix + i], b[kb.Length - suffix + i]));
        }

        return new PlanComparison(rows, PlanHashOf(pinned), PlanHashOf(current), TotalCost(pinned), TotalCost(current))
        {
            Positional = positional,
        };
    }

    /// <summary>단계 열쇠(연산 + 옵션 + 객체)를 정수로 바꾼다(같은 글이면 같은 수).</summary>
    private static int[] Keys(IReadOnlyList<PlanStep> steps, Dictionary<string, int> keys)
    {
        var result = new int[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var text = string.Concat(s.Operation, "\u001F", s.Options ?? string.Empty, "\u001F", s.ObjectOwner ?? string.Empty, ".", s.ObjectName ?? string.Empty);
            if (!keys.TryGetValue(text, out var k))
            {
                k = keys.Count;
                keys[text] = k;
            }

            result[i] = k;
        }

        return result;
    }

    /// <summary>가운데 부분을 LCS 로 맞춘다. 표는 뒤에서부터 채워(뒤쪽 부분열 길이) 앞에서부터 따라간다.</summary>
    private static void AlignMiddle(List<PlanDiffRow> rows, IReadOnlyList<PlanStep> a, IReadOnlyList<PlanStep> b, int[] ka, int[] kb, int offset, int n, int m)
    {
        var width = m + 1;
        var table = new int[(n + 1) * width];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[(i * width) + j] = ka[offset + i] == kb[offset + j]
                    ? table[((i + 1) * width) + j + 1] + 1
                    : Math.Max(table[((i + 1) * width) + j], table[(i * width) + j + 1]);
            }
        }

        int x = 0, y = 0, gapA = 0, gapB = 0;
        while (x < n && y < m)
        {
            if (ka[offset + x] == kb[offset + y])
            {
                AddGap(rows, a, b, ka, kb, offset + gapA, offset + x, offset + gapB, offset + y);
                rows.Add(new PlanDiffRow(PlanDiffKind.Same, a[offset + x], b[offset + y]));
                x++;
                y++;
                gapA = x;
                gapB = y;
            }
            else if (table[((x + 1) * width) + y] >= table[(x * width) + y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        AddGap(rows, a, b, ka, kb, offset + gapA, offset + n, offset + gapB, offset + m);
    }

    /// <summary>짝 없는 구간: 자리 순서대로 짝지어 바뀜(열쇠가 같으면 같음), 남은 쪽은 빠짐·추가.</summary>
    private static void AddGap(List<PlanDiffRow> rows, IReadOnlyList<PlanStep> a, IReadOnlyList<PlanStep> b, int[] ka, int[] kb, int fromA, int toA, int fromB, int toB)
    {
        var pairs = Math.Min(toA - fromA, toB - fromB);
        for (var k = 0; k < pairs; k++)
        {
            var kind = ka[fromA + k] == kb[fromB + k] ? PlanDiffKind.Same : PlanDiffKind.Changed;
            rows.Add(new PlanDiffRow(kind, a[fromA + k], b[fromB + k]));
        }

        for (var i = fromA + pairs; i < toA; i++)
        {
            rows.Add(new PlanDiffRow(PlanDiffKind.Removed, a[i], null));
        }

        for (var j = fromB + pairs; j < toB; j++)
        {
            rows.Add(new PlanDiffRow(PlanDiffKind.Added, null, b[j]));
        }
    }
}
