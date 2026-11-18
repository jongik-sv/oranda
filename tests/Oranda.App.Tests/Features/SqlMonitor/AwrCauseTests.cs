using Avalonia.Controls;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Features.SqlMonitor.Ai;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.SqlMonitor;

/// <summary>
/// AWR 「AI로 원인 보기」(AI 확대 §3.1, TSK-15-05): 입구가 켜지는 조건(계획 2개 이상·허용 수준 계획 이상·AI 설정, AWR 을 못 쓰면 기존 안내),
/// 채팅창 새 대화(주 모델·도구 범위·바인드 값은 조회+행 값만·가림), 답 카드(원인 후보·Baseline 스크립트를 편집기에), 이어 묻기. 가짜 드라이버는 Session 이 없으면 거절한다.
/// </summary>
public class AwrCauseTests
{
    private const string SqlId = SqlMonitorTuningTests.SqlId;
    private const long GoodPlan = 3392001144;
    private const long BadPlan = 1838229974;

    private const string Answer =
        "## 언제 무엇이 바뀌었나\n10-24 09:00 에 3392001144 에서 1838229974 로 바뀌었습니다.\n\n" +
        "## 원인 후보\n| 원인 | 근거 |\n| --- | --- |\n| 통계 갱신 | LOT_HIST last_analyzed 10-24 02:10 |\n| 바인드 엿보기 | EQP_ID 히스토그램 |\n\n" +
        "## 권하는 조치\n이전 계획을 고정하고 통계 수집 방식을 다시 정합니다.\n\n" +
        "## 고정할 계획\n3392001144";

    private sealed class Rig
    {
        public Rig(AiAccessLevel access = AiAccessLevel.Plan, LicensePack pack = LicensePack.Diagnostics, ConnectionEnvironment env = ConnectionEnvironment.Development,
            Dbms dbms = Dbms.Oracle, AiSettings? settings = null)
        {
            Session = new CauseSession(new ConnectionProfile("dev-example", "sample", "h:1521/s", Id: Guid.NewGuid(), Environment: env, AiAccess: access, LicensePack: pack, Dbms: dbms));
            Settings = settings ?? new AiSettings { Model = "opus" };
            Vm = new SqlMonitorViewModel(Monitor, null, _ => Task.FromResult<ISession?>(Session))
            {
                PostToUi = a => a(),
                TuningService = Tuning,
                OpenEditor = (t, b) => Editors.Add((t, b)),
                AiSettingsSource = () => Settings,
                AiDriver = Driver,
                AiChat = KitFakes.Host(Chat),
                AiMasker = new KitMasker(),
                AiCopyText = text =>
                {
                    Copied.Add(text);
                    return Task.CompletedTask;
                },
            };
        }

        public CauseSession Session { get; }

        public AiSettings Settings { get; set; }

        public FakeTuning Tuning { get; } = new() { Awr = SqlMonitorTuningTests.AwrRows() };

        public CauseMonitor Monitor { get; } = new();

        public FakeDriver Driver { get; } = new() { Answer = _ => KitFakes.Answer(Answer) };

        public AiChatViewModel Chat { get; } = new();

        public SqlMonitorViewModel Vm { get; }

        public List<(string Title, string Body)> Editors { get; } = [];

        public List<string> Copied { get; } = [];

        public async Task<Rig> LoadAsync()
        {
            await Vm.StartAsync();
            Vm.SelectedSql = Vm.Results[0];
            Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
            await Vm.Awr.EnsureLoadedAsync();
            return this;
        }
    }

