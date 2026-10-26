using Oranda.App.Features.Sessions;
using Oranda.App.Shell.Layout;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Sessions;

// 소유: TSK-14-04. 「세션」 도구 창이 보이고 숨는 규칙(순수 논리)과 기본 배치.
public sealed class SessionsVisibilityTests
{
    /// <summary>도킹을 흉내 내는 상태: 보임 여부와 보이기·숨기기 횟수.</summary>
    private sealed class Fake
    {
        public bool Visible { get; set; }

        public int Shown { get; private set; }

        public int Hidden { get; private set; }

        public SessionsVisibility Rule { get; }

        public Fake()
        {
            Rule = new SessionsVisibility(() => Visible, () => { Visible = true; Shown++; }, () => { Visible = false; Hidden++; });
        }

        /// <summary>사용자가 창을 닫았다(배치 바뀜이 따라온다).</summary>
        public void UserCloses()
        {
            Visible = false;
            Rule.LayoutChanged();
        }
    }

    [Fact]
    public void One_session_keeps_the_window_hidden_and_two_show_it_once()
    {
        var f = new Fake();
        f.Rule.Update(0);
        f.Rule.Update(1);
        Assert.False(f.Visible);
        Assert.Equal(0, f.Shown);

        f.Rule.Update(2);
        Assert.True(f.Visible);
        f.Rule.Update(3);
        f.Rule.Update(2);
        Assert.Equal(1, f.Shown); // 이미 보이는 창을 다시 보이지 않는다
    }

    [Fact]
    public void Dropping_to_one_session_hides_the_window_that_was_shown_automatically()
    {
        var f = new Fake();
        f.Rule.Update(2);
        f.Rule.Update(1);

        Assert.False(f.Visible);
        Assert.Equal(1, f.Hidden);
        Assert.False(f.Rule.AutoShown);
    }

    [Fact]
    public void A_window_the_user_opened_by_hand_is_not_hidden_when_sessions_drop()
    {
        var f = new Fake { Visible = true };
        f.Rule.Update(1);
        f.Rule.Update(2); // 이미 보인다: 우리가 보인 게 아니다
        f.Rule.Update(1);

        Assert.True(f.Visible);
        Assert.Equal(0, f.Shown);
        Assert.Equal(0, f.Hidden);
    }

    [Fact]
    public void A_window_the_user_closes_is_not_opened_again_behind_their_back()
    {
        var f = new Fake();
        f.Rule.Update(2);
        f.UserCloses();
        Assert.True(f.Rule.Dismissed);

        f.Rule.Update(3);
        f.Rule.Update(1);
        f.Rule.Update(2); // 다시 둘이 돼도 닫은 창을 몰래 열지 않는다
        Assert.False(f.Visible);
        Assert.Equal(1, f.Shown);
    }

    [Fact]
    public void Closing_the_window_after_sessions_dropped_is_not_taken_as_a_dismissal()
    {
        var f = new Fake();
        f.Rule.Update(2);
        f.Rule.Update(1); // 우리가 숨겼다
        f.Rule.LayoutChanged();
        Assert.False(f.Rule.Dismissed);

        f.Rule.Update(2); // 다시 둘이 되면 다시 보인다
        Assert.True(f.Visible);
        Assert.Equal(2, f.Shown);
    }

    // ---- 기본 배치 ----------------------------------------------------------------------------

    private static ToolPanelDefinition Tool(string name, DockLocation at, int order) => new()
    {
        Id = "oranda.panel." + name,
        Title = "Panel." + name,
        Kind = ToolPanelKind.Tool,
        DefaultLocation = at,
        ViewModelType = typeof(object),
        Order = order,
    };

    private static readonly ToolPanelDefinition[] Panels =
    [
        Tool("schemaBrowser", DockLocation.Left, 10),
        Tool("scripts", DockLocation.Left, 20),
        Tool("sessions", DockLocation.Left, 30),
        Tool("results", DockLocation.Bottom, 5),
    ];

    private const string Sessions = "oranda.panel.sessions";

    [Fact]
    public void The_default_layout_keeps_the_sessions_window_closed()
    {
        var layout = LayoutPlanner.CreateDefault(Panels);

        Assert.DoesNotContain(Sessions, LayoutPlanner.ToolIds(layout.Root));
        Assert.Contains(Sessions, layout.HiddenTools!);
        Assert.Equal(["oranda.panel.schemaBrowser", "oranda.panel.scripts"], LayoutPlanner.ToolIds(layout.Root).Where(id => id.Contains("schema") || id.Contains("scripts")));
    }

    [Fact]
    public void A_saved_layout_from_before_the_window_existed_does_not_open_it_either()
    {
        var saved = LayoutPlanner.CreateDefault(Panels.Where(p => p.Id != Sessions));

        var layout = LayoutPlanner.Reconcile(saved, Panels);

        Assert.DoesNotContain(Sessions, LayoutPlanner.ToolIds(layout.Root));
        Assert.Contains(Sessions, layout.HiddenTools!);
    }

    [Fact]
    public void Showing_the_window_adds_it_to_the_left_group_and_hiding_it_closes_it_again()
    {
        var layout = LayoutPlanner.Reconcile(LayoutPlanner.CreateDefault(Panels), Panels);
        var shown = LayoutPlanner.ShowTool(layout, Panels.Single(p => p.Id == Sessions));

        var root = shown.Root!;
        Assert.Contains(Sessions, LayoutPlanner.ToolIds(root));
        Assert.DoesNotContain(Sessions, shown.HiddenTools!);
        var group = Flatten(root).Single(n => n.Kind == DockNodeKind.ToolGroup && (n.Tools ?? []).Contains(Sessions));
        Assert.Equal(DockLocation.Left, LayoutPlanner.LocationOf(root, group));
        Assert.Contains("oranda.panel.schemaBrowser", group.Tools!); // 스키마 브라우저·스크립트와 같은 묶음의 탭

        var hidden = LayoutPlanner.HideTool(shown, Sessions);
        Assert.DoesNotContain(Sessions, LayoutPlanner.ToolIds(hidden.Root));
        Assert.Contains(Sessions, hidden.HiddenTools!);
    }

    private static IEnumerable<DockNode> Flatten(DockNode node) => new[] { node }.Concat((node.Children ?? []).SelectMany(Flatten));
}
