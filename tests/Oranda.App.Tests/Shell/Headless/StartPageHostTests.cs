using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Shell.Headless;

// 문서가 없을 때 문서 영역의 시작 화면. 문서 영역 템플릿이 다시 만들어져도(메인 창 내용을 떼었다 다시 붙임) 같은 시작 화면 Control 을
// 두 호스트에 붙이지 않는다(「already has a visual parent」 — 2026-10-24 폴더 트리 크래시와 같은 종류).
public class StartPageHostTests
{
    [Fact]
    public Task Re_templating_the_document_area_builds_a_new_start_page_instead_of_sharing_one() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        h.Window.UpdateLayout();
        var before = Assert.Single(h.Window.GetVisualDescendants().OfType<StartPage>());

        var content = (Control)h.Window.Content!;
        h.Window.Content = null;
        Dispatcher.UIThread.RunJobs();
        h.Window.Content = new Border { Child = content };
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();

        var after = Assert.Single(h.Window.GetVisualDescendants().OfType<StartPage>());
        Assert.True(after.IsEffectivelyVisible);
        Assert.NotSame(before, after); // 새 호스트가 자기 시작 화면을 만들었다(옛 것은 옛 호스트와 함께 버려진다)
    });

    // 회귀 방지: 문서가 없는 메인 창의 문서 영역에 시작 화면 하나(수정 전에도 통과한다).
    [Fact]
    public Task The_main_window_without_documents_shows_one_start_page_in_the_document_area() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        h.Window.UpdateLayout();

        var page = Assert.Single(h.Window.GetVisualDescendants().OfType<StartPage>());
        Assert.True(page.IsEffectivelyVisible);
        Assert.Contains(page.GetVisualAncestors(), a => a is Control { Name: "PART_EmptyContentHost" });
        MyBatisFlowRig.Render(h.Window, "startpage-main-empty", (int)h.Window.Bounds.Width, (int)h.Window.Bounds.Height);
    });

    // 시나리오 0개 문장의 시나리오 콤보: 선택 칸 내용이 null 이고 항목 템플릿(x:DataType)이 null 을 거부하면 위로 템플릿을 찾는다.
    // 시작 화면 템플릿이 null 을 맞다고 보면 콤보 안에 시작 화면(28pt 「Oranda」)이 그려졌다(2026-10-24 사용자 신고).
    [Fact]
    public Task A_scenario_combo_with_no_selection_never_draws_the_start_page() => HeadlessHost.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var window = rig.Harness.Window;
        var docking = rig.Get<DockWorkspace>();
        var (ws, session) = await rig.OpenAsync(rig.Write("EmpMapper.xml", MapperSamples.EmpMapper));
        docking.Activate(docking.OpenDocument("oranda.panel.sqlEditor", ws)!);
        session.OpenStatement("updateEmpSal");
        rig.Hub.Refresh();
        MyBatisFlowRig.Pump();
        window.UpdateLayout();

        var combo = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ScenarioCombo" && c.IsEffectivelyVisible);
        Assert.Null(combo.SelectedItem);
        Assert.Empty(combo.GetVisualDescendants().OfType<StartPage>());
        Assert.False(StartPage.HostTemplate(() => throw new InvalidOperationException()).Match(null));
        Assert.All(window.GetVisualDescendants().OfType<StartPage>(),
            p => Assert.Contains(p.GetVisualAncestors(), a => a is Control { Name: "PART_EmptyContentHost" }));
        MyBatisFlowRig.Render(window, "startpage-scenario-combo", (int)window.Bounds.Width, (int)window.Bounds.Height);
    });
}
