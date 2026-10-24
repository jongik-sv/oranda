using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oranda.Core.Plans;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>그래프 노드 색 진하기의 기준.</summary>
public enum PlanGraphColorBasis
{
    /// <summary>자체 시간 비중(실제 계획). 시간이 없으면 비용.</summary>
    Time,

    /// <summary>자체 비용 비중.</summary>
    Cost,
}

/// <summary>그래프 노드 하나(논리 좌표, 확대 전).</summary>
public sealed record PlanGraphNode(int Index, int StepId, double X, double Y, double Width, double Height)
{
    /// <summary>첫 줄: 「Id 연산」(위험이면 앞에 ⚠).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>둘째 줄: 객체·시간·비중.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>색 진하기(0~1) — 자체 시간(또는 비용) 비중.</summary>
    public double Intensity { get; init; }

    public bool IsHottest { get; init; }

    public bool IsRisky { get; init; }

    /// <summary>접힌 하위 단계 수(0 이면 펼침).</summary>
    public int HiddenCount { get; init; }

    public double CenterX => X + Width / 2;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>부모 → 자식 직각 간선(점 4개: 부모 아래 → 꺾임 → 꺾임 → 자식 위).</summary>
public sealed record PlanGraphEdge(int ParentIndex, int ChildIndex, IReadOnlyList<(double X, double Y)> Points);

/// <summary>
/// 계획 그래프 배치(TSK-06-04): 위 → 아래 트리, 자식은 가로로 펼친다. 노드 너비 = 자체 비용 비중(글이 들어갈 만큼은 늘 확보),
/// 색 진하기 = 자체 시간 비중(없으면 비용). 단계가 <see cref="CollapseLimit"/> 를 넘으면 처음엔 깊은 하위 트리를 접는다(+n).
/// 순수 계산이라 화면 없이 시험한다.
/// </summary>
public sealed class PlanGraphLayout
{
    public const double NodeHeight = 36;
    public const double MinNodeWidth = 92;
    public const double MaxNodeWidth = 240;
    public const double HorizontalGap = 16;
    public const double VerticalGap = 28;
    public const double Margin = 20;
    public const int CollapseLimit = 60;

    /// <summary>글자 폭 어림(DIP/글자). 한글·영문 섞인 작은 글꼴 기준.</summary>
    public const double CharWidth = 7.4;

    private PlanGraphLayout(IReadOnlyList<PlanGraphNode> nodes, IReadOnlyList<PlanGraphEdge> edges, double width, double height)
    {
        Nodes = nodes;
        Edges = edges;
        Width = width;
        Height = height;
    }

    public static PlanGraphLayout Empty { get; } = new([], [], 0, 0);

    public IReadOnlyList<PlanGraphNode> Nodes { get; }

    public IReadOnlyList<PlanGraphEdge> Edges { get; }

    /// <summary>전체 너비(여백 포함).</summary>
    public double Width { get; }

    public double Height { get; }

    /// <summary>점에 있는 노드(없으면 null).</summary>
    public PlanGraphNode? HitTest(double x, double y) => Nodes.FirstOrDefault(n => n.Contains(x, y));

    /// <summary>
    /// 처음 접을 단계(Id). 너비 우선으로 깊이 단위로 넣다가 <paramref name="limit"/> 를 넘기 전 깊이에서 멈추고,
    /// 그 깊이의 자식 있는 단계를 접는다. 깊이 0·1 은 늘 보인다. 넘지 않으면 빈 집합.
    /// </summary>
    public static HashSet<int> InitialCollapsed(IReadOnlyList<PlanStep> steps, int limit = CollapseLimit)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var result = new HashSet<int>();
        if (steps.Count <= limit)
        {
            return result;
        }

        var (_, children, roots) = Tree(steps);
        var level = roots;
        var shown = 0;
        var depth = 0;
        while (level.Count > 0)
        {
            var next = level.SelectMany(i => children[i]).ToList();
            shown += level.Count;
            if (depth >= 1 && next.Count > 0 && shown + next.Count > limit)
            {
                foreach (var i in level.Where(i => children[i].Count > 0))
                {
                    result.Add(steps[i].Id);
                }

                break;
            }

            level = next;
            depth++;
        }

