using Oranda.App.Features.ExecutionPlan;
using Oranda.Core.Plans;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>TSK-06-04 계획 그래프 배치: 위→아래 트리, 겹치지 않는 노드, 직각 간선, 비용 너비·시간 진하기, 60 단계 넘으면 접힘.</summary>
public class PlanGraphLayoutTests
{
    private static PlanGraphLayout Layout(IReadOnlyList<PlanStep> steps, PlanGraphColorBasis basis = PlanGraphColorBasis.Cost, IReadOnlySet<int>? collapsed = null) =>
        PlanGraphLayout.Compute(steps, PlanMetrics.Compute(steps), basis, collapsed);

    [Fact]
    public void Empty_plan_has_no_nodes()
    {
        var g = Layout([]);
        Assert.Empty(g.Nodes);
        Assert.Same(PlanGraphLayout.Empty, g);
        Assert.Null(g.HitTest(10, 10));
    }

    [Fact]
    public void Children_sit_one_level_below_their_parent_without_overlap_and_edges_are_orthogonal()
    {
        var steps = PlanBoostTests.WithActuals("x").Steps.Concat(
        [
            new PlanStep(3, 1, 2, "INDEX") { Options = "UNIQUE SCAN", ObjectName = "PK", Cost = 1 },
            new PlanStep(4, 0, 1, "FILTER") { Cost = 0 },
        ]).ToList();
        var g = Layout(steps);
        Assert.Equal(steps.Count, g.Nodes.Count);
        Assert.Equal(steps.Count - 1, g.Edges.Count);

        foreach (var e in g.Edges)
        {
            var p = g.Nodes[e.ParentIndex];
            var c = g.Nodes[e.ChildIndex];
            Assert.Equal(p.Y + PlanGraphLayout.NodeHeight + PlanGraphLayout.VerticalGap, c.Y, 3);
            Assert.Equal(4, e.Points.Count);
            Assert.Equal(e.Points[0].X, e.Points[1].X); // 세로
            Assert.Equal(e.Points[1].Y, e.Points[2].Y); // 가로
            Assert.Equal(e.Points[2].X, e.Points[3].X); // 세로
            Assert.Equal(c.CenterX, e.Points[3].X, 3);
            Assert.Equal(p.CenterX, e.Points[0].X, 3);
        }

        foreach (var row in g.Nodes.GroupBy(n => n.Y))
        {
            var ordered = row.OrderBy(n => n.X).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                Assert.True(ordered[i].X >= ordered[i - 1].X + ordered[i - 1].Width + PlanGraphLayout.HorizontalGap - 0.01, "같은 줄 노드가 겹친다");
            }
        }

