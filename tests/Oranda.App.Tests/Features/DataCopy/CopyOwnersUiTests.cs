using Oranda.App.Features.DataCopy;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>묶음 B 화면: 규칙마다 원본·대상 계정, 시노님으로 표 더하기, 기본 계정 바꾸기, 규칙 키로 찾는 점검·진행, 실행 확인.</summary>
public sealed class CopyOwnersUiTests
{
    private static readonly ResolvedPeriod Period = new(null, null);

    private static CopySessions Sessions(string targetUser = "tgt_user") =>
        new(new FakeSession(), new FakeSession(TestJobs.Profile("TGT", Oranda.Core.Connection.ConnectionEnvironment.Test, 2) with { User = targetUser }));

    // v2 작업: 기본 계정 APP→APP, 다른 계정 APP2 의 ORDERS(시노님 ORDERS 로 더함), 부모는 APP.CUSTOMERS.
    private static CopyJob MixedJob() => TestJobs.Job("두 계정", "SRC", "TGT",
        TestJobs.Rule("CUSTOMERS"),
        TestJobs.Rule("ORDERS", RuleKind.ParentKey) with
        {
            SourceOwner = "APP2",
            TargetOwner = "APP2",
            WrittenAs = "ORDERS",
            Parent = new ParentLink("CUSTOMERS", ["CUST_ID"], ["CUST_ID"], "FK_ORDERS_CUST") { ParentOwner = "APP" },
        },
        TestJobs.Rule("CODES") with { SourceOwner = "SRC2", TargetOwner = "DST" }) with
    {
        Sequences = [new SequenceRule("ORDERS_SEQ", "APP2.ORDERS", "ORDER_ID")],
    };

    [Fact]
    public async Task Owners_written_as_and_parent_owner_survive_opening_and_saving_a_job()
    {
        // 다른 계정 규칙이 기본 계정으로 바뀌어 저장되면 DST.ORDERS 대신 엉뚱한 표를 지운다: 화면 → BuildJob 왕복이 계정을 지켜야 한다.
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(MixedJob());

        var orders = vm.Editor.Rules.Single(r => r.Table == "ORDERS");
        Assert.Equal("APP2", orders.SourceOwner);
        Assert.Equal("APP2", orders.TargetOwner);
        Assert.Equal("APP2.ORDERS", orders.SourceName);
        Assert.True(orders.HasSynonym);
        Assert.Contains("ORDERS", orders.SynonymText, StringComparison.Ordinal);
        Assert.Equal("APP.CUSTOMERS 의 키 · CUST_ID", orders.ConditionText); // 부모가 다른 계정이면 계정.표
        Assert.Equal("APP", vm.Editor.Rules[0].SourceOwner); // 비었던 계정은 기본 계정으로 풀어 보인다

        var built = vm.Editor.BuildJob();
        var saved = built.Tables.Single(r => r.Table == "ORDERS");
        Assert.Equal("APP2", saved.SourceOwner);
        Assert.Equal("APP2", saved.TargetOwner);
        Assert.Equal("ORDERS", saved.WrittenAs);
        Assert.Equal("APP", saved.Parent!.ParentOwner);
        Assert.All(built.Tables, r => Assert.False(string.IsNullOrEmpty(r.SourceOwner))); // 규칙마다 계정을 적어 저장한다
        Assert.All(built.Tables, r => Assert.False(string.IsNullOrEmpty(r.TargetOwner)));
        Assert.Equal("DST", built.Tables.Single(r => r.Table == "CODES").TargetOwner);
        Assert.Equal("APP2.ORDERS", Assert.Single(built.Sequences).Table);

        await vm.GoToPrecheckAsync(); // 점검 전 저장도 같은 값
        var stored = h.Store.Jobs[built.Id].Tables.Single(r => r.Table == "ORDERS");
        Assert.Equal(("APP2", "APP2"), (stored.SourceOwner, stored.TargetOwner));
    }

