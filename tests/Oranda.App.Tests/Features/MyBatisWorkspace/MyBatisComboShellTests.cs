using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Shell;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 실제 셸(메인 창·도킹)에서 「조합 검사」 명령. 검사할 때마다 도킹 배치를 새로 지으면 메인 창 전체가 다시 그려져 깜박였다(2026-10-24 사용자 신고):
/// 창이 이미 보이면 배치를 그대로 두고 그 탭만 앞으로 가져온다.
/// </summary>
public sealed class MyBatisComboShellTests
{
    [Fact]
    public Task Checking_again_with_the_panel_visible_keeps_the_dock_layout_and_the_panel_view() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var (ws, session) = await rig.OpenAsync(path);
        var docking = rig.Get<DockWorkspace>();
        docking.Activate(docking.OpenDocument("oranda.panel.sqlEditor", ws)!);
        session.SelectStatement("updateEmpSal");

        // 창을 먼저 그려 둔다(명령은 가장 최근에 만든 창 ViewModel 을 쓴다 — 앞 시험이 만든 것이 남아 있을 수 있다).
        docking.ActivateTool("oranda.panel.mybatisCombos");
        MyBatisFlowRig.Pump();
        rig.Harness.Window.UpdateLayout();
        MyBatisFlowRig.Pump();
        var handler = new MyBatisComboCommands(rig.Harness.Services);
        var context = new CommandContext("oranda.mybatis.checkCombinations", FakeContextKeys.AllTrue);

        await handler.ExecuteAsync(context, default);
        MyBatisFlowRig.Pump();
        var layout = docking.Layout;
        var view = Assert.Single(rig.Harness.Window.GetVisualDescendants().OfType<MyBatisCombosView>());
        Assert.Equal(4, ((MyBatisCombosViewModel)view.DataContext!).Rows.Count);

        await handler.ExecuteAsync(context, default);
        MyBatisFlowRig.Pump();

        Assert.Same(layout, docking.Layout);
        Assert.Same(view, Assert.Single(rig.Harness.Window.GetVisualDescendants().OfType<MyBatisCombosView>()));
    });
}