        Assert.All(g.Nodes, n => Assert.True(n.X >= PlanGraphLayout.Margin - 0.01 && n.X + n.Width <= g.Width));
        Assert.Equal(g.Nodes.Max(n => n.Bottom) + PlanGraphLayout.Margin, g.Height, 3);
    }

    [Fact]
    public void Width_follows_self_cost_and_the_hottest_and_risky_steps_are_marked()
    {
        var g = Layout(PlanMetricsTests.HashJoin());
        var b = g.Nodes.Single(n => n.StepId == 3); // 자체 비용 60
        var a = g.Nodes.Single(n => n.StepId == 2); // 자체 비용 30
        Assert.True(b.Width > a.Width);
        Assert.True(b.IsHottest);
        Assert.Single(g.Nodes, n => n.IsHottest);
        Assert.True(a.IsRisky);
        Assert.StartsWith("⚠ 2 TABLE ACCESS FULL", a.Title, StringComparison.Ordinal);
        Assert.Contains("비용 30", a.Detail, StringComparison.Ordinal);
        Assert.All(g.Nodes, n => Assert.InRange(n.Width, PlanGraphLayout.MinNodeWidth, PlanGraphLayout.MaxNodeWidth + 60));
        Assert.All(g.Nodes, n => Assert.InRange(n.Intensity, 0, 1));
    }

    [Fact]
    public void Color_intensity_uses_time_when_asked_and_present_otherwise_cost()
    {
        var steps = PlanBoostTests.WithActuals("x").Steps;
        var byTime = Layout(steps, PlanGraphColorBasis.Time);
        var byCost = Layout(steps, PlanGraphColorBasis.Cost);
        var full = byTime.Nodes.Single(n => n.StepId == 2);
        Assert.Contains("자체 1.60 s", full.Detail, StringComparison.Ordinal);
        Assert.NotEqual(full.Intensity, byCost.Nodes.Single(n => n.StepId == 2).Intensity);

        // 시간이 없는 계획은 시간 기준이어도 비용으로 칠한다.
        var estimated = PlanMetricsTests.HashJoin();
        Assert.Equal(
            Layout(estimated, PlanGraphColorBasis.Cost).Nodes.Select(n => n.Intensity),
            Layout(estimated, PlanGraphColorBasis.Time).Nodes.Select(n => n.Intensity));
    }

    [Fact]
    public void Hit_test_finds_the_node_under_a_point()
    {
        var g = Layout(PlanMetricsTests.HashJoin());
        var n = g.Nodes[2];
        Assert.Same(n, g.HitTest(n.X + 2, n.Y + 2));
        Assert.Null(g.HitTest(n.X - 1, n.Y - 1));
    }

    /// <summary>뿌리 아래 넓고 깊은 계획(단계 <paramref name="count"/> 개).</summary>
    private static List<PlanStep> Wide(int count)
    {
        var steps = new List<PlanStep> { new(0, null, 0, "SELECT STATEMENT") { Cost = 100 } };
        var id = 1;
        // 깊이 1: 10개, 그 아래 각각 사슬
        var level1 = new List<int>();
        for (var i = 0; i < 10 && id < count; i++, id++)
        {
            steps.Add(new PlanStep(id, 0, 1, "NESTED LOOPS") { Cost = 10 });
            level1.Add(id);
        }

        var parents = level1.ToList();
        var depth = 2;
        while (id < count)
        {
            var next = new List<int>();
            foreach (var p in parents)
            {
                if (id >= count)
                {
                    break;
                }

                steps.Add(new PlanStep(id, p, depth, "TABLE ACCESS") { Options = "BY INDEX ROWID", Cost = 1 });
                next.Add(id++);
            }

            parents = next;
            depth++;
        }

        return [.. steps.OrderBy(s => s.Id)];
    }

    [Fact]
    public void Plans_up_to_sixty_steps_are_not_collapsed()
    {
        Assert.Empty(PlanGraphLayout.InitialCollapsed(Wide(60)));
        Assert.Empty(PlanGraphLayout.InitialCollapsed(PlanMetricsTests.HashJoin()));
    }

    [Fact]
    public void Bigger_plans_collapse_deep_subtrees_and_show_plus_n_until_expanded()
    {
        var steps = Wide(120);
        var collapsed = PlanGraphLayout.InitialCollapsed(steps);
        Assert.NotEmpty(collapsed);
        var g = Layout(steps, collapsed: collapsed);
        Assert.True(g.Nodes.Count <= PlanGraphLayout.CollapseLimit, $"보이는 노드 {g.Nodes.Count}");
        Assert.True(g.Nodes.Count >= 11); // 뿌리와 깊이 1 은 늘 보인다
        Assert.Equal(steps.Count, g.Nodes.Count + g.Nodes.Sum(n => n.HiddenCount));
        var folded = g.Nodes.First(n => n.HiddenCount > 0);
        Assert.Contains("+" + folded.HiddenCount, folded.Detail, StringComparison.Ordinal);

        var all = Layout(steps);
        Assert.Equal(steps.Count, all.Nodes.Count);
    }

    [Fact]
    public void Broken_parent_links_do_not_loop_and_become_roots()
    {
        PlanStep[] steps =
        [
            new(0, 2, 0, "SELECT STATEMENT") { Cost = 5 }, // 뒤 단계를 부모로(고리)
            new(1, 0, 1, "TABLE ACCESS") { Options = "FULL", Cost = 5 },
            new(2, 1, 2, "INDEX") { Cost = 1 },
            new(3, 99, 0, "ORPHAN") { Cost = 1 },
        ];
        var g = Layout(steps);
        Assert.Equal(4, g.Nodes.Count);
        Assert.Equal(2, g.Edges.Count);
    }
}