        return result;
    }

    public static PlanGraphLayout Compute(
        IReadOnlyList<PlanStep> steps, PlanMetrics metrics, PlanGraphColorBasis basis, IReadOnlySet<int>? collapsed = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(metrics);
        if (steps.Count == 0)
        {
            return Empty;
        }

        var (_, children, roots) = Tree(steps);
        var byTime = basis == PlanGraphColorBasis.Time && metrics.Actual == PlanActualMetric.Time;
        var n = steps.Count;
        var widths = new double[n];
        var titles = new string[n];
        var details = new string[n];
        for (var i = 0; i < n; i++)
        {
            var s = steps[i];
            var m = metrics.Steps[i];
            titles[i] = (s.Risk != PlanRisk.None ? "⚠ " : string.Empty) + s.Id.ToString(CultureInfo.InvariantCulture) + " " + ShortOperation(s);
            details[i] = Detail(s, m, metrics);
            var text = Math.Max(titles[i].Length, details[i].Length) * CharWidth + 20;
            var byCost = MinNodeWidth + (MaxNodeWidth - MinNodeWidth) * Math.Clamp(m.SelfCostShare, 0, 1);
            widths[i] = Math.Clamp(Math.Max(text, byCost), MinNodeWidth, MaxNodeWidth + 60);
        }

        // 접힌 노드의 자손은 빼고 「+n」 을 단다.
        var hidden = new int[n];
        var visibleChildren = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            var isCollapsed = collapsed is not null && collapsed.Contains(steps[i].Id) && children[i].Count > 0;
            visibleChildren[i] = isCollapsed ? [] : children[i];
            if (isCollapsed)
            {
                hidden[i] = CountDescendants(i, children);
            }
        }

        // 하위 트리 폭(후위) → 왼쪽부터 자리 매김(전위). 깊은 계획도 되게 재귀 없이 돈다.
        var subtree = new double[n];
        foreach (var i in PostOrder(roots, visibleChildren))
        {
            var kids = visibleChildren[i];
            var sum = kids.Count == 0 ? 0 : kids.Sum(k => subtree[k]) + HorizontalGap * (kids.Count - 1);
            subtree[i] = Math.Max(widths[i], sum);
        }

        var x = new double[n];
        var y = new double[n];
        var placed = new bool[n];
        var left = Margin;
        var stack = new Stack<(int Node, double Left, int Depth)>();
        foreach (var r in roots)
        {
            stack.Push((r, left, 0));
            while (stack.Count > 0)
            {
                var (node, l, d) = stack.Pop();
                placed[node] = true;
                x[node] = l + (subtree[node] - widths[node]) / 2;
                y[node] = Margin + d * (NodeHeight + VerticalGap);
                var kids = visibleChildren[node];
                var sum = kids.Count == 0 ? 0 : kids.Sum(k => subtree[k]) + HorizontalGap * (kids.Count - 1);
                var cl = l + (subtree[node] - sum) / 2;
                var starts = new double[kids.Count];
                for (var k = 0; k < kids.Count; k++)
                {
                    starts[k] = cl;
                    cl += subtree[kids[k]] + HorizontalGap;
                }

                for (var k = kids.Count - 1; k >= 0; k--)
                {
                    stack.Push((kids[k], starts[k], d + 1));
                }
            }

            left += subtree[r] + HorizontalGap;
        }

        var nodes = new List<PlanGraphNode>();
        var map = new int[n];
        for (var i = 0; i < n; i++)
        {
            map[i] = -1;
            if (!placed[i])
            {
                continue;
            }

            var m = metrics.Steps[i];
            map[i] = nodes.Count;
            nodes.Add(new PlanGraphNode(i, steps[i].Id, x[i], y[i], widths[i], NodeHeight)
            {
                Title = titles[i],
                Detail = hidden[i] > 0 ? details[i] + $" · +{hidden[i]}" : details[i],
                Intensity = Math.Clamp(byTime ? m.SelfActualShare : m.SelfCostShare, 0, 1),
                IsHottest = i == metrics.HottestIndex,
                IsRisky = steps[i].Risk != PlanRisk.None,
                HiddenCount = hidden[i],
            });
        }

