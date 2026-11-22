using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>
/// 밖의 에이전트 편집기 도구 안쪽(agent-cli-control §5.3·§6.1): 제안 대기열·받아들이기(실행 취소 1번에 원상)·어긋남·읽기 전용·잠긴 탭·새 탭 한도·탭 읽기·저장 요청·바인드.
/// </summary>
public class ExternalEditorServiceTests
{
    private const string Original = "SELECT * FROM sample_orders WHERE status = 'A';\nSELECT 1 FROM dual;";

    private static readonly Guid DevProfile = Guid.NewGuid();
    private static readonly Guid ProdProfile = Guid.NewGuid();

    private sealed class FakeDialogs : IEditorDialogs
    {
        /// <summary>비교 창의 답: null = 거절(창 닫기), 그 밖 = 넣을 글. 기본 = 제안 그대로.</summary>
        public Func<string, string, string?> Preview { get; set; } = (_, after) => after;

        public List<(string Title, string Before, string After)> Previews { get; } = [];

        public List<(string Title, string Before, string After)> Comparisons { get; } = [];

        public string? SavePath { get; set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult(SavePath);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<string?> PreviewEditTextAsync(string title, string before, string after, CancellationToken ct)
        {
            Previews.Add((title, before, after));
            return Task.FromResult(Preview(before, after));
        }

        public Task ShowComparisonAsync(string title, string before, string after, CancellationToken ct)
        {
            Comparisons.Add((title, before, after));
            return Task.CompletedTask;
        }
    }

    /// <summary>개발 접속·접속 없는 탭만 연다. 운영 접속은 잠김. 바인드 값은 개발 접속만.</summary>
    private sealed class DevOnlyAccess : IExternalTabAccess
    {
        public bool CanReadText(Guid? connectionProfileId) => connectionProfileId is null || connectionProfileId == DevProfile;

        public bool CanReadBindValues(Guid? connectionProfileId) => connectionProfileId == DevProfile;
    }

    private static readonly IExternalTabAccess Access = new DevOnlyAccess();

    private static (ExternalEditorService Service, SqlEditorHost Host, FakeDialogs Dialogs, SqlEditorViewModel Tab) Create(string text = Original)
    {
        var dialogs = new FakeDialogs();
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument(text);
        tab.ConnectionProfileId = DevProfile;
        var service = new ExternalEditorService { HostSource = () => host };
        return (service, host, dialogs, tab);
    }

    private static ExternalProposalView Propose(ExternalEditorService service, SqlEditorViewModel tab, string newText, ExternalRange? range = null, long? baseVersion = null, string? title = null)
    {
        var result = service.Propose(Access, new ExternalProposalRequest(tab.TabId, baseVersion ?? tab.Version, range ?? ExternalRange.Document, newText) { Title = title, Client = "Claude Code" });
        Assert.True(result.IsOk, result.Message);
        return result.Value!;
    }

    // ---- 제안 ------------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_a_proposal_puts_the_text_in_and_one_undo_restores_the_original()
    {
        var (service, _, dialogs, tab) = Create();
        var view = Propose(service, tab, "SELECT 2 FROM dual;", title: "주문 상태 조건 추가");

        Assert.Equal(ExternalProposalStatus.Pending, view.Status);
        Assert.Equal(Original, tab.Text); // 띠만 섰고 글은 그대로
        var bar = Assert.IsType<AgentEditorBarViewModel>(tab.AgentBar);
        Assert.True(bar.IsVisible);
        Assert.Equal("Claude Code 가 편집을 제안했습니다 · 주문 상태 조건 추가", bar.Headline);
        Assert.True(bar.ShowsCompare);

        await bar.AcceptCommand.ExecuteAsync(null);

        Assert.Equal("SELECT 2 FROM dual;", tab.Text);
        Assert.Equal("주문 상태 조건 추가", Assert.Single(dialogs.Previews).Title); // 비교 창을 거쳤다
        var done = service.GetProposal(view.ProposalId).Value!;
        Assert.Equal(ExternalProposalStatus.Accepted, done.Status);
        Assert.Equal("accepted", done.StatusText);
        Assert.Equal(tab.Version, done.Version);
        Assert.Equal("SELECT 2 FROM dual;", done.FinalText);
        Assert.False(bar.IsVisible);

        tab.Document.UndoStack.Undo();
        Assert.Equal(Original, tab.Text);
    }

