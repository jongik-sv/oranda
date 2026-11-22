using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>편집기 아래 에이전트 띠는 앱 언어를 바꾸면 바로 다시 그린다(머리 글·둘째 줄·대기 수·단추 글). 에이전트가 보낸 제목·까닭은 그대로.</summary>
[Collection("UiLanguage")]
public sealed class AgentEditorBarLanguageTests
{
    private static (ExternalEditorService Service, SqlEditorViewModel Tab, SqlEditorHost Host) Create()
    {
        var host = new SqlEditorHost(dialogs: new NoDialogs());
        var tab = host.NewDocument("SELECT 1 FROM dual");
        return (new ExternalEditorService { HostSource = () => host }, tab, host);
    }

    [Fact]
    public void Switching_the_app_language_redraws_the_bar_at_once()
    {
        var previous = UiText.Current;
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        try
        {
            var (service, tab, _) = Create();
            service.Propose(OpenTabAccess.Instance, new ExternalProposalRequest(tab.TabId, tab.Version, ExternalRange.Document, "SELECT 2 FROM dual")
            {
                Title = "조건 추가",
                Client = "Claude Code",
            });
            service.Propose(OpenTabAccess.Instance, new ExternalProposalRequest(tab.TabId, tab.Version, ExternalRange.Document, "SELECT 3 FROM dual"));
            var bar = tab.AgentBar!;
            Assert.Equal("Claude Code 가 편집을 제안했습니다 · 조건 추가", bar.Headline);
            Assert.Equal("대기 1", bar.QueueText);
            var changed = new List<string?>();
            bar.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            text.SetLanguage("en");

            Assert.Contains(nameof(AgentEditorBarViewModel.Headline), changed);
            Assert.Contains(nameof(AgentEditorBarViewModel.AcceptLabel), changed);
            Assert.Equal("Claude Code proposed an edit · 조건 추가", bar.Headline);
            Assert.Equal("1 waiting", bar.QueueText);
            Assert.Equal("Accept", bar.AcceptLabel);
            Assert.Equal("Discard", bar.DiscardLabel);

            bar.DiscardCommand.Execute(null);
            Assert.Equal("External agent proposed an edit", bar.Headline); // 이름 없는 클라이언트도 지금 언어로

            text.SetLanguage("ko");
            Assert.Equal("외부 에이전트 가 편집을 제안했습니다", bar.Headline);

            // 항목이 없으면 언어를 듣지 않는다(닫힌 탭의 띠가 글자 입구에 붙어 남지 않게).
            bar.DiscardCommand.Execute(null);
            changed.Clear();
            text.SetLanguage("en");
            Assert.Empty(changed);
        }
        finally
        {
            UiText.Current = previous;
        }
    }

    [Fact]
    public void New_tab_notice_follows_the_language_too()
    {
        var previous = UiText.Current;
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        try
        {
            var (service, _, host) = Create();
            var made = service.NewTab(OpenTabAccess.Instance, "DELETE FROM sample_orders", client: "Codex").Value!;
            var bar = host.Tabs.Single(t => t.TabId == made.TabId).AgentBar!;
            Assert.Equal("AI 가 만든 변경문 — 검토 후 직접 실행", bar.Headline);
            Assert.Equal("Codex 가 이 탭을 만들었습니다", bar.Detail);

            text.SetLanguage("en");

            Assert.Equal("Change statements made by AI — review and run them yourself", bar.Headline);
            Assert.Equal("Codex made this tab", bar.Detail);
        }
        finally
        {
            UiText.Current = previous;
        }
    }
}
