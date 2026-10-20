using System;
using System.Collections.Generic;
using Oranda.Core.Plans;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>실제 비중 막대가 무엇을 재는지.</summary>
public enum PlanActualMetric
{
    /// <summary>실제 통계 없음(예상 계획).</summary>
    None,

    /// <summary>A-Time(누적 경과 시간).</summary>
    Time,

    /// <summary>A-Time 이 없을 때 A-Rows.</summary>
    Rows,
}

/// <summary>계획 한 단계의 비중·실행 순서. 값은 계획을 받을 때 한 번만 구한다.</summary>
/// <param name="ExecutionOrder">실행 순서(1부터). 자식이 부모보다 먼저, 형제는 Id 순.</param>
/// <param name="SelfCost">자체 비용 = 누적 비용 − 바로 아래 자식들의 누적 비용 합(0 미만은 0). 비용이 없으면 null.</param>
/// <param name="CostShare">누적 비용 ÷ 계획 전체 비용(0~1).</param>
/// <param name="SelfCostShare">자체 비용 ÷ 계획 전체 비용(0~1).</param>
/// <param name="SelfTime">자체 시간 = 누적 A-Time − 자식들의 누적 A-Time 합(0 미만은 0).</param>
/// <param name="ActualShare">시간이면 누적 A-Time ÷ 전체, 행 수면 A-Rows ÷ 가장 많은 단계의 A-Rows(0~1).</param>
/// <param name="SelfActualShare">시간이면 자체 시간 ÷ 전체, 행 수면 <paramref name="ActualShare"/> 와 같다(행 수는 누적이 아니다).</param>
public readonly record struct PlanStepMetrics(
    int ExecutionOrder,
    long? SelfCost,
    double CostShare,
    double SelfCostShare,
    TimeSpan? SelfTime,
    double ActualShare,
    double SelfActualShare);

/// <summary>
/// 계획 트리의 비용·시간 비중과 실행 순서, 가장 비싼 단계를 구한다(PLAN-03 보강).
/// <para>Oracle 의 COST·A-Time 은 자식을 포함한 누적 값이라 가장 큰 값은 늘 맨 위(Id 0)다. 그래서 「가장 비싼 단계」는
/// 자체 값(누적 − 바로 아래 자식 누적 합)으로 고른다: 실제 시간이 있으면 자체 시간, 없으면 자체 비용.</para>
/// <para>실행 순서는 Oracle 의 보통 읽는 법(자식 먼저, 형제는 Id 순 = 후위 순회)이다. FILTER 의 하위 질의·스칼라 하위 질의처럼
/// 실제 호출 순서가 다른 경우는 따로 다루지 않는다.</para>
/// </summary>
public sealed class PlanMetrics
{
    private PlanMetrics(PlanStepMetrics[] steps, PlanActualMetric actual, int hottest, long? totalCost)
    {
        Steps = steps;
        Actual = actual;
        HottestIndex = hottest;
        TotalCost = totalCost;
    }

    /// <summary>단계별 값(입력 단계와 같은 순서).</summary>
    public IReadOnlyList<PlanStepMetrics> Steps { get; }

    /// <summary>실제 비중 막대의 기준.</summary>
    public PlanActualMetric Actual { get; }

    /// <summary>가장 비싼 단계의 위치(없으면 -1). 단계가 하나뿐이면 표시하지 않는다.</summary>
    public int HottestIndex { get; }

    /// <summary>계획 전체 비용(단계 비용 중 가장 큰 값 = 보통 Id 0). 비용이 하나도 없으면 null.</summary>
    public long? TotalCost { get; }

