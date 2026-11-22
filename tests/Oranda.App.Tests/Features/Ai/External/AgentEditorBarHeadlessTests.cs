using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>편집기 아래 밖의 에이전트 띠(agent-cli-control §6.1)를 SQL 편집기 화면에 그린다: 머리 글·대기 수·단추, 버리면 다음 제안.</summary>
public class AgentEditorBarHeadlessTests
{
    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task Sql_editor_shows_the_agent_bar_with_queue_and_buttons_and_hides_it_when_done() => HeadlessHost.Run(() =>
    {
        var host = new SqlEditorHost(dialogs: new NoDialogs());
        var tab = host.NewDocument("SELECT 1 FROM dual");
        var service = new ExternalEditorService { HostSource = () => host };
        var first = service.Propose(OpenTabAccess.Instance, new ExternalProposalRequest(tab.TabId, tab.Version, ExternalRange.Document, "SELECT 2 FROM dual")
        {
            Title = "조건 추가",
            Reason = "상태 조건이 빠졌습니다",
            Client = "Claude Code",
        }).Value!;
        service.Propose(OpenTabAccess.Instance, new ExternalProposalRequest(tab.TabId, tab.Version, ExternalRange.Document, "SELECT 3 FROM dual") { Client = "Codex" });

        var view = new SqlEditorView { DataContext = tab };
        var window = new Window { Width = 800, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Claude Code 가 편집을 제안했습니다 · 조건 추가", Find<TextBlock>(view, "AgentBarHeadline").Text);
        Assert.Equal("상태 조건이 빠졌습니다", Find<TextBlock>(view, "AgentBarDetail").Text);
        Assert.True(Find<TextBlock>(view, "AgentBarQueue").IsVisible);
        Assert.Equal("대기 1", Find<TextBlock>(view, "AgentBarQueue").Text);
        Assert.True(Find<Button>(view, "AgentBarCompare").IsVisible);
        Assert.Equal("받아들이기", Find<Button>(view, "AgentBarAccept").Content);
        Assert.Equal("버리기", Find<Button>(view, "AgentBarDiscard").Content);

        Find<Button>(view, "AgentBarDiscard").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ExternalProposalStatus.Rejected, service.GetProposal(first.ProposalId).Value!.Status);
        Assert.Equal("Codex 가 편집을 제안했습니다", Find<TextBlock>(view, "AgentBarHeadline").Text);
        Assert.False(Find<TextBlock>(view, "AgentBarQueue").IsVisible);

        Find<Button>(view, "AgentBarDiscard").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.GetVisualDescendants().OfType<AgentEditorBar>().Single().IsVisible);
        Assert.Equal("SELECT 1 FROM dual", tab.Text);
        window.Close();
    });
}