    [Fact]
    public async Task Hand_edits_in_the_comparison_window_come_back_as_modified_with_the_final_text()
    {
        var (service, _, dialogs, tab) = Create();
        dialogs.Preview = (_, after) => after + " -- 고침";
        var view = Propose(service, tab, "SELECT 2 FROM dual;");

        await tab.AgentBar!.AcceptCommand.ExecuteAsync(null);

        var done = (await service.WaitAsync(view.ProposalId, TimeSpan.Zero, CancellationToken.None)).Value!;
        Assert.Equal(ExternalProposalStatus.Modified, done.Status);
        Assert.Equal("SELECT 2 FROM dual; -- 고침", done.FinalText);
        Assert.Equal("SELECT 2 FROM dual; -- 고침", tab.Text);
    }

    [Fact]
    public void A_different_base_version_is_stale_at_once_without_a_bar()
    {
        var (service, _, _, tab) = Create();
        var view = Propose(service, tab, "x", baseVersion: tab.Version - 1);

        Assert.Equal(ExternalProposalStatus.Stale, view.Status);
        Assert.Equal(tab.Version, view.Version);
        Assert.Null(tab.AgentBar);
        Assert.Equal(ExternalProposalStatus.Stale, service.GetProposal(view.ProposalId).Value!.Status);
    }

    [Fact]
    public async Task Typing_while_a_proposal_waits_makes_it_stale_and_takes_it_off_the_bar()
    {
        var (service, _, _, tab) = Create();
        var view = Propose(service, tab, "SELECT 2 FROM dual;");
        var waiting = service.WaitAsync(view.ProposalId, TimeSpan.FromSeconds(30), CancellationToken.None);

        tab.Document.Insert(0, "-- ");
        Assert.Equal("-- " + Original, tab.Text);
        var version = tab.Version;

        var done = (await waiting).Value!;
        Assert.Equal(ExternalProposalStatus.Stale, done.Status);
        Assert.Equal(version, done.Version);
        Assert.False(tab.AgentBar!.IsVisible);
    }

    [Fact]
    public async Task Typing_outside_a_range_proposal_moves_the_range_and_accepting_still_replaces_the_same_text()
    {
        var (service, _, dialogs, tab) = Create();
        var start = Original.IndexOf("SELECT 1", StringComparison.Ordinal);
        var view = Propose(service, tab, "SELECT 2 FROM dual;", ExternalRange.Of(start, "SELECT 1 FROM dual;".Length));

        tab.Document.Insert(0, "-- 앞에 쓴 주석\n");                      // 범위 앞
        tab.Document.Insert(tab.Document.TextLength, "\n-- 뒤");          // 범위 뒤(끝 경계에 바로 붙음)
        Assert.Equal(ExternalProposalStatus.Pending, service.GetProposal(view.ProposalId).Value!.Status);
        Assert.True(tab.AgentBar!.IsVisible);

        await tab.AgentBar.CompareCommand.ExecuteAsync(null);
        Assert.Equal("SELECT 1 FROM dual;", dialogs.Comparisons.Single().Before);

        await tab.AgentBar.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(ExternalProposalStatus.Accepted, service.GetProposal(view.ProposalId).Value!.Status);
        Assert.Equal("-- 앞에 쓴 주석\nSELECT * FROM sample_orders WHERE status = 'A';\nSELECT 2 FROM dual;\n-- 뒤", tab.Text);
    }