        var edges = new List<PlanGraphEdge>();
        foreach (var p in nodes)
        {
            foreach (var c in visibleChildren[p.Index])
            {
                if (map[c] < 0)
                {
                    continue;
                }

                var child = nodes[map[c]];
                var mid = p.Bottom + VerticalGap / 2;
                edges.Add(new PlanGraphEdge(map[p.Index], map[c], [(p.CenterX, p.Bottom), (p.CenterX, mid), (child.CenterX, mid), (child.CenterX, child.Y)]));
            }
        }

        var width = nodes.Max(nd => nd.X + nd.Width) + Margin;
        var height = nodes.Max(nd => nd.Bottom) + Margin;
        return new PlanGraphLayout(nodes, edges, width, height);
    }

    /// <summary>노드 첫 줄 연산(30자 넘으면 끝을 자른다).</summary>
    internal static string ShortOperation(PlanStep step)
    {
        var name = PlanRowItem.OperationName(step);
        return name.Length > 30 ? name[..29] + "…" : name;
    }

    private static string Detail(PlanStep s, PlanStepMetrics m, PlanMetrics metrics)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(s.ObjectName))
        {
            parts.Add(s.ObjectName.Length > 22 ? s.ObjectName[..21] + "…" : s.ObjectName);
        }

        if (metrics.Actual == PlanActualMetric.Time && m.SelfTime is { } t)
        {
            parts.Add("자체 " + t.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            parts.Add(PlanRowItem.Percent(m.SelfActualShare));
        }
        else if (s.Cost is not null)
        {
            parts.Add("비용 " + PlanRowItem.Fmt(s.Cost));
            parts.Add(PlanRowItem.Percent(m.SelfCostShare));
        }

        return string.Join(" · ", parts);
    }

    private static int CountDescendants(int i, List<int>[] children)
    {
        var count = 0;
        var stack = new Stack<int>(children[i]);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            count++;
            foreach (var g in children[c])
            {
                stack.Push(g);
            }
        }

        return count;
    }

    private static List<int> PostOrder(List<int> roots, List<int>[] children)
    {
        var order = new List<int>();
        var stack = new Stack<(int Node, bool Done)>();
        foreach (var r in Enumerable.Reverse(roots))
        {
            stack.Push((r, false));
        }

        while (stack.Count > 0)
        {
            var (node, done) = stack.Pop();
            if (done)
            {
                order.Add(node);
                continue;
            }

            stack.Push((node, true));
            for (var k = children[node].Count - 1; k >= 0; k--)
            {
                stack.Push((children[node][k], false));
            }
        }

        return order;
    }

    /// <summary>부모 위치·자식 목록(Id 순)·뿌리. 부모가 없거나 고리면 뿌리로 본다.</summary>
    private static (int[] Parent, List<int>[] Children, List<int> Roots) Tree(IReadOnlyList<PlanStep> steps)
    {
        var n = steps.Count;
        var index = new Dictionary<int, int>(n);
        for (var i = 0; i < n; i++)
        {
            index.TryAdd(steps[i].Id, i);
        }

        var parent = new int[n];
        var children = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            children[i] = [];
        }

        for (var i = 0; i < n; i++)
        {
            // 앞 단계만 부모로 인정한다(Id 순 = 전위 순서). 고리·자기 참조를 막는다.
            parent[i] = steps[i].ParentId is { } p && index.TryGetValue(p, out var pi) && pi < i ? pi : -1;
            if (parent[i] >= 0)
            {
                children[parent[i]].Add(i);
            }
        }

        var roots = Enumerable.Range(0, n).Where(i => parent[i] < 0).ToList();
        return (parent, children, roots);
    }
}