    public static PlanMetrics Compute(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var n = steps.Count;
        var index = new Dictionary<int, int>(n);
        for (var i = 0; i < n; i++)
        {
            index.TryAdd(steps[i].Id, i);
        }

        // 부모 위치(없거나 자기 자신이면 뿌리).
        var parent = new int[n];
        var childCount = new int[n];
        var childCost = new long[n];
        var childTicks = new long[n];
        long maxCost = 0;
        long maxTicks = 0;
        long maxRows = 0;
        bool anyCost = false, anyTime = false, anyRows = false;
        for (var i = 0; i < n; i++)
        {
            var s = steps[i];
            parent[i] = s.ParentId is { } p && index.TryGetValue(p, out var pi) && pi != i ? pi : -1;
            if (parent[i] >= 0)
            {
                childCount[parent[i]]++;
                childCost[parent[i]] += Math.Max(s.Cost ?? 0, 0);
                childTicks[parent[i]] += Math.Max(s.ActualTime?.Ticks ?? 0, 0);
            }

            if (s.Cost is { } c)
            {
                anyCost = true;
                maxCost = Math.Max(maxCost, c);
            }

            if (s.ActualTime is { } t)
            {
                anyTime = true;
                maxTicks = Math.Max(maxTicks, t.Ticks);
            }

            if (s.ActualRows is { } r)
            {
                anyRows = true;
                maxRows = Math.Max(maxRows, r);
            }
        }

        var actual = anyTime ? PlanActualMetric.Time : anyRows ? PlanActualMetric.Rows : PlanActualMetric.None;
        var order = ExecutionOrder(parent, childCount);
        var result = new PlanStepMetrics[n];
        var hottest = -1;
        long hottestValue = 0;
        for (var i = 0; i < n; i++)
        {
            var s = steps[i];
            long? selfCost = s.Cost is { } c ? Math.Max(c - childCost[i], 0) : null;
            TimeSpan? selfTime = s.ActualTime is { } t ? TimeSpan.FromTicks(Math.Max(t.Ticks - childTicks[i], 0)) : null;
            double actualShare = 0, selfActualShare = 0;
            if (actual == PlanActualMetric.Time && s.ActualTime is { } at)
            {
                actualShare = Share(at.Ticks, maxTicks);
                selfActualShare = Share(selfTime!.Value.Ticks, maxTicks);
            }
            else if (actual == PlanActualMetric.Rows && s.ActualRows is { } ar)
            {
                actualShare = Share(ar, maxRows);
                selfActualShare = actualShare;
            }

            result[i] = new PlanStepMetrics(
                order[i],
                selfCost,
                Share(s.Cost ?? 0, maxCost),
                Share(selfCost ?? 0, maxCost),
                selfTime,
                actualShare,
                selfActualShare);

            var weight = actual == PlanActualMetric.Time ? selfTime?.Ticks ?? 0 : selfCost ?? 0;
            if (weight > hottestValue)
            {
                hottestValue = weight;
                hottest = i;
            }
        }

        return new PlanMetrics(result, actual, n > 1 ? hottest : -1, anyCost ? maxCost : null);
    }

    private static double Share(long value, long total) => total <= 0 ? 0 : Math.Clamp((double)value / total, 0, 1);

    /// <summary>후위 순회(자식 먼저, 형제는 입력 순서 = Id 순)로 1부터 번호를 매긴다. 깊은 계획도 되도록 재귀 없이 돈다.</summary>
    private static int[] ExecutionOrder(int[] parent, int[] childCount)
    {
        var n = parent.Length;

        // 자식 목록을 한 배열에 모은다(CSR): start[p] .. start[p+1]-1 이 p 의 자식.
        var start = new int[n + 1];
        for (var i = 0; i < n; i++)
        {
            start[i + 1] = start[i] + childCount[i];
        }

        var fill = new int[n];
        var children = new int[Math.Max(start[n], 0)];
        for (var i = 0; i < n; i++)
        {
            if (parent[i] is var p && p >= 0)
            {
                children[start[p] + fill[p]++] = i;
            }
        }

        var order = new int[n];
        var visited = new bool[n];
        var next = 0;
        var stack = new Stack<(int Node, int Child)>();
        for (var root = 0; root < n; root++)
        {
            if (parent[root] >= 0 || visited[root])
            {
                continue;
            }

            Visit(root);
        }

        // 부모 사슬이 고리라 뿌리에서 닿지 않은 단계는 Id 순으로 뒤에 붙인다.
        for (var i = 0; i < n; i++)
        {
            if (!visited[i])
            {
                Visit(i);
            }
        }

        return order;

        void Visit(int root)
        {
            visited[root] = true;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (node, k) = stack.Pop();
                var first = start[node];
                var count = start[node + 1] - first;
                while (k < count && visited[children[first + k]])
                {
                    k++;
                }

                if (k < count)
                {
                    var child = children[first + k];
                    stack.Push((node, k + 1));
                    visited[child] = true;
                    stack.Push((child, 0));
                }
                else
                {
                    order[node] = ++next;
                }
            }
        }
    }
}