    [Fact]
    public async Task Entry_turns_on_only_with_two_plans_and_says_why_otherwise()
    {
        var rig = await new Rig().LoadAsync();
        Assert.Equal(AwrPaneState.Loaded, rig.Vm.Awr.State);
        Assert.True(rig.Vm.Awr.CauseEntry.IsEnabled);
        Assert.Equal(AwrCausePrompt.EnabledTip, rig.Vm.Awr.CauseEntry.ToolTip);

        var one = new Rig();
        one.Tuning.Awr = [.. SqlMonitorTuningTests.AwrRows().Where(r => r.PlanHashValue == GoodPlan)];
        await one.LoadAsync();
        Assert.False(one.Vm.Awr.CauseEntry.IsEnabled);
        Assert.Contains("2개 이상", one.Vm.Awr.CauseEntry.ToolTip, StringComparison.Ordinal);
        Assert.Contains("1개", one.Vm.Awr.CauseEntry.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_is_off_with_the_existing_awr_notice_when_the_pack_is_missing_and_follows_ai_settings_and_level()
    {
        var noPack = await new Rig(pack: LicensePack.None).LoadAsync();
        Assert.Equal(AwrPaneState.LicenseOff, noPack.Vm.Awr.State);
        Assert.False(noPack.Vm.Awr.CauseEntry.IsEnabled);
        Assert.Contains("Diagnostics Pack", noPack.Vm.Awr.CauseEntry.ToolTip, StringComparison.Ordinal); // 기존 AWR 화면 안내 그대로(AE-4)
        Assert.Contains(noPack.Vm.Awr.HintText, noPack.Vm.Awr.CauseEntry.ToolTip, StringComparison.Ordinal);

        var metadata = await new Rig(access: AiAccessLevel.Metadata).LoadAsync();
        Assert.Equal(AiEntryRule.LevelTooLowText(AiAccessLevel.Plan), metadata.Vm.Awr.CauseEntry.ToolTip);

        var none = await new Rig(settings: new AiSettings { Driver = AiDriverKind.None }).LoadAsync();
        Assert.Equal(AiEntryRule.NotConfiguredText, none.Vm.Awr.CauseEntry.ToolTip);
        Assert.False(await none.Vm.StartAwrCauseAsync());
        Assert.Empty(none.Driver.Requests);
    }

    [Fact]
    public async Task Start_opens_a_new_chat_conversation_on_the_main_model_with_the_sql_scope_for_the_tools()
    {
        var rig = await new Rig().LoadAsync();

        await rig.Vm.Awr.CauseEntry.RunCommand.ExecuteAsync(null);

        var request = Assert.Single(rig.Driver.Requests);
        Assert.Equal(AiTier.Main, request.Tier);
        Assert.NotNull(request.Session); // 도구를 쓴다: 실행 범위가 있다
        Assert.True(request.Session!.IsRevoked); // 끝나면 폐기
        Assert.Equal(SqlId, request.Context.SqlId);
        Assert.Equal(CauseMonitor.FullText, request.Context.StatementText);
        Assert.Equal("MES", request.Context.CurrentSchema);
        Assert.True(request.Session.IsKnownSqlId(SqlId));
        Assert.Contains("<context name=\"awr_plans\">", request.Prompt, StringComparison.Ordinal);
        Assert.Contains($"plan_hash_value={GoodPlan}", request.Prompt, StringComparison.Ordinal);
        Assert.Contains($"{GoodPlan} -> {BadPlan}", request.Prompt, StringComparison.Ordinal); // 바뀐 시점
        Assert.Contains("get_awr_plan_history", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("## " + AwrCausePrompt.PinSection, request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("EQP-7", request.Prompt, StringComparison.Ordinal); // 바인드 캡처 값은 조회+행 값에서만
        var conversation = Assert.IsType<AwrCauseConversation>(rig.Chat.Owner);
        Assert.Same(conversation, rig.Vm.AwrCause);
        Assert.StartsWith(AwrCausePrompt.Title, rig.Chat.Title, StringComparison.Ordinal);
        var first = Assert.IsType<ChatUserEntry>(rig.Chat.Entries[0]).Text;
        Assert.Contains("보내지 않음: 바인드 캡처 값", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bind_capture_values_go_only_at_the_row_values_level()
    {
        var rig = await new Rig(access: AiAccessLevel.QueryRows).LoadAsync();

        await rig.Vm.StartAwrCauseAsync();

        var prompt = Assert.Single(rig.Driver.Requests).Prompt;
        Assert.Contains("<context name=\"bind_capture\">", prompt, StringComparison.Ordinal);
        Assert.Contains(":EQP (VARCHAR2) = EQP-7", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Masked_connection_sends_the_statement_with_placeholders_and_gives_the_tools_the_vault()
    {
        var rig = await new Rig(env: ConnectionEnvironment.Production).LoadAsync();

        await rig.Vm.StartAwrCauseAsync();

        var request = Assert.Single(rig.Driver.Requests);
        Assert.True(request.MaskingRequired);
        Assert.DoesNotContain("'RUN'", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(":v1", request.Prompt, StringComparison.Ordinal);
        Assert.NotNull(request.Session!.Run.Literals); // 도구 결과의 SQL 글도 같은 사전으로 가린다
        Assert.True(request.Session.Run.MaskLiteralsRequired);
    }

    [Fact]
    public async Task Answer_becomes_a_card_whose_baseline_script_goes_to_a_new_editor_tab_without_running()
    {
        var rig = await new Rig().LoadAsync();

        await rig.Vm.StartAwrCauseAsync();

        Assert.DoesNotContain(rig.Chat.Entries, e => e is ChatAssistantEntry a && a.Text.Contains("## 원인 후보", StringComparison.Ordinal)); // 흘러온 글은 카드로 바뀐다
        var card = Assert.IsType<AiFeatureCard>(Assert.Single(rig.Chat.Entries.OfType<ChatCardEntry>()).Card);
        Assert.Equal(AwrCausePrompt.Title, card.Title);
        Assert.True(card.HasParts);
        Assert.Equal(2, card.Answer![AwrCausePrompt.CauseSection]!.Rows!.Count);
        Assert.True(card.CanInsertVisible);
        Assert.Contains("DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE", card.InsertText, StringComparison.Ordinal);
        Assert.Contains($"plan_hash_value => {GoodPlan}", card.InsertText, StringComparison.Ordinal);
        Assert.Contains("-- DECLARE", card.InsertText, StringComparison.Ordinal); // AWR 에서 올리는 문안은 주석

        card.InsertEditorCommand.Execute(null);

        var (title, body) = Assert.Single(rig.Editors);
        Assert.Equal($"baseline_{SqlId}_{GoodPlan}.sql", title);
        Assert.Equal(card.InsertText, body);
        Assert.True(card.IsInserted);
        Assert.Empty(rig.Tuning.AwrPlanHashes); // 화면도 AI 도 DB 에 아무것도 보내지 않았다(계획 비교도 안 함)
    }

    [Theory]
    [InlineData("없음")]
    [InlineData("1234567890")] // 이력에 없는 번호는 버린다
    public async Task No_baseline_button_when_the_answer_pins_nothing_known(string pin)
    {
        var rig = await new Rig().LoadAsync();
        rig.Driver.Answer = _ => KitFakes.Answer(Answer.Replace("## 고정할 계획\n3392001144", "## 고정할 계획\n" + pin, StringComparison.Ordinal));

        await rig.Vm.StartAwrCauseAsync();

        var card = Assert.IsType<AiFeatureCard>(Assert.Single(rig.Chat.Entries.OfType<ChatCardEntry>()).Card);
        Assert.True(card.HasParts);
        Assert.False(card.CanInsertVisible);
    }

    [Fact]
    public async Task Tibero_gets_no_baseline_and_a_broken_template_shows_the_text_as_it_came()
    {
        var rig = await new Rig(dbms: Dbms.Tibero).LoadAsync();
        rig.Driver.Answer = _ => KitFakes.Answer("그냥 글 답 3392001144");

        await rig.Vm.StartAwrCauseAsync();

        Assert.Contains("write 없음 in the last section", Assert.Single(rig.Driver.Requests).Prompt, StringComparison.Ordinal);
        var card = Assert.IsType<AiFeatureCard>(Assert.Single(rig.Chat.Entries.OfType<ChatCardEntry>()).Card);
        Assert.True(card.ShowsRawText);
        Assert.False(card.CanInsertVisible);
    }

    [Fact]
    public async Task Follow_up_goes_to_the_same_conversation_route_and_model_with_the_resume_id()
    {
        var rig = await new Rig().LoadAsync();
        await rig.Vm.StartAwrCauseAsync();
        rig.Driver.Answer = _ => KitFakes.Answer("히스토그램을 지우면 됩니다", resume: "resume-2");

        rig.Chat.Draft = "히스토그램은?";
        rig.Chat.SendCommand.Execute(null);
        await KitFakes.WaitAsync(() => rig.Driver.Requests.Count == 2 && !rig.Chat.IsRunning);

        var (first, follow) = (rig.Driver.Requests[0], rig.Driver.Requests[1]);
        Assert.Equal("resume-1", follow.ResumeId);
        Assert.Equal(first.Expected, follow.Expected);
        Assert.Equal(AiTier.Main, follow.Tier);
        Assert.Equal(first.Context, follow.Context); // 같은 sql_id 범위
        Assert.StartsWith("# Follow-up\n히스토그램은?", follow.Prompt, StringComparison.Ordinal);
        Assert.NotSame(first.Session, follow.Session);
        Assert.Equal("히스토그램을 지우면 됩니다", rig.Chat.Entries.OfType<ChatAssistantEntry>().Last().Text);
        Assert.Single(rig.Chat.Entries.OfType<ChatCardEntry>()); // 이어 묻기 답은 글 그대로
    }

    [Fact]
    public async Task Follow_up_without_a_resume_id_carries_the_first_prompt_and_previous_answer()
    {
        var rig = await new Rig().LoadAsync();
        rig.Driver.Answer = _ => KitFakes.Answer(Answer, resume: null);
        await rig.Vm.StartAwrCauseAsync();

        await rig.Vm.AwrCause!.FollowUpAsync(new ChatFollowUp("왜?", []), CancellationToken.None);

        var second = rig.Driver.Requests[1];
        Assert.Null(second.ResumeId);
        Assert.StartsWith(rig.Driver.Requests[0].Prompt, second.Prompt, StringComparison.Ordinal);
        Assert.Contains("<previous_answer>\n" + Answer, second.Prompt, StringComparison.Ordinal);
        Assert.EndsWith("Never run anything; only suggest.", second.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Driver_failure_leaves_the_error_line_and_no_card()
    {
        var rig = await new Rig().LoadAsync();
        rig.Driver.Answer = _ => [new AiError("요금 한도", "rate_limited")];

        await rig.Vm.StartAwrCauseAsync();

        Assert.Empty(rig.Chat.Entries.OfType<ChatCardEntry>());
        Assert.Contains(rig.Chat.Entries, e => e is ChatAssistantEntry a && a.Text.StartsWith("오류:", StringComparison.Ordinal));
        Assert.False(rig.Chat.IsRunning);
    }

    [Fact]
    public Task Awr_tab_shows_the_entry_button_with_its_reason() => HeadlessHost.Run(async () =>
    {
        var rig = new Rig();
        rig.Tuning.Awr = [.. SqlMonitorTuningTests.AwrRows().Where(r => r.PlanHashValue == GoodPlan)];
        await rig.LoadAsync();
        var window = new Window { Content = new SqlMonitorView { DataContext = rig.Vm }, Width = 1180, Height = 760 };
        window.Show();
        MyBatisFlowRig.Pump();
        var button = ((Control)window.Content!).FindControl<Button>("AwrAiCauseButton")!;

        Assert.Equal(SqlMonitorViewModel.AwrCauseLabel, button.Content);
        Assert.False(button.IsEffectivelyEnabled);
        Assert.Contains("2개 이상", ToolTip.GetTip(button) as string, StringComparison.Ordinal);

        rig.Tuning.Awr = SqlMonitorTuningTests.AwrRows();
        await rig.Vm.Awr.LoadAsync();
        MyBatisFlowRig.Pump();
        Assert.True(button.IsEffectivelyEnabled);
        window.Close();
    });

    internal sealed class CauseMonitor : IMonitorService
    {
        public const string FullText = "SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh WHERE lh.eqp_id = :eqp AND lh.stat = 'RUN'";

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TopSqlRow>>(
            [
                new(new CursorRef(SqlId, 0, 1), "SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh") { Executions = 18_220, ParsingSchema = "MES" },
            ]);

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) =>
            Task.FromResult(new SqlDetail(cursor, FullText) { Binds = [new CapturedBind(":EQP", 1, "VARCHAR2", "EQP-7")] });

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => throw new NotSupportedException();

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class CauseSession(ConnectionProfile profile) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = profile;

        public ServerInfo Server { get; } = new("19.21.0.0.0", "SAMPLE1", "h", "SAMPLE", "SAMPLE", null, false, 1, new ServerCapabilities(true, true, true, true, true));

        public SessionPurpose Purpose => SessionPurpose.Monitor;

        public string DbKey => "SAMPLE/SAMPLE";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