    [Fact]
    public async Task Changing_a_default_schema_moves_only_the_rules_that_used_it()
    {
        var h = new DataCopyHarness();
        var job = MixedJob() with { Sequences = [new SequenceRule("CUST_SEQ", "CUSTOMERS", "CUST_ID")] };
        var vm = await h.OpenAsync(job);

        vm.Editor.SourceOwner = "APP9";
        vm.Editor.TargetOwner = "QA";

        var customers = vm.Editor.Rules.Single(r => r.Table == "CUSTOMERS");
        var orders = vm.Editor.Rules.Single(r => r.Table == "ORDERS");
        var codes = vm.Editor.Rules.Single(r => r.Table == "CODES");
        Assert.Equal(("APP9", "QA"), (customers.SourceOwner, customers.TargetOwner)); // 옛 기본(APP→APP)을 쓰던 규칙
        Assert.Equal(("APP2", "APP2"), (orders.SourceOwner, orders.TargetOwner)); // 다른 계정 규칙은 그대로
        Assert.Equal(("SRC2", "DST"), (codes.SourceOwner, codes.TargetOwner));

        var built = vm.Editor.BuildJob();
        Assert.Equal("CUSTOMERS", Assert.Single(built.Sequences).Table); // 기본 계정의 표 키는 여전히 이름만
        Assert.Equal("APP9", built.SourceOwner);
    }

