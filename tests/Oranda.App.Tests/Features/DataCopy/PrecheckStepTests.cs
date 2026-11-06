using Oranda.App.Features.DataCopy;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>데이터 복사 2단계(사전 점검): 운영 대상 차단, 실행 조건, 미리 확인한 문장 집합, 다시 켜기, 원본 읽기 전용.</summary>
public sealed class PrecheckStepTests
{
    private static readonly ResolvedPeriod Period = new(null, null);

    private static CheckItem Error() => new("ORDERS", "TARGET_MISSING", CheckLevel.Error, "대상 테이블이 없습니다.");

    private static PreviewStatement Statement(string sql, PolicyDecisionKind policy, string? retype = null, PreviewSide side = PreviewSide.Target) =>
        new(sql, side, policy, null, retype);

    private static CopySessions Sessions() => new(new FakeSession(), new FakeSession());

    [Fact]
    public void Check_item_shows_readable_name_and_keeps_unknown_code()
    {
        var known = new CheckItemViewModel(new CheckItem("ORDERS", "TargetTriggers", CheckLevel.Confirm, "트리거"), []);
        Assert.Equal("대상 트리거", known.CodeText);
        Assert.Equal("TargetTriggers", known.Code); // 코드는 상세 칸에 남긴다

        var unknown = new CheckItemViewModel(new CheckItem("ORDERS", "SomethingNew", CheckLevel.Pass, "새 점검"), []);
        Assert.Equal("SomethingNew", unknown.CodeText);
    }