    [Fact]
    public async Task Typing_at_the_start_edge_is_not_swallowed_by_the_range()
    {
        var (service, _, _, tab) = Create();
        var start = Original.IndexOf("SELECT 1", StringComparison.Ordinal);
        Propose(service, tab, "SELECT 2 FROM dual;", ExternalRange.Of(start, "SELECT 1 FROM dual;".Length));

        tab.Document.Insert(start, "/* x */ ");
        await tab.AgentBar!.AcceptCommand.ExecuteAsync(null);

        Assert.EndsWith("\n/* x */ SELECT 2 FROM dual;", tab.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_inside_a_range_proposal_makes_it_stale()
    {
        var (service, _, _, tab) = Create();
        var start = Original.IndexOf("SELECT 1", StringComparison.Ordinal);
        var view = Propose(service, tab, "SELECT 2 FROM dual;", ExternalRange.Of(start, "SELECT 1 FROM dual;".Length));
        var waiting = service.WaitAsync(view.ProposalId, TimeSpan.FromSeconds(30), CancellationToken.None);

        tab.Document.Replace(start + "SELECT ".Length, 1, "9");
        Assert.Contains("SELECT 9 FROM dual;", tab.Text, StringComparison.Ordinal);
        Assert.False(tab.AgentBar!.IsVisible);

        var done = (await waiting).Value!;
        Assert.Equal(ExternalProposalStatus.Stale, done.Status);
    }

    [Fact]
    public void Deleting_text_that_holds_the_range_makes_it_stale()
    {
        var (service, _, _, tab) = Create();
        var start = Original.IndexOf("SELECT 1", StringComparison.Ordinal);
        var view = Propose(service, tab, "SELECT 2 FROM dual;", ExternalRange.Of(start, 6));

        tab.Document.Remove(start - 1, tab.Document.TextLength - start + 1);

        Assert.Equal(ExternalProposalStatus.Stale, service.GetProposal(view.ProposalId).Value!.Status);
    }

    [Fact]
    public async Task Accepting_one_range_proposal_moves_a_queued_one_on_other_text()
    {
        var (service, _, _, tab) = Create();
        var second = Original.IndexOf("SELECT 1", StringComparison.Ordinal);
        Propose(service, tab, "SELECT a, b FROM sample_orders WHERE status = 'A';", ExternalRange.Of(0, second - 1));
        var queued = Propose(service, tab, "SELECT 2 FROM dual;", ExternalRange.Of(second, "SELECT 1 FROM dual;".Length));

        await tab.AgentBar!.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(ExternalProposalStatus.Pending, service.GetProposal(queued.ProposalId).Value!.Status);
        await tab.AgentBar.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(ExternalProposalStatus.Accepted, service.GetProposal(queued.ProposalId).Value!.Status);
        Assert.Equal("SELECT a, b FROM sample_orders WHERE status = 'A';\nSELECT 2 FROM dual;", tab.Text);
    }

    [Fact]
    public async Task Proposals_queue_one_per_tab_and_the_next_shows_after_the_first_is_discarded()
    {
        var (service, _, _, tab) = Create();
        var first = Propose(service, tab, "SELECT 2 FROM dual;", title: "첫째");
        var second = Propose(service, tab, "SELECT 3 FROM dual;", title: "둘째");
        var bar = tab.AgentBar!;

        Assert.Equal(2, bar.Items.Count);
        Assert.EndsWith("첫째", bar.Headline, StringComparison.Ordinal);
        Assert.Equal("대기 1", bar.QueueText);

        bar.DiscardCommand.Execute(null);

        Assert.Equal(ExternalProposalStatus.Rejected, service.GetProposal(first.ProposalId).Value!.Status);
        Assert.EndsWith("둘째", bar.Headline, StringComparison.Ordinal);
        Assert.False(bar.HasQueue);

        await bar.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(ExternalProposalStatus.Accepted, service.GetProposal(second.ProposalId).Value!.Status);
        Assert.Equal("SELECT 3 FROM dual;", tab.Text);
    }

    [Fact]
    public async Task Accepting_one_proposal_makes_the_queued_one_on_the_old_version_stale()
    {
        var (service, _, _, tab) = Create();
        Propose(service, tab, "SELECT 2 FROM dual;");
        var queued = Propose(service, tab, "SELECT 3 FROM dual;");

        await tab.AgentBar!.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(ExternalProposalStatus.Stale, service.GetProposal(queued.ProposalId).Value!.Status);
        Assert.False(tab.AgentBar.IsVisible);
    }

    [Fact]
    public async Task Closing_the_comparison_window_leaves_the_proposal_on_the_bar()
    {
        var (service, _, dialogs, tab) = Create();
        dialogs.Preview = (_, _) => null;
        var view = Propose(service, tab, "SELECT 2 FROM dual;");

        await tab.AgentBar!.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(ExternalProposalStatus.Pending, service.GetProposal(view.ProposalId).Value!.Status);
        Assert.True(tab.AgentBar.IsVisible);
        Assert.Equal(ExternalEditorText.NotApplied, tab.AgentBar.StatusText);
        Assert.Equal(Original, tab.Text);
    }

    [Fact]
    public async Task Compare_only_shows_the_difference_and_changes_nothing()
    {
        var (service, _, dialogs, tab) = Create();
        tab.Select(0, 8);
        var view = Propose(service, tab, "SELECT a", ExternalRange.Selection);

        await tab.AgentBar!.CompareCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialogs.Comparisons);
        Assert.Equal(("SELECT *", "SELECT a"), (shown.Before, shown.After));
        Assert.Equal(Original, tab.Text);
        Assert.Equal(ExternalProposalStatus.Pending, service.GetProposal(view.ProposalId).Value!.Status);
    }

    [Fact]
    public async Task Waiting_past_the_time_returns_timeout_and_the_proposal_stays_pending()
    {
        var (service, _, _, tab) = Create();
        var view = Propose(service, tab, "SELECT 2 FROM dual;");

        var waited = (await service.WaitAsync(view.ProposalId, TimeSpan.FromMilliseconds(20), CancellationToken.None)).Value!;

        Assert.Equal(ExternalProposalStatus.Timeout, waited.Status);
        Assert.Equal(ExternalProposalStatus.Pending, service.GetProposal(view.ProposalId).Value!.Status);
        Assert.True(tab.AgentBar!.IsVisible);
    }

    [Fact]
    public async Task Closing_the_tab_rejects_its_waiting_proposals()
    {
        var (service, host, _, tab) = Create();
        var view = Propose(service, tab, "SELECT 2 FROM dual;");
        var waiting = service.WaitAsync(view.ProposalId, TimeSpan.FromSeconds(30), CancellationToken.None);

        host.Close(tab);

        Assert.Equal(ExternalProposalStatus.Rejected, (await waiting).Value!.Status);
    }

    [Fact]
    public void Statement_range_replaces_only_the_statement_at_the_caret()
    {
        var (service, _, _, tab) = Create();
        tab.Select(Original.IndexOf("dual", StringComparison.Ordinal), 0);

        Propose(service, tab, "SELECT 9 FROM dual", ExternalRange.Statement);
        var accepted = tab.AgentBar!.AcceptCommand.ExecuteAsync(null);

        Assert.True(accepted.IsCompleted);
        Assert.Equal("SELECT * FROM sample_orders WHERE status = 'A';\nSELECT 9 FROM dual;", tab.Text);
    }

    [Fact]
    public void Read_only_locked_outside_and_unknown_tabs_are_refused()
    {
        var (service, host, _, tab) = Create();
        tab.IsReadOnly = true;
        var readOnly = service.Propose(Access, new ExternalProposalRequest(tab.TabId, tab.Version, ExternalRange.Document, "x"));
        Assert.Equal(ExternalEditorErrors.ReadOnly, readOnly.Error);
        Assert.Contains("new_tab", readOnly.Message, StringComparison.Ordinal);

        var prod = host.NewDocument("SELECT 1 FROM dual");
        prod.ConnectionProfileId = ProdProfile;
        Assert.Equal(ExternalEditorErrors.Locked, service.Propose(Access, new ExternalProposalRequest(prod.TabId, prod.Version, ExternalRange.Document, "x")).Error);

        var open = host.NewDocument("abc");
        Assert.Equal(ExternalEditorErrors.InvalidRange, service.Propose(Access, new ExternalProposalRequest(open.TabId, open.Version, ExternalRange.Of(2, 5), "x")).Error);
        Assert.Equal(ExternalEditorErrors.TabNotFound, service.Propose(Access, new ExternalProposalRequest(Guid.NewGuid(), 0, ExternalRange.Document, "x")).Error);
        Assert.Null(prod.AgentBar);
        Assert.Null(open.AgentBar);
    }

    // ---- 탭 목록·읽기·활성화 ------------------------------------------------------------------

    [Fact]
    public void Locked_tabs_show_only_their_title_and_cannot_be_read()
    {
        var (service, host, _, tab) = Create();
        tab.FilePath = Path.Combine(Path.GetTempPath(), "secret-folder", "orders.sql");
        var prod = host.NewDocument("SELECT 1 FROM dual");
        prod.ConnectionProfileId = ProdProfile;
        prod.Title = "운영 조회";

        var tabs = service.ListTabs(Access);

        var dev = Assert.Single(tabs, t => t.TabId == tab.TabId);
        Assert.False(dev.Locked);
        Assert.Equal("orders.sql", dev.FileName); // 폴더 경로는 뺀다
        Assert.Equal(tab.Version, dev.Version);
        Assert.Equal("sql", dev.Kind);
        var locked = Assert.Single(tabs, t => t.TabId == prod.TabId);
        Assert.True(locked.Locked);
        Assert.Equal("운영 조회", locked.Title);
        Assert.True(locked.Active);
        Assert.Null(locked.Version);
        Assert.Null(locked.ConnectionProfileId);
        Assert.Equal(ExternalEditorErrors.Locked, service.GetTab(Access, prod.TabId).Error);
        Assert.Equal(ExternalEditorErrors.Locked, service.GetBinds(Access, prod.TabId).Error);
    }

    [Fact]
    public void Get_tab_reads_the_scope_with_caret_line_and_column_and_cuts_at_max_chars()
    {
        var (service, _, _, tab) = Create();
        var caret = Original.IndexOf("dual", StringComparison.Ordinal);
        tab.Select(caret, 4);

        var statement = service.GetTab(Access, tab.TabId, ExternalTabScope.Statement).Value!;
        Assert.Equal("SELECT 1 FROM dual", statement.Text);
        Assert.Equal(new ExternalCaret(caret + 4, 2, 19), statement.Caret);
        Assert.Equal(new ExternalSelection(caret, 4, Block: false), statement.Selection);
        Assert.Equal("query", statement.Statement!.Kind);
        Assert.Equal(tab.Version, statement.Version);

        Assert.Equal("dual", service.GetTab(Access, tab.TabId, ExternalTabScope.Selection).Value!.Text);

        var cut = service.GetTab(Access, null, ExternalTabScope.Document, maxChars: 6).Value!; // 활성 탭
        Assert.Equal("SELECT", cut.Text);
        Assert.True(cut.Truncated);
        Assert.Equal(new TextRange(0, Original.Length), cut.Range);
    }

    [Fact]
    public void Focus_tab_makes_the_tab_active_and_moves_to_the_line()
    {
        var (service, host, _, tab) = Create();
        host.NewDocument("other");

        Assert.True(service.FocusTab(tab.TabId, line: 2).IsOk);

        Assert.Same(tab, host.Active);
        Assert.Equal(2, tab.CaretPosition.Line);
        Assert.Equal(ExternalEditorErrors.InvalidRange, service.FocusTab(tab.TabId, line: 9).Error);
        Assert.Equal(ExternalEditorErrors.TabNotFound, service.FocusTab(Guid.NewGuid()).Error);
    }

    // ---- 새 탭 ---------------------------------------------------------------------------------

    [Fact]
    public void New_tab_is_marked_AI_keeps_the_active_tab_and_warns_about_change_statements()
    {
        var (service, host, _, tab) = Create();

        var query = service.NewTab(Access, "SELECT 1 FROM dual", "주문 조회", DevProfile, "Codex").Value!;
        var change = service.NewTab(Access, "UPDATE sample_orders SET status = 'B' WHERE id = 1;", null, null, "Codex").Value!;

        Assert.Same(tab, host.Active); // 사람이 보던 탭 그대로
        var queryTab = host.Tabs.Single(t => t.TabId == query.TabId);
        Assert.Equal("AI·주문 조회", queryTab.Title);
        Assert.Equal(DevProfile, queryTab.ConnectionProfileId);
        Assert.Null(queryTab.AgentBar);
        var changeTab = host.Tabs.Single(t => t.TabId == change.TabId);
        Assert.Equal("AI·SQL", changeTab.Title);
        Assert.Equal(ExternalEditorText.ChangeStatementNotice, changeTab.AgentBar!.Headline);
        Assert.Equal(ExternalEditorText.Close, changeTab.AgentBar.DiscardLabel);
        Assert.False(changeTab.AgentBar.ShowsAccept);
        Assert.Equal(ExternalEditorErrors.Locked, service.NewTab(Access, "SELECT 1 FROM dual", null, ProdProfile).Error);
    }

    [Fact]
    public void New_tab_allows_ten_a_minute()
    {
        var now = new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var service = new ExternalEditorService { HostSource = () => host, UtcNow = () => now };

        for (var i = 0; i < ExternalEditorService.NewTabLimit; i++)
        {
            Assert.True(service.NewTab(Access, "SELECT " + i + " FROM dual").IsOk);
        }

        var refused = service.NewTab(Access, "SELECT 11 FROM dual");
        Assert.Equal(ExternalEditorErrors.RateLimited, refused.Error);
        Assert.Equal(10, host.Tabs.Count);

        now = now.AddMinutes(1);
        Assert.True(service.NewTab(Access, "SELECT 12 FROM dual").IsOk);
    }

    // ---- 바인드·저장 ----------------------------------------------------------------------------

    [Fact]
    public void Binds_give_values_only_when_allowed_and_never_for_secret_names()
    {
        var (service, host, _, tab) = Create("SELECT * FROM sample_users WHERE id = :id AND pwd = :user_pwd AND c = :code");
        tab.Binds.Add("id").Text = "7";
        tab.Binds.Add("user_pwd").Text = "s3cret";

        var binds = service.GetBinds(Access, tab.TabId).Value!;

        Assert.Equal(["ID", "USER_PWD", "CODE"], binds.Select(b => b.Name));
        Assert.Equal("7", binds[0].Value);
        Assert.True(binds[1].HasValue);
        Assert.Equal("***", binds[1].Value); // 비밀 이름은 값 대신 가림 표시
        Assert.False(binds[2].HasValue);

        var free = host.NewDocument("SELECT :id FROM dual"); // 접속 없는 탭: 글은 읽어도 값은 주지 않는다
        free.Binds.Add("id").Text = "7";
        var noValue = Assert.Single(service.GetBinds(Access, free.TabId).Value!);
        Assert.True(noValue.HasValue);
        Assert.Null(noValue.Value);
    }

    [Fact]
    public async Task Save_request_saves_only_when_the_person_presses_save()
    {
        var (service, _, _, tab) = Create();
        var path = Path.Combine(Path.GetTempPath(), "oranda-ext-" + Guid.NewGuid().ToString("N") + ".sql");
        tab.FilePath = path;
        try
        {
            var saving = service.RequestSaveAsync(Access, tab.TabId, "Claude Code", TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.False(saving.IsCompleted);
            Assert.False(File.Exists(path));
            Assert.Equal(ExternalEditorText.Save, tab.AgentBar!.AcceptLabel);
            Assert.Equal(ExternalEditorText.Ignore, tab.AgentBar.DiscardLabel);

            await tab.AgentBar.AcceptCommand.ExecuteAsync(null);

            var saved = (await saving).Value!;
            Assert.Equal(ExternalSaveStatus.Saved, saved.Status);
            Assert.Equal(Path.GetFileName(path), saved.FileName);
            Assert.True(File.Exists(path));
            Assert.False(tab.AgentBar.IsVisible);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_request_is_rejected_by_ignore_and_times_out_without_an_answer()
    {
        var (service, _, _, tab) = Create();
        var ignored = service.RequestSaveAsync(Access, tab.TabId, null, TimeSpan.FromSeconds(30), CancellationToken.None);
        tab.AgentBar!.DiscardCommand.Execute(null);
        Assert.Equal(ExternalSaveStatus.Rejected, (await ignored).Value!.Status);

        var late = (await service.RequestSaveAsync(Access, tab.TabId, null, TimeSpan.FromMilliseconds(20), CancellationToken.None)).Value!;
        Assert.Equal(ExternalSaveStatus.Timeout, late.Status);
        Assert.False(tab.AgentBar.IsVisible); // 띠를 거뒀다

        tab.IsReadOnly = true;
        Assert.Equal(ExternalEditorErrors.ReadOnly, (await service.RequestSaveAsync(Access, tab.TabId, null, TimeSpan.Zero, CancellationToken.None)).Error);
    }
}