    [Fact]
    public async Task Typed_synonym_is_resolved_on_the_source_and_saved_with_its_real_schema()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("APP", "ORDERS")] = NameStep.Synonym("APP2", "ORDERS");
        h.Metadata.NameSteps[("APP2", "ORDERS")] = NameStep.TableOrView;
        h.Metadata.NameSteps[("APP", "CODES")] = NameStep.TableOrView;
        h.Metadata.Tables[new DbObjectRef("APP2", "ORDERS", "TABLE")] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        h.Metadata.Tables[new DbObjectRef("APP", "CODES", "TABLE")] = TestJobs.Table("CODES", TestJobs.Column("CODE", "VARCHAR2", 1));
        var vm = await h.OpenAsync(TestJobs.Job("시노님"));

        await vm.AddTableInputAsync("orders");

        var rule = Assert.Single(vm.Editor.Rules);
        Assert.Equal(("APP2", "ORDERS", "ORDERS"), (rule.SourceOwner, rule.Table, rule.WrittenAs));
        Assert.Equal("APP2", rule.TargetOwner); // 결정 B-1: 다른 계정이면 원본 계정 이름 그대로
        Assert.True(rule.HasColumns); // 풀린 계정에서 열 정보를 읽었다
        Assert.Contains("APP2.ORDERS", vm.StatusText, StringComparison.Ordinal);
        Assert.True(Assert.Single(h.Opened).ReadOnly);

        await vm.AddTableInputAsync("APP2.ORDERS"); // 같은 원본 계정·표면 기존 규칙을 고르기만 한다
        Assert.Single(vm.Editor.Rules);

        await vm.AddTableInputAsync("CODES");
        var codes = vm.Editor.Rules.Single(r => r.Table == "CODES");
        Assert.Equal(("APP", "APP"), (codes.SourceOwner, codes.TargetOwner)); // 기본 원본 계정의 표 → 기본 대상 계정
        Assert.Null(codes.WrittenAs);
    }

    [Fact]
    public async Task Unresolvable_names_and_db_link_synonyms_are_not_added()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("APP", "REMOTE")] = NameStep.Synonym("APP", "ORDERS", "PROD_LINK");
        var vm = await h.OpenAsync(TestJobs.Job("못 풂"));

        await vm.AddTableInputAsync("NOPE");
        Assert.Empty(vm.Editor.Rules);
        Assert.True(vm.StatusIsError);
        Assert.Contains("원본에서 찾을 수 없습니다", vm.StatusText, StringComparison.Ordinal);

        await vm.AddTableInputAsync("REMOTE");
        Assert.Empty(vm.Editor.Rules);
        Assert.Contains("DB 링크", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("원본 접속으로 고르세요", vm.StatusText, StringComparison.Ordinal);

        await vm.AddTableInputAsync("ORDERS@PROD_LINK");
        Assert.Empty(vm.Editor.Rules);
        Assert.Contains("DB 링크", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_source_session_the_typed_name_is_added_under_the_written_or_default_schema()
    {
        var h = new DataCopyHarness { RefuseOpen = _ => true }; // 비밀번호 입력 취소
        var vm = await h.OpenAsync(TestJobs.Job("세션 없음"));

        await vm.AddTableInputAsync("ORDERS");
        await vm.AddTableInputAsync("app2.items");

        Assert.Equal(["APP.ORDERS", "APP2.ITEMS"], vm.Editor.Rules.Select(r => r.SourceName));
        Assert.Equal("APP2", vm.Editor.Rules[1].TargetOwner);
        Assert.False(vm.Editor.Rules[0].HasColumns);
    }

    [Fact]
    public async Task Schema_browser_synonym_node_is_resolved_before_it_is_added()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("PUBLIC", "ORDERS")] = NameStep.Synonym("APP2", "ORDERS");
        h.Metadata.NameSteps[("APP2", "ORDERS")] = NameStep.TableOrView;
        h.Metadata.Tables[new DbObjectRef("APP2", "ORDERS", "TABLE")] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        var vm = await h.OpenAsync(TestJobs.Job("브라우저 시노님"));

        await vm.AddTableAsync(new DbObjectRef("PUBLIC", "ORDERS", "SYNONYM"), h.Source);

        var rule = Assert.Single(vm.Editor.Rules);
        Assert.Equal(("APP2", "ORDERS", "PUBLIC.ORDERS"), (rule.SourceOwner, rule.Table, rule.WrittenAs));
    }

    [Fact]
    public async Task Validate_reports_two_rules_writing_one_target_and_a_target_that_is_read_as_a_source()
    {
        var h = new DataCopyHarness();
        var job = TestJobs.Job("모순", "SRC", "TGT",
            TestJobs.Rule("ORDERS"),
            TestJobs.Rule("ORDERS") with { SourceOwner = "APP2", TargetOwner = "APP" });
        var vm = await h.OpenAsync(job);

        Assert.Contains("APP.ORDERS", vm.Editor.Validate(), StringComparison.Ordinal); // 대상 표 하나에 규칙 둘

        vm.Editor.Rules[1].TargetOwner = "APP2";
        Assert.Null(vm.Editor.Validate());

        // 같은 접속: APP2.ORDERS 를 읽는 규칙이 있는데 다른 규칙이 APP2.ORDERS 에 쓴다.
        vm.Editor.Target = vm.Editor.TargetOptions.Single(o => o.Profile.Name == "SRC");
        vm.Editor.Rules[0].TargetOwner = "APP2";
        vm.Editor.Rules[1].TargetOwner = "QA";
        var problem = vm.Editor.Validate();
        Assert.NotNull(problem);
        Assert.Contains("APP2.ORDERS", problem, StringComparison.Ordinal);

        vm.Editor.Rules[0].TargetOwner = "QA2";
        Assert.Null(vm.Editor.Validate());
    }

    [Fact]
    public void Target_schema_choices_merge_same_schemas_and_the_rule_knows_when_it_writes_elsewhere()
    {
        var h = new DataCopyHarness();
        var editor = new RuleEditorViewModel(h.Services());
        editor.SetConnections([h.Source, h.Target]);
        editor.Load(MixedJob(), h.Source, h.Target);

        var orders = editor.Rules.Single(r => r.Table == "ORDERS");
        var options = editor.TargetOwnerOptions(orders);
        Assert.Equal(["APP2", "APP", "TGT_USER"], options.Select(o => o.Owner));
        Assert.Equal("APP2", options[0].ToString()); // 고르면 칸 글자가 계정 이름
        Assert.True(orders.IsTargetSameAsSource);
        Assert.True(orders.IsOtherTargetOwner); // 대상 접속 계정은 TGT_USER
        Assert.False(string.IsNullOrEmpty(orders.OtherTargetOwnerNotice));

        orders.TargetOwner = "tgt_user";
        Assert.False(orders.IsOtherTargetOwner);
        Assert.Equal(2, editor.TargetOwnerOptions(editor.Rules[0]).Count); // APP(원본·기본 대상 합침), TGT_USER
    }

    [Fact]
    public async Task Precheck_rows_find_plans_by_rule_key_and_show_schema_dot_table()
    {
        var job = MixedJob();
        var item = new CheckItem("APP2.ORDERS", "TargetTriggers", CheckLevel.Confirm, "트리거") { Choices = [CheckChoice.DisableTriggers] };
        var plain = new CheckItem("CUSTOMERS", "SequenceGap", CheckLevel.Pass, "후보") { Column = "CUST_ID", Sequence = "CUST_SEQ" };
        var whole = new CheckItem(null, CopyCheckCodes.OtherOwnerTargets, CheckLevel.Confirm, "다른 계정");
        var plans = new[]
        {
            TestJobs.Plan("CUSTOMERS", canDisableTriggers: false),
            new CopyTablePlan(job.Tables[1], TestJobs.Table("ORDERS"), TestJobs.Table("ORDERS"), [], []) { CanDisableTriggers = true },
        };
        var h = new DataCopyHarness { Precheck = new FakePrecheck(TestJobs.Report([item, plain, whole], plans)) };
        var precheck = new PrecheckViewModel(h.Services());
        await precheck.RunAsync(Sessions(), job, Period);

        var row = precheck.Items.Single(i => i.Table == "APP2.ORDERS");
        Assert.Equal("APP2.ORDERS", row.TableText);
        Assert.True(Assert.Single(row.Choices).IsVisible); // 계획을 규칙 키로 찾았다(같은 이름 다른 계정과 섞이지 않음)
        Assert.Equal("APP.CUSTOMERS", precheck.Items.Single(i => i.Table == "CUSTOMERS").TableText);
        Assert.Equal(string.Empty, precheck.Items.Single(i => i.Table is null).TableText);
        Assert.Equal("다른 계정 표에 씀", precheck.Items.Single(i => i.Table is null).CodeText);

        row.Choices[0].IsChosen = true;
        Assert.Equal(["APP2.ORDERS"], precheck.BuildDecisions().DisableTriggerTables);

        var changed = PrecheckViewModel.ApplyJobChoice(job, row, CheckChoice.SwitchToMerge)!;
        Assert.Equal(TargetAction.MergeByPk, changed.Tables[1].Action);
        Assert.NotEqual(TargetAction.MergeByPk, changed.Tables[0].Action);
    }

    [Fact]
    public void Run_confirmation_is_asked_only_for_several_target_schemas_or_a_non_session_schema()
    {
        var single = TestJobs.Job("한 계정", rules: TestJobs.Rule("ORDERS")) with { TargetOwner = "TGT_USER" };
        Assert.Null(RunConfirmation.For(single, "TGT_USER"));
        Assert.NotNull(RunConfirmation.For(single, "OTHER"));

        var mixed = MixedJob();
        var confirmation = RunConfirmation.For(mixed, "APP")!;
        Assert.Equal(["APP", "APP2", "DST"], confirmation.Owners.Select(o => o.Owner));
        Assert.True(confirmation.Owners[0].IsSessionUser);
        Assert.Equal(1, confirmation.Owners[1].Tables);
        Assert.Contains("3", confirmation.Header, StringComparison.Ordinal);
        Assert.Contains("APP2 · 표 1개 · 같은 범위 지우고 넣기 1", RunConfirmation.LineText(confirmation.Owners[1]), StringComparison.Ordinal);

        var excluded = single with { Tables = [TestJobs.Rule("ORDERS", RuleKind.Exclude) with { TargetOwner = "ELSE" }, .. single.Tables] };
        Assert.Null(RunConfirmation.For(excluded, "TGT_USER")); // 제외 규칙은 지우지 않는다
    }

    [Fact]
    public async Task Run_waits_for_the_confirmation_and_cancel_keeps_the_target_untouched()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(MixedJob());
        await vm.GoToPrecheckAsync();
        Assert.True(vm.CanStartRun);

        RunConfirmation? asked = null;
        vm.ConfirmRun = c =>
        {
            asked = c;
            return Task.FromResult(false);
        };
        await vm.StartRunAsync();
        Assert.NotNull(asked);
        Assert.Equal(0, h.Runner.Calls);
        Assert.Equal(DataCopyStep.Precheck, vm.Step);

        vm.ConfirmRun = _ => Task.FromResult(true);
        await vm.StartRunAsync();
        Assert.Equal(1, h.Runner.Calls);
    }

    [Fact]
    public async Task Run_rows_are_keyed_by_rule_key_and_show_schema_dot_table()
    {
        var h = new DataCopyHarness();
        var job = MixedJob();
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(CopyOutcome.PartiallyFailed,
            [new TableRunResult("APP2.ORDERS", 5, 4, TimeSpan.FromSeconds(1), CopyOutcome.PartiallyFailed, null)],
            [new FailedRow("APP2.ORDERS", new Dictionary<string, string?> { ["ORDER_ID"] = "9" }, "ORA-00001")], [], DateTimeOffset.UnixEpoch));
        var run = new RunViewModel(h.Services());
        var report = TestJobs.Report(order: [TestJobs.Plan("CUSTOMERS"), new CopyTablePlan(job.Tables[1], TestJobs.Table("ORDERS"), TestJobs.Table("ORDERS"), [], [])]);

        await run.StartAsync(Sessions(), job, report, new CopyDecisions(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), false), Period);

        Assert.Equal(["CUSTOMERS", "APP2.ORDERS"], run.Rows.Select(r => r.Table));
        Assert.Equal(["APP.CUSTOMERS", "APP2.ORDERS"], run.Rows.Select(r => r.Display));
        Assert.Equal(4, run.Rows[1].TargetRows); // 결과가 키로 같은 줄에 붙었다
        Assert.Equal("APP2.ORDERS", Assert.Single(run.FailedRows).Display);
    }
}