    [Fact]
    public void Every_check_code_has_a_name_and_an_english_literal()
    {
        // Oracle 점검(PrecheckRules·CopyPrecheckService)이 만드는 코드를 소스에서 모아 이름 표와 맞춘다.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("Oranda.sln*").Length == 0)
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var copyDir = Path.Combine(dir.FullName, "src", "Oranda.Oracle", "Transfer", "Copy");
        var codes = Directory.GetFiles(copyDir, "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"new CheckItem\([^,]+,\s*""(?<code>[A-Za-z]+)""").Select(m => m.Groups["code"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(codes);
        Assert.Empty(codes.Where(c => !CheckItemViewModel.CodeNameTable.ContainsKey(c)));
        Assert.Empty(CheckItemViewModel.CodeNameTable.Values.Where(n => !Oranda.App.Features.Localization.LiteralTranslator.Table.ContainsKey(n)));
    }

    [Fact]
    public async Task Opening_job_whose_target_is_production_clears_target()
    {
        var h = new DataCopyHarness();
        var job = TestJobs.Job("동료가 보낸 작업", source: "SRC", target: "PRODDB", TestJobs.Rule("ORDERS"));

        var vm = await h.OpenAsync(job);

        Assert.Null(vm.Editor.Target); // 이 PC 에서는 운영 접속이라 대상을 비운다
        Assert.NotNull(vm.Editor.Source);
        Assert.Contains("운영", vm.Editor.Notice, StringComparison.Ordinal);
        Assert.Contains("운영", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.StatusIsError);

        // 대상이 비어 있으면 점검으로 넘어가지 못하고 세션도 열지 않는다.
        await vm.GoToPrecheckAsync();
        Assert.Equal(DataCopyStep.RuleEdit, vm.Step);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task Run_is_disabled_while_precheck_has_errors()
    {
        var h = new DataCopyHarness();
        var vm = new PrecheckViewModel(h.Services());

        h.Precheck = new FakePrecheck(TestJobs.Report([Error()]));
        vm = new PrecheckViewModel(h.Services());
        await vm.RunAsync(Sessions(), TestJobs.Job("점검"), Period);
        Assert.True(vm.HasErrors);
        Assert.False(vm.CanRun);

        h.Precheck = new FakePrecheck(TestJobs.Report([new CheckItem("ORDERS", "OK", CheckLevel.Pass, "통과")]));
        vm = new PrecheckViewModel(h.Services());
        await vm.RunAsync(Sessions(), TestJobs.Job("점검"), Period);
        Assert.True(vm.CanRun);
    }

    [Fact]
    public async Task Run_does_not_start_while_precheck_has_errors()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([Error()]));
        var vm = await h.OpenAsync(TestJobs.Job("오류 있는 점검", rules: TestJobs.Rule("ORDERS")));

        await vm.GoToPrecheckAsync();
        await vm.StartRunAsync();

        Assert.Equal(0, h.Runner.Calls);
        Assert.Equal(DataCopyStep.Precheck, vm.Step);
    }

    [Fact]
    public async Task Confirm_statements_are_preconfirmed_on_run()
    {
        var h = new DataCopyHarness();
        var preview = new[]
        {
            Statement("DELETE FROM \"APP\".\"ORDERS\"", PolicyDecisionKind.Confirm),
            Statement("ALTER SEQUENCE \"APP\".\"SEQ\" INCREMENT BY 5", PolicyDecisionKind.Confirm),
            Statement("SELECT * FROM \"APP\".\"ORDERS\"", PolicyDecisionKind.Allow, side: PreviewSide.Source),
        };
        h.Precheck = new FakePrecheck(TestJobs.Report(preview: preview));
        var vm = await h.OpenAsync(TestJobs.Job("확인 문장", rules: TestJobs.Rule("ORDERS")));

        await vm.GoToPrecheckAsync();
        Assert.True(vm.Precheck.CanRun);
        await vm.StartRunAsync();

        Assert.Equal(1, h.Runner.Calls);
        Assert.Equal(
            preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).Order(),
            h.Runner.Decisions!.PreConfirmedSql.Order());
        Assert.Equal(DataCopyStep.Run, vm.Step);
    }

    [Fact]
    public async Task Denied_statement_blocks_run_and_is_never_preconfirmed()
    {
        var h = new DataCopyHarness();
        var vm = new PrecheckViewModel(h.Services());
        h.Precheck = new FakePrecheck(TestJobs.Report(preview: [Statement("DROP USER X", PolicyDecisionKind.Deny)]));
        vm = new PrecheckViewModel(h.Services());

        await vm.RunAsync(Sessions(), TestJobs.Job("거부"), Period);

        Assert.False(vm.CanRun);
        Assert.Empty(vm.BuildDecisions().PreConfirmedSql);
    }

    [Fact]
    public async Task Drop_statements_need_connection_name_retype()
    {
        var h = new DataCopyHarness();
        const string DropA = "DROP TABLE \"APP\".\"ORDERS\" CASCADE CONSTRAINTS";
        const string DropB = "DROP TABLE \"APP\".\"ITEMS\" CASCADE CONSTRAINTS";
        // 정책이 문장마다 내는 RetypeText 는 지울 테이블 이름이다. 사용자가 다시 입력할 글자는 대상 접속 이름 하나다.
        h.Precheck = new FakePrecheck(TestJobs.Report(preview:
        [
            Statement(DropA, PolicyDecisionKind.Confirm, retype: "ORDERS"),
            Statement(DropB, PolicyDecisionKind.Confirm, retype: "ITEMS"),
        ]));
        var vm = new PrecheckViewModel(h.Services());
        var sessions = new CopySessions(new FakeSession(), new FakeSession(h.Target));

        await vm.RunAsync(sessions, TestJobs.Job("다시 만들기"), Period);

        Assert.True(vm.NeedsRetype);
        Assert.False(vm.CanRun); // 다시 입력 전에는 「복사 실행」이 꺼진다
        Assert.Empty(vm.BuildDecisions().PreConfirmedSql);

        vm.RetypeInput = "ORDERS"; // 테이블 이름은 확인이 아니다
        Assert.False(vm.CanRun);
        Assert.Empty(vm.BuildDecisions().PreConfirmedSql);

        vm.RetypeInput = "TGT"; // 대상 접속 이름 한 번으로 목록 전체를 확인한다
        Assert.True(vm.CanRun);
        Assert.Equal(new[] { DropA, DropB }.Order(), vm.BuildDecisions().PreConfirmedSql.Order());
    }

    [Fact]
    public async Task Disable_trigger_choice_hidden_without_privilege()
    {
        var h = new DataCopyHarness();
        CheckItem Item(string table) => new(table, "TRIGGERS", CheckLevel.Confirm, "대상 트리거가 있습니다.") { Choices = [CheckChoice.DisableTriggers] };
        h.Precheck = new FakePrecheck(TestJobs.Report(
            [Item("NO_RIGHT"), Item("HAS_RIGHT")],
            [TestJobs.Plan("NO_RIGHT", canDisableTriggers: false), TestJobs.Plan("HAS_RIGHT", canDisableTriggers: true)]));
        var vm = new PrecheckViewModel(h.Services());

        await vm.RunAsync(Sessions(), TestJobs.Job("트리거"), Period);

        Assert.False(vm.Items[0].Choices.Single().IsVisible); // 끌 권한이 없으면 선택지를 보이지 않는다
        Assert.True(vm.Items[1].Choices.Single().IsVisible);
        vm.Items[1].Choices.Single().IsChosen = true;
        Assert.Equal(["HAS_RIGHT"], vm.BuildDecisions().DisableTriggerTables);
    }

    [Fact]
    public async Task Unfinished_run_offers_reenable()
    {
        var h = new DataCopyHarness();
        var job = TestJobs.Job("끝나지 않은 작업", rules: TestJobs.Rule("ORDERS"));
        h.RunLog.Unfinished.Add(new UnfinishedRun("/runs/1.jsonl", DateTimeOffset.UnixEpoch,
        [
            new DisabledObject(DisabledKind.Trigger, "APP", "ORDERS", "TRG_ORDERS"),
            new DisabledObject(DisabledKind.ForeignKey, "APP", "ORDER_ITEMS", "FK_ITEMS_ORDERS"),
        ]));

        var vm = await h.OpenAsync(job);

        Assert.True(vm.HasUnfinished);
        var item = Assert.Single(vm.UnfinishedRuns);
        Assert.Equal(2, item.Lines.Count);

        await vm.ReenableAsync(item);

        Assert.Equal(
            [
                "ALTER TRIGGER \"APP\".\"TRG_ORDERS\" ENABLE",
                "ALTER TABLE \"APP\".\"ORDER_ITEMS\" ENABLE VALIDATE CONSTRAINT \"FK_ITEMS_ORDERS\"",
            ],
            h.Statements);
        Assert.Equal(["/runs/1.jsonl"], h.RunLog.Recovered);
        Assert.Empty(vm.UnfinishedRuns);
        Assert.Equal(["TGT"], h.Opened.Select(p => p.Name)); // 대상 세션만 열고 닫는다
    }

    [Fact]
    public async Task Unfinished_run_stays_when_enable_fails()
    {
        var h = new DataCopyHarness();
        var services = new DataCopyServices
        {
            Store = h.Store,
            RunLog = h.RunLog,
            ProfileLoader = _ => Task.FromResult<IReadOnlyList<ConnectionProfile>>([h.Source, h.Target]),
            SessionOpener = (profile, _) => Task.FromResult<ISession?>(new FakeSession(profile)),
            StatementRunner = (_, _, _) => Task.FromResult<string?>("ORA-01031: insufficient privileges"),
        };
        h.RunLog.Unfinished.Add(new UnfinishedRun("/runs/2.jsonl", DateTimeOffset.UnixEpoch, [new DisabledObject(DisabledKind.Trigger, "APP", "ORDERS", "TRG")]));
        var job = TestJobs.Job("실패", rules: TestJobs.Rule("ORDERS"));
        h.Store.Jobs[job.Id] = job;
        var vm = new DataCopyViewModel(services);
        await vm.InitializeAsync();
        await vm.OpenJobAsync(job);

        await vm.ReenableAsync(Assert.Single(vm.UnfinishedRuns));

        Assert.Empty(h.RunLog.Recovered); // 켜지 못했으면 복구된 것으로 표시하지 않는다
        Assert.Single(vm.UnfinishedRuns);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Changing_connections_reopens_sessions()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(TestJobs.Job("접속 바꾸기", rules: TestJobs.Rule("ORDERS")));

        await vm.GoToPrecheckAsync();
        await vm.GoToPrecheckAsync(); // 같은 접속이면 세션을 다시 열지 않는다
        Assert.Equal(["SRC", "TGT"], h.Opened.Select(p => p.Name));

        vm.Editor.Target = vm.Editor.TargetOptions.Single(o => o.Profile.Name == "SRC");
        vm.Editor.TargetOwner = "OTHER"; // 같은 접속·같은 소유자는 시작하지 않는다
        await vm.GoToPrecheckAsync();

        Assert.Equal(["SRC", "TGT", "SRC", "SRC"], h.Opened.Select(p => p.Name));
    }

    [Fact]
    public async Task Precheck_reuses_the_source_session_opened_for_column_info_and_closes_each_session_once()
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[new DbObjectRef("APP", "ORDERS", "TABLE")] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        var vm = await h.OpenAsync(TestJobs.Job("세션 다시 쓰기", rules: TestJobs.Rule("ORDERS")));

        await vm.LoadColumnsAsync(vm.Editor.Rules[0]); // 열 정보를 읽으려고 원본만 연다
        Assert.Equal(["SRC"], h.Opened.Select(p => p.Name));

        await vm.GoToPrecheckAsync();
        Assert.Equal(["SRC", "TGT"], h.Opened.Select(p => p.Name)); // 원본을 다시 열지 않는다(비밀번호를 두 번 묻지 않는다)
        Assert.True(h.Opened[0].ReadOnly);

        await vm.CloseSessionsAsync();
        Assert.All(h.OpenedSessions, s => Assert.Equal(1, s.DisposeCount)); // 넘겨 쓴 원본도 한 번만 닫는다
    }

    [Fact]
    public async Task Handed_over_source_session_is_closed_once_when_the_target_does_not_open()
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[new DbObjectRef("APP", "ORDERS", "TABLE")] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        var vm = await h.OpenAsync(TestJobs.Job("대상 열기 실패", rules: TestJobs.Rule("ORDERS")));
        await vm.LoadColumnsAsync(vm.Editor.Rules[0]);
        var columnSession = Assert.Single(h.OpenedSessions);

        h.RefuseOpen = p => p.Name == "TGT"; // 대상 비밀번호 입력을 취소했다
        await vm.GoToPrecheckAsync();
        Assert.False(vm.HasSessions);
        Assert.Equal(1, columnSession.DisposeCount); // 넘겨 쓴 원본은 쌍을 못 만들었을 때 한 번 닫힌다

        h.RefuseOpen = null;
        await vm.LoadColumnsAsync(vm.Editor.Rules[0]); // 닫힌 세션을 다시 쓰지 않고 새로 연다
        Assert.Equal(["SRC", "TGT", "SRC"], h.Opened.Select(p => p.Name));
        Assert.Equal(1, columnSession.DisposeCount);
    }

    [Fact]
    public async Task Precheck_selects_the_first_confirm_item_and_keeps_the_choice_on_recheck()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report(
        [
            new CheckItem("CODES", "COLUMN_MISSING", CheckLevel.Error, "원본에 없는 NOT NULL 열이 대상에 있습니다."),
            new CheckItem("ORDERS", "TRIGGERS", CheckLevel.Confirm, "대상 트리거가 실행됩니다.") { Choices = [CheckChoice.DisableTriggers] },
            new CheckItem("ORDER_ITEMS", "FK_CHILD", CheckLevel.Confirm, "대상의 자식 테이블이 참조합니다.") { Choices = [CheckChoice.SwitchToMerge] },
        ]));
        var vm = await h.OpenAsync(TestJobs.Job("점검 항목 고르기", rules: TestJobs.Rule("ORDERS")));

        await vm.GoToPrecheckAsync();
        Assert.Equal("ORDERS", vm.Precheck.SelectedItem?.Table); // 오류가 앞에 있어도 고를 거리가 있는 첫 「확인 필요」

        vm.Precheck.SelectedItem = vm.Precheck.Items.Single(i => i.Table == "ORDER_ITEMS");
        await vm.GoToPrecheckAsync();
        Assert.Equal("ORDER_ITEMS", vm.Precheck.SelectedItem?.Table); // 다시 점검해도 고른 항목을 이어 간다
    }

    [Fact]
    public async Task Source_session_is_opened_read_only()
    {
        var h = new DataCopyHarness();
        var received = new List<ConnectionProfile>();

        var sessions = await DataCopySessions.OpenAsync((profile, _) =>
        {
            received.Add(profile);
            return Task.FromResult<ISession?>(new FakeSession(profile, purpose: SessionPurpose.Dedicated));
        }, h.Source, h.Target, CancellationToken.None);

        Assert.NotNull(sessions);
        Assert.True(received[0].ReadOnly); // 원본: 읽기 전용 프로필
        Assert.Equal("SRC", received[0].Name);
        Assert.Equal("TGT", received[1].Name);
        Assert.False(received[1].ReadOnly);
        Assert.Equal(SessionPurpose.Dedicated, sessions.Source.Purpose);
        Assert.Equal(SessionPurpose.Dedicated, sessions.Target.Purpose);
    }

    [Fact]
    public async Task Production_target_never_opens_any_session()
    {
        var h = new DataCopyHarness();
        var opened = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => DataCopySessions.OpenAsync((_, _) =>
        {
            opened++;
            return Task.FromResult<ISession?>(null);
        }, h.Source, h.Production, CancellationToken.None));

        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Cancelled_target_password_closes_the_already_open_source()
    {
        var h = new DataCopyHarness();
        var source = new FakeSession(h.Source);

        var sessions = await DataCopySessions.OpenAsync((profile, _) =>
            Task.FromResult<ISession?>(profile.Name == "SRC" ? source : null), h.Source, h.Target, CancellationToken.None);

        Assert.Null(sessions);
        Assert.Equal(SessionState.Closed, source.State);
    }

    [Fact]
    public async Task Failed_recheck_clears_the_old_report()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([new CheckItem(null, "OK", CheckLevel.Pass, "통과")]));
        var vm = new PrecheckViewModel(h.Services());
        await vm.RunAsync(Sessions(), TestJobs.Job("점검"), Period);
        Assert.True(vm.CanRun);

        var services = new DataCopyServices { Precheck = new ThrowingPrecheck() };
        var failing = new PrecheckViewModel(services);
        failing.Items.Add(new CheckItemViewModel(new CheckItem(null, "OLD", CheckLevel.Pass, "옛 결과"), []));
        await failing.RunAsync(Sessions(), TestJobs.Job("점검"), Period);

        Assert.False(failing.CanRun); // 점검이 실패하면 옛 결과로 실행할 수 없다
        Assert.Null(failing.Report);
        Assert.Empty(failing.Items);
        Assert.False(string.IsNullOrEmpty(failing.Notice));
    }

    [Fact]
    public async Task Same_source_and_target_is_rejected()
    {
        var h = new DataCopyHarness();
        var job = TestJobs.Job("같은 접속", source: "SRC", target: "SRC", TestJobs.Rule("ORDERS"));
        var vm = await h.OpenAsync(job);

        await vm.GoToPrecheckAsync();

        Assert.Equal(DataCopyStep.RuleEdit, vm.Step);
        Assert.True(vm.StatusIsError);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task Recreate_on_a_non_development_target_is_rejected()
    {
        var h = new DataCopyHarness();
        var rule = TestJobs.Rule("ORDERS") with { Action = TargetAction.Recreate };
        var vm = await h.OpenAsync(TestJobs.Job("다시 만들기 거부", rules: rule)); // 대상 TGT = 테스트 환경

        await vm.GoToPrecheckAsync();

        Assert.Equal(DataCopyStep.RuleEdit, vm.Step);
        Assert.Contains("개발", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task Target_relabelled_as_production_while_open_blocks_the_precheck()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(TestJobs.Job("라벨 바뀜", rules: TestJobs.Rule("ORDERS")));
        h.Profiles[1] = h.Target with { Environment = ConnectionEnvironment.Production }; // 접속 관리자에서 운영으로 바꿈

        await vm.GoToPrecheckAsync();

        Assert.Equal(DataCopyStep.RuleEdit, vm.Step);
        Assert.Empty(h.Opened); // 세션도 열지 않는다
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Reenable_uses_the_target_saved_in_the_job_not_the_unsaved_edit()
    {
        var h = new DataCopyHarness();
        h.RunLog.Unfinished.Add(new UnfinishedRun("/runs/3.jsonl", DateTimeOffset.UnixEpoch, [new DisabledObject(DisabledKind.Trigger, "APP", "ORDERS", "TRG")]));
        var vm = await h.OpenAsync(TestJobs.Job("저장된 대상", rules: TestJobs.Rule("ORDERS")));
        vm.Editor.Target = vm.Editor.TargetOptions.Single(o => o.Profile.Name == "SRC"); // 저장하지 않은 편집

        await vm.ReenableAsync(Assert.Single(vm.UnfinishedRuns));

        Assert.Equal(["TGT"], h.Opened.Select(p => p.Name));
    }

    [Fact]
    public async Task Closing_the_window_waits_for_the_run_to_stop_before_closing_sessions()
    {
        var h = new DataCopyHarness();
        var releaseRunner = new TaskCompletionSource();
        var runnerFinished = false;
        h.Runner.Behavior = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default); // 취소되면 정리를 시작한다
            await releaseRunner.Task; // 트리거·FK 다시 켜기 같은 정리
            runnerFinished = true;
            return TestJobs.Result(CopyOutcome.Stopped);
        };
        var vm = await h.OpenAsync(TestJobs.Job("닫기", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();
        var run = vm.StartRunAsync();
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var closing = vm.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.False(closing.IsCompleted); // 정리가 끝나기 전에는 세션을 닫지 않는다
        Assert.All(h.OpenedSessions, s => Assert.NotEqual(SessionState.Closed, s.State));

        releaseRunner.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(10));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(runnerFinished);
        Assert.All(h.OpenedSessions, s => Assert.Equal(SessionState.Closed, s.State));
    }

    [Fact]
    public async Task Opening_another_job_is_refused_while_a_run_is_in_progress()
    {
        var h = new DataCopyHarness();
        var release = new TaskCompletionSource<CopyRunResult>();
        h.Runner.Behavior = _ => release.Task;
        var vm = await h.OpenAsync(TestJobs.Job("도는 작업", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();
        var run = vm.StartRunAsync();
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var other = TestJobs.Job("다른 작업", rules: TestJobs.Rule("CODES"));

        await vm.OpenJobAsync(other);

        Assert.NotEqual(other.Id, vm.CurrentJob?.Id);
        Assert.False(vm.CanGoToPrecheck);
        Assert.True(vm.StatusIsError);

        release.SetResult(TestJobs.Result());
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<DataCopyViewModel> PrecheckedAsync(DataCopyHarness h, string name = "점검한 작업")
    {
        var vm = await h.OpenAsync(TestJobs.Job(name, rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();
        Assert.True(vm.Precheck.CanRun);
        return vm;
    }

    [Fact]
    public async Task Run_is_refused_when_target_became_production_after_the_precheck()
    {
        var h = new DataCopyHarness();
        var vm = await PrecheckedAsync(h);
        h.Profiles[1] = h.Target with { Environment = ConnectionEnvironment.Production }; // 점검 뒤 접속 관리자에서 운영으로 바꿈

        await vm.StartRunAsync();

        Assert.Equal(0, h.Runner.Calls); // 운영 라벨 접속에는 쓰지 않는다
        Assert.False(vm.Precheck.CanRun); // 점검 결과도 지웠다
        Assert.True(vm.StatusIsError);
        Assert.All(h.OpenedSessions, s => Assert.Equal(SessionState.Closed, s.State)); // 옛 세션도 닫았다
    }

    [Fact]
    public async Task Retry_of_failed_rows_is_refused_when_target_became_production()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed,
            new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "1" }, "ORA-00001")));
        var vm = await PrecheckedAsync(h);
        await vm.StartRunAsync();
        Assert.True(vm.Run.CanRetry);
        h.Profiles[1] = h.Target with { Environment = ConnectionEnvironment.Production };
        var retryCalls = h.Runner.RetryCalls;

        await vm.RetryFailedRowsAsync();

        Assert.Equal(retryCalls, h.Runner.RetryCalls);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Retry_right_after_a_run_is_not_refused_when_only_the_last_run_summary_changed()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed,
            new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "1" }, "ORA-00001")));
        var vm = await PrecheckedAsync(h);
        await vm.StartRunAsync();
        Assert.True(vm.Run.CanRetry);
        Assert.NotNull(vm.CurrentJob!.LastRun); // 복사가 끝나면 열린 작업에 마지막 실행 요약이 합쳐진다
        var retryCalls = h.Runner.RetryCalls;

        await vm.RetryFailedRowsAsync(); // 창이 부르는 경로(직전 확인 포함)

        Assert.Equal(retryCalls + 1, h.Runner.RetryCalls);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task Retry_after_a_run_is_refused_when_the_open_job_changed()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed,
            new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "1" }, "ORA-00001")));
        var vm = await PrecheckedAsync(h);
        vm.Jobs.Selected = vm.Jobs.Items.First(i => i.Job.Id == vm.CurrentJob!.Id);
        await vm.StartRunAsync();
        Assert.True(vm.Run.CanRetry);
        await vm.RenameSelectedAsync("복사 뒤에 바꾼 이름"); // 마지막 실행 요약 말고 작업 내용이 바뀌었다
        var retryCalls = h.Runner.RetryCalls;

        await vm.RetryFailedRowsAsync();

        Assert.Equal(retryCalls, h.Runner.RetryCalls);
        Assert.True(vm.StatusIsError);
        Assert.Contains("다시 점검하세요", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_validation_on_recheck_clears_the_old_report()
    {
        var h = new DataCopyHarness();
        var vm = await PrecheckedAsync(h);

        vm.Editor.Target = null; // 다시 점검할 때 입력 확인이 실패한다
        await vm.GoToPrecheckAsync();
        await vm.StartRunAsync();

        Assert.Null(vm.Precheck.Report);
        Assert.False(vm.Precheck.CanRun);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public async Task Sessions_are_reopened_when_target_environment_or_read_only_changes()
    {
        var h = new DataCopyHarness();
        var vm = await PrecheckedAsync(h);
        Assert.Equal(2, h.Opened.Count);

        h.Profiles[1] = h.Target with { Environment = ConnectionEnvironment.Development }; // 테스트 → 개발
        await vm.GoToPrecheckAsync();
        Assert.Equal(4, h.Opened.Count);
        Assert.Equal(ConnectionEnvironment.Development, h.Opened[3].Environment); // 정책 관문이 새 프로필로 판단하도록 다시 열었다

        h.Profiles[1] = h.Profiles[1] with { ReadOnly = true };
        await vm.GoToPrecheckAsync();
        Assert.Equal(4, h.Opened.Count); // 읽기 전용 대상은 점검 전에 막아 새 세션을 열지 않는다
        Assert.True(vm.StatusIsError);
        Assert.Equal(DataCopyText.ReadOnlyTarget, vm.StatusText);
    }

    [Fact]
    public async Task Job_choice_clears_the_report_first_and_stays_cleared_when_saving_fails()
    {
        var h = new DataCopyHarness();
        var item = new CheckItem("ORDERS", "CHILD_FK", CheckLevel.Confirm, "자식 행이 남을 수 있습니다.") { Choices = [CheckChoice.SwitchToMerge] };
        h.Precheck = new FakePrecheck(TestJobs.Report([item], preview: [Statement("DELETE FROM \"APP\".\"ORDERS\"", PolicyDecisionKind.Confirm)]));
        var vm = await PrecheckedAsync(h);
        Assert.Contains("DELETE FROM \"APP\".\"ORDERS\"", vm.Precheck.BuildDecisions().PreConfirmedSql);
        h.Store.SaveFailure = new IOException("디스크 가득 참");

        await vm.ApplyChoiceAsync(vm.Precheck.Items.Single(), CheckChoice.SwitchToMerge);

        Assert.Equal(TargetAction.MergeByPk, vm.CurrentJob!.Tables.Single().Action); // 바뀐 작업
        Assert.Null(vm.Precheck.Report); // 옛 점검(DeleteAllInsert 의 DELETE)으로는 실행할 수 없다
        Assert.Empty(vm.Precheck.BuildDecisions().PreConfirmedSql);
        Assert.False(vm.CanStartRun);
        await vm.StartRunAsync();
        Assert.Equal(0, h.Runner.Calls);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Run_is_refused_while_a_recheck_is_in_progress()
    {
        var h = new DataCopyHarness();
        var vm = await PrecheckedAsync(h);
        var gate = new TaskCompletionSource();
        h.Precheck.Gate = gate; // 두 번째 점검이 끝나지 않는다
        var recheck = vm.GoToPrecheckAsync();
        await Task.Delay(100);

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanStartRun);
        await vm.StartRunAsync();
        Assert.Equal(0, h.Runner.Calls);

        gate.SetResult();
        await recheck.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.CanStartRun);
    }

    [Fact]
    public async Task Close_gives_up_waiting_after_the_limit_and_keeps_the_warning_for_the_status_bar()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = async _ =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None); // 취소를 무시하는 실행기
            return TestJobs.Result();
        };
        var vm = await PrecheckedAsync(h);
        var run = vm.StartRunAsync();
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var old = DataCopyViewModel.StopWait;
        DataCopyViewModel.StopWait = TimeSpan.FromMilliseconds(200);
        try
        {
            await vm.DisposeAsync();
        }
        finally
        {
            DataCopyViewModel.StopWait = old;
        }

        Assert.False(string.IsNullOrEmpty(vm.ClosingWarning)); // 닫힌 창이 아니라 셸 상태 줄로 보낼 경고
        Assert.All(h.OpenedSessions, s => Assert.Equal(SessionState.Closed, s.State));
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public async Task Check_items_list_errors_first_with_level_icons()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report(
        [
            new CheckItem(null, "A", CheckLevel.Pass, "통과"),
            new CheckItem("T", "B", CheckLevel.Confirm, "확인"),
            new CheckItem("T", "C", CheckLevel.Error, "오류"),
        ]));
        var vm = new PrecheckViewModel(h.Services());

        await vm.RunAsync(Sessions(), TestJobs.Job("정렬"), Period);

        Assert.Equal(["오류", "확인", "통과"], vm.Items.Select(i => i.Message));
        Assert.Equal(["error", "warn", "ok"], vm.Items.Select(i => i.IconKey));
    }

    private static CheckItem Candidate(string sequence = "ORDERS_SEQ") =>
        new("ORDERS", "SequenceGap", CheckLevel.Pass, $"ORDERS.ID: 시퀀스 후보 {sequence}. 작업에 연결하면 복사 뒤 값을 맞춥니다.")
        {
            Column = "ID",
            Sequence = sequence,
        };

    [Fact]
    public async Task Sequence_candidate_is_unlinked_until_checked_then_saved_to_the_job()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([Candidate()]))
        {
            // 운영 점검처럼: 작업에 이미 연결된 표·열은 후보로 내지 않는다.
            ReportFor = job => TestJobs.Report(job.Sequences.Any(s => s.Table == "ORDERS" && s.Column == "ID") ? [] : [Candidate()]),
        };
        var vm = await PrecheckedAsync(h);

        var link = Assert.IsType<SequenceLinkViewModel>(vm.Precheck.Items.Single().SequenceLink);
        Assert.False(link.IsLinked); // 기본은 꺼짐
        Assert.Empty(vm.CurrentJob!.Sequences);
        Assert.Empty(vm.Precheck.LinkedSequences);

        link.IsLinked = true;
        await vm.SetSequenceLinkAsync(link, true);

        var rule = Assert.Single(vm.CurrentJob!.Sequences);
        Assert.Equal(new SequenceRule("ORDERS_SEQ", "ORDERS", "ID"), rule);
        Assert.Equal([rule], h.Store.Jobs[vm.CurrentJob.Id].Sequences); // 작업 파일에 함께 저장
        Assert.Equal([rule], vm.Editor.BuildJob().Sequences); // 규칙 편집으로 돌아가도 남는다
        Assert.Equal(2, h.Precheck.Calls); // 연결한 규칙이 점검되도록 다시 점검
        Assert.True(vm.Precheck.CanRun);
        Assert.Equal("연결됨", Assert.Single(vm.Precheck.LinkedSequences).StateText);
        Assert.DoesNotContain(vm.Precheck.Items, i => i.HasSequenceLink); // 연결한 뒤에는 후보가 사라지고 연결 목록으로 옮겨 간다
    }

    [Fact]
    public async Task Linked_sequence_can_be_unlinked_and_the_job_file_follows()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report());
        var rule = new SequenceRule("ORDERS_SEQ", "ORDERS", "ID");
        var job = TestJobs.Job("연결된 작업", rules: TestJobs.Rule("ORDERS")) with { Sequences = [rule] };
        var vm = await h.OpenAsync(job);
        await vm.GoToPrecheckAsync();

        var link = Assert.Single(vm.Precheck.LinkedSequences);
        Assert.True(link.IsLinked); // 이미 작업에 있는 규칙은 연결됨으로 보인다

        link.IsLinked = false;
        await vm.SetSequenceLinkAsync(link, false);

        Assert.Empty(vm.CurrentJob!.Sequences);
        Assert.Empty(h.Store.Jobs[job.Id].Sequences);
        Assert.Empty(vm.Precheck.LinkedSequences);
    }

    [Fact]
    public async Task Sequence_link_is_reverted_while_a_check_is_running()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([Candidate()]));
        var vm = await PrecheckedAsync(h);
        var link = vm.Precheck.Items.Single().SequenceLink!;
        var gate = new TaskCompletionSource();
        h.Precheck.Gate = gate;
        var recheck = vm.GoToPrecheckAsync(); // 점검이 도는 중
        await Task.Delay(100);

        link.IsLinked = true;
        await vm.SetSequenceLinkAsync(link, true);

        Assert.False(link.IsLinked); // 지금은 바꿀 수 없어 체크를 되돌린다
        Assert.Empty(vm.CurrentJob!.Sequences);
        gate.SetResult();
        await recheck;
    }

    [Fact]
    public async Task Pass_item_without_candidate_sequence_has_no_link()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([new CheckItem("ORDERS", "SequenceGap", CheckLevel.Confirm, "최대값을 읽지 못했습니다.") { Column = "ID" }]));
        var vm = await PrecheckedAsync(h);

        Assert.Null(vm.Precheck.Items.Single().SequenceLink);
    }

    [Fact]
    public async Task Read_only_target_stops_before_the_precheck_with_a_notice()
    {
        var h = new DataCopyHarness();
        h.Profiles[1] = h.Target with { ReadOnly = true };
        var vm = await h.OpenAsync(TestJobs.Job("읽기 전용 대상", rules: TestJobs.Rule("ORDERS")));

        Assert.Equal(DataCopyText.ReadOnlyTarget, vm.Editor.TargetReadOnlyNotice);
        Assert.True(vm.Editor.HasTargetReadOnlyNotice);
        Assert.Equal(DataCopyText.ReadOnlyTarget, vm.Editor.Validate());
        Assert.False(vm.CanGoToPrecheck);

        await vm.GoToPrecheckAsync();

        Assert.Equal(DataCopyStep.RuleEdit, vm.Step);
        Assert.Empty(h.Opened);
        Assert.Equal(0, h.Precheck.Calls);
        Assert.Equal(DataCopyText.ReadOnlyTarget, vm.StatusText);
        Assert.True(vm.StatusIsError);

        vm.Editor.Target = vm.Editor.TargetOptions.First(o => !o.Profile.ReadOnly && o.IsEnabled); // 쓰기 가능한 접속으로 바꾸면 풀린다
        Assert.False(vm.Editor.HasTargetReadOnlyNotice);
        Assert.True(vm.CanGoToPrecheck);
    }

    private sealed class ThrowingPrecheck : ICopyPrecheckService
    {
        public Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period, IProgress<string>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("점검 실패");
    }
}
