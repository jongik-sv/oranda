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
        Assert.Empty(CopyOwners.Problems(built)); // 시퀀스 연결이 규칙을 잃지 않는다
        Assert.Null(vm.Editor.Validate());

        // 다른 계정 규칙의 키(APP2.ORDERS)도 기본 원본 계정이 APP2 가 되면 이름만(ORDERS)으로 다시 잇는다.
        var other = await h.OpenAsync(MixedJob());
        other.Editor.SourceOwner = "APP2";
        var rekeyed = other.Editor.BuildJob();
        Assert.Equal("ORDERS", Assert.Single(rekeyed.Sequences).Table);
        Assert.Same(rekeyed.Tables.Single(r => r.Table == "ORDERS"), CopyOwners.Find(rekeyed, "ORDERS"));
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

        RuleEditorViewModel.ChooseTargetOwner(orders, " tgt_user "); // 적은 이름은 사전 이름(대문자)으로
        Assert.Equal("TGT_USER", orders.TargetOwner);
        Assert.False(orders.IsOtherTargetOwner);
        RuleEditorViewModel.ChooseTargetOwner(orders, "\"tgt_user\""); // 따옴표로 감싼 이름만 그대로: 다른 계정이다
        Assert.Equal("tgt_user", orders.TargetOwner);
        Assert.True(orders.IsOtherTargetOwner);
        RuleEditorViewModel.ChooseTargetOwner(orders, "TGT_USER");
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
    public async Task Disabling_foreign_keys_keeps_the_schema_of_each_constraint()
    {
        // 같은 이름 FK 가 두 계정에 있다. 한 표의 「FK 끄기」를 고르면 그 표 계획의 FK(계정, 이름)만 결정에 든다.
        var job = MixedJob();
        var item = new CheckItem("APP2.ORDERS", "TargetFkBlocks", CheckLevel.Confirm, "FK") { Choices = [CheckChoice.DisableForeignKeys] };
        var plans = new[]
        {
            TestJobs.Plan("CUSTOMERS") with { DisablableForeignKeys = [new DisabledObject(DisabledKind.ForeignKey, "APP", "ITEMS", "FK_ITEMS")] },
            new CopyTablePlan(job.Tables[1], TestJobs.Table("ORDERS"), TestJobs.Table("ORDERS"), [], [])
            {
                DisablableForeignKeys = [new DisabledObject(DisabledKind.ForeignKey, "APP2", "ITEMS", "FK_ITEMS")],
            },
        };
        var h = new DataCopyHarness { Precheck = new FakePrecheck(TestJobs.Report([item], plans)) };
        var precheck = new PrecheckViewModel(h.Services());
        await precheck.RunAsync(Sessions(), job, Period);

        var choice = Assert.Single(precheck.Items.Single(i => i.Table == "APP2.ORDERS").Choices);
        Assert.True(choice.IsVisible);
        choice.IsChosen = true;
        Assert.Equal([new ForeignKeyRef("APP2", "FK_ITEMS")], precheck.BuildDecisions().DisableForeignKeys);
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
    public async Task Run_is_not_started_when_the_confirmation_cannot_be_asked()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(MixedJob());
        await vm.GoToPrecheckAsync();
        vm.ConfirmRun = null; // 창이 확인을 잇지 못했다

        await vm.StartRunAsync();
        Assert.Equal(0, h.Runner.Calls);
        Assert.True(vm.StatusIsError);
        Assert.Equal(DataCopyText.RunConfirmMissing, vm.StatusText);
    }

    [Fact]
    public async Task Connection_changed_while_the_confirmation_is_open_is_caught_before_the_run()
    {
        // 묻고 나서 직전 확인을 한다: 대화상자가 떠 있는 사이 대상이 운영 접속이 되면 실행하지 않는다.
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(MixedJob());
        await vm.GoToPrecheckAsync();
        var asked = 0;
        vm.ConfirmRun = _ =>
        {
            asked++;
            h.Profiles[1] = h.Target with { Environment = Oranda.Core.Connection.ConnectionEnvironment.Production };
            return Task.FromResult(true);
        };

        await vm.StartRunAsync();
        Assert.Equal(1, asked);
        Assert.Equal(0, h.Runner.Calls);
        Assert.False(vm.Precheck.CanRun); // 점검 결과를 지워 다시 점검하게 한다
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Read_only_state_comes_from_the_chosen_list_entry_not_the_job_id()
    {
        // 같은 ID 의 파일이 둘(하나는 새 버전 파일). 새 버전 파일을 고르면 ID 로 먼저 나온 보통 파일과 상관없이 읽기 전용이다.
        var h = new DataCopyHarness();
        var normal = TestJobs.Job("보통", rules: TestJobs.Rule("ORDERS"));
        var newer = normal with { Name = "새 버전" };
        var store = new TwoFileStore([new CopyJobEntry(normal, "/jobs/a.json", false, null), new CopyJobEntry(newer, "/jobs/b.json", true, null)]);
        var services = h.Services();
        var vm = new DataCopyViewModel(new DataCopyServices
        {
            Store = store,
            Precheck = services.Precheck,
            Runner = services.Runner,
            Metadata = services.Metadata,
            Time = services.Time,
            ProfileLoader = services.ProfileLoader,
            SessionOpener = services.SessionOpener,
        });
        await vm.InitializeAsync();

        vm.Jobs.Selected = vm.Jobs.Items[1];
        await vm.OpenJobAsync(vm.Jobs.Items[1].Job);
        Assert.True(vm.IsReadOnlyJob);

        vm.Jobs.Selected = vm.Jobs.Items[0];
        await vm.OpenJobAsync(vm.Jobs.Items[0].Job);
        Assert.False(vm.IsReadOnlyJob);

        // 목록에 없는 같은 ID 의 객체면 하나라도 읽기 전용이면 막는다.
        vm.Jobs.Selected = null;
        await vm.OpenJobAsync(normal with { Name = "다른 객체" });
        Assert.True(vm.IsReadOnlyJob);
    }

    [Fact]
    public async Task Typed_name_without_a_metadata_service_says_the_synonym_was_not_resolved()
    {
        var h = new DataCopyHarness { NoMetadata = true };
        var vm = await h.OpenAsync(TestJobs.Job("메타데이터 없음"));

        await vm.AddTableInputAsync("ORDERS");

        Assert.Equal("APP.ORDERS", Assert.Single(vm.Editor.Rules).SourceName);
        Assert.True(vm.StatusIsError);
        Assert.Contains("시노님", vm.StatusText, StringComparison.Ordinal);
    }

    private sealed class TwoFileStore(IReadOnlyList<CopyJobEntry> entries) : ICopyJobStore
    {
        public Task<IReadOnlyList<CopyJobEntry>> ListAsync(CancellationToken ct) => Task.FromResult(entries);

        public Task SaveAsync(CopyJob job, CancellationToken ct) => Task.CompletedTask;

        public Task<CopyJob> DuplicateAsync(string id, string newName, CancellationToken ct) => throw new NotSupportedException();

        public Task RenameAsync(string id, string newName, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;

        public Task ExportAsync(string id, string path, CancellationToken ct) => Task.CompletedTask;

        public Task<CopyJob> ImportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
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

        await run.StartAsync(Sessions(), job, report, new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false), Period);

        Assert.Equal(["CUSTOMERS", "APP2.ORDERS"], run.Rows.Select(r => r.Table));
        Assert.Equal(["APP.CUSTOMERS", "APP2.ORDERS"], run.Rows.Select(r => r.Display));
        Assert.Equal(4, run.Rows[1].TargetRows); // 결과가 키로 같은 줄에 붙었다
        Assert.Equal("APP2.ORDERS", Assert.Single(run.FailedRows).Display);
    }

    // ---- 기본 계정 따르기(리뷰 1·2·3·5·7·11·15) ----

    private static RuleEditorViewModel Editor(DataCopyHarness h, CopyJob job)
    {
        var editor = new RuleEditorViewModel(h.Services());
        editor.SetConnections([h.Source, h.Target]);
        editor.Load(job, h.Source, h.Target);
        return editor;
    }

    [Fact]
    public void Default_target_round_trip_returns_every_rule_to_its_own_target()
    {
        // 기본 대상 DST→SRC2→DST: 원래 SRC2 에 쓰던 CODES 가 DST 로 끌려가면 DST.CODES 를 지운다.
        var job = TestJobs.Job("왕복", "SRC", "TGT",
            TestJobs.Rule("ORDERS"),
            TestJobs.Rule("CODES") with { SourceOwner = "APP", TargetOwner = "SRC2" }) with { TargetOwner = "DST" };
        var editor = Editor(new DataCopyHarness(), job);
        var orders = editor.Rules.Single(r => r.Table == "ORDERS");
        var codes = editor.Rules.Single(r => r.Table == "CODES");
        Assert.True(orders.FollowsDefaultTarget);
        Assert.False(codes.FollowsDefaultTarget);

        editor.TargetOwner = "SRC2";
        Assert.Equal(("SRC2", "SRC2"), (orders.TargetOwner, codes.TargetOwner));
        editor.TargetOwner = "DST";
        Assert.Equal(("DST", "SRC2"), (orders.TargetOwner, codes.TargetOwner));

        var built = editor.BuildJob();
        Assert.Equal("SRC2", built.Tables.Single(r => r.Table == "CODES").TargetOwner);
        Assert.Equal("DST", built.Tables.Single(r => r.Table == "ORDERS").TargetOwner);
    }

    [Fact]
    public void Default_source_round_trip_keeps_the_other_schema_rule_on_its_schema()
    {
        // 원본 APP→APP2→APP: 시노님으로 더한 APP2.ORDERS 가 APP 를 읽게 되면 안 된다.
        var job = TestJobs.Job("원본 왕복", "SRC", "TGT",
            TestJobs.Rule("CUSTOMERS"),
            TestJobs.Rule("ORDERS") with { SourceOwner = "APP2", TargetOwner = "APP2", WrittenAs = "ORDERS" });
        var editor = Editor(new DataCopyHarness(), job);
        var customers = editor.Rules.Single(r => r.Table == "CUSTOMERS");
        var orders = editor.Rules.Single(r => r.Table == "ORDERS");

        editor.SourceOwner = "APP2";
        Assert.Equal(("APP2", "APP2"), (customers.SourceOwner, orders.SourceOwner));
        editor.SourceOwner = "APP";
        Assert.Equal(("APP", "APP"), (customers.SourceOwner, customers.TargetOwner));
        Assert.Equal(("APP2", "APP2"), (orders.SourceOwner, orders.TargetOwner));

        var built = editor.BuildJob();
        Assert.Equal(("APP2", "APP2"), (built.Tables[1].SourceOwner, built.Tables[1].TargetOwner));
        Assert.Equal(("APP", "APP"), (built.Tables[0].SourceOwner, built.Tables[0].TargetOwner));
    }

    [Fact]
    public void Choosing_a_target_stops_following_and_the_default_choice_follows_again()
    {
        var job = TestJobs.Job("고르기", "SRC", "TGT", TestJobs.Rule("ORDERS")) with { TargetOwner = "DST" };
        var editor = Editor(new DataCopyHarness(), job);
        var orders = editor.Rules[0];

        RuleEditorViewModel.ChooseTargetOwner(orders, "qa");
        Assert.Equal("QA", orders.TargetOwner);
        Assert.False(orders.FollowsDefaultTarget);
        editor.TargetOwner = "DST2";
        Assert.Equal("QA", orders.TargetOwner); // 고른 대상은 기본을 따라 바뀌지 않는다

        var options = editor.TargetOwnerOptions(orders);
        var byDefault = Assert.Single(options, o => o.IsDefault);
        Assert.Equal("DST2", byDefault.Owner);
        RuleEditorViewModel.ChooseTargetOwner(orders, byDefault.Owner, byDefault);
        Assert.True(orders.FollowsDefaultTarget);
        editor.TargetOwner = "DST";
        Assert.Equal("DST", orders.TargetOwner);

        // 접속 계정 항목을 고르면 따름이 풀린다.
        var session = Assert.Single(editor.TargetOwnerOptions(orders), o => o.Owner == "TGT_USER");
        RuleEditorViewModel.ChooseTargetOwner(orders, session.Owner, session);
        Assert.False(orders.FollowsDefaultTarget);
    }

    [Fact]
    public void Default_schema_text_is_trimmed_and_upper_cased_unless_quoted()
    {
        var editor = Editor(new DataCopyHarness(), TestJobs.Job("정리", "SRC", "TGT", TestJobs.Rule("ORDERS")));
        editor.SourceOwner = "  app9 ";
        Assert.Equal("APP9", editor.SourceOwner);
        Assert.Equal("APP9", editor.Rules[0].SourceOwner);
        editor.TargetOwner = "\"qa\"";
        Assert.Equal("qa", editor.TargetOwner);
        Assert.Equal("qa", editor.Rules[0].TargetOwner);
    }

    [Fact]
    public async Task Adding_a_table_of_an_existing_rule_ignores_schema_letter_case()
    {
        var h = new DataCopyHarness { RefuseOpen = _ => true };
        var vm = await h.OpenAsync(TestJobs.Job("대소문자", "SRC", "TGT", TestJobs.Rule("ORDERS") with { SourceOwner = "APP2", TargetOwner = "APP2" }));
        await vm.Editor.AddTableAsync(new CopyTableName("app2", "ORDERS"), null);
        Assert.Single(vm.Editor.Rules);
    }

    [Fact]
    public void Changing_the_default_source_moves_same_schema_parents_and_keeps_the_target_delete_scoped()
    {
        // 부모가 작업 밖으로 빠지면 대상 DELETE 가 부모 조건 없이 부모 표 전체 기준이 된다.
        var job = TestJobs.Job("부모", "SRC", "TGT",
            TestJobs.Rule("CUSTOMERS", RuleKind.Custom) with { CustomWhere = "REGION = 'K'" },
            TestJobs.Rule("ORDERS", RuleKind.ParentKey) with
            {
                Action = TargetAction.DeleteRangeInsert,
                Parent = new ParentLink("CUSTOMERS", ["CUST_ID"], ["CUST_ID"], "FK_ORDERS_CUST") { ParentOwner = "APP" },
            },
            TestJobs.Rule("ITEMS", RuleKind.ParentKey) with
            {
                Action = TargetAction.DeleteRangeInsert,
                Parent = new ParentLink("PRODUCTS", ["PROD_ID"], ["PROD_ID"], "FK_ITEMS_PROD") { ParentOwner = "MASTER" },
            }) with { TargetOwner = "DST" };
        var editor = Editor(new DataCopyHarness(), job);

        editor.SourceOwner = "APP9";
        var orders = editor.Rules.Single(r => r.Table == "ORDERS");
        Assert.Equal("APP9", orders.Parent!.ParentOwner); // 같은 계정 FK 의 부모는 자식과 함께 옮긴다
        Assert.Equal("MASTER", editor.Rules.Single(r => r.Table == "ITEMS").Parent!.ParentOwner); // 계정을 넘는 FK 는 그대로

        var built = editor.BuildJob();
        Assert.NotNull(CopyOwners.Parent(built, built.Tables[1])); // 부모가 여전히 작업 안
        var builder = new CopySqlBuilder(built, Period, new Dictionary<string, TableInfo>(), new Dictionary<string, TableInfo>());
        var delete = builder.TargetDelete("ORDERS")!.Sql;
        Assert.Equal("DELETE FROM \"DST\".\"ORDERS\" WHERE \"CUST_ID\" IN (SELECT \"CUST_ID\" FROM \"DST\".\"CUSTOMERS\" WHERE (REGION = 'K'))", delete);
        Assert.Contains("FROM \"MASTER\".\"PRODUCTS\"", builder.TargetDelete("ITEMS")!.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_owner_of_a_migrated_job_moves_with_the_rule_target()
    {
        // v1 변환 작업은 시퀀스 계정에 작업 대상 계정을 적어 둔다. 대상을 바꾸면 옛 계정의 시퀀스를 보정하면 안 된다.
        var job = TestJobs.Job("v1", "SRC", "TGT",
            TestJobs.Rule("CUSTOMERS") with { SourceOwner = "APP", TargetOwner = "DST" },
            TestJobs.Rule("ORDERS") with { SourceOwner = "APP", TargetOwner = "DST" }) with
        {
            TargetOwner = "DST",
            Sequences = [new SequenceRule("CUST_SEQ", "CUSTOMERS", "CUST_ID") { Owner = "DST" }, new SequenceRule("ORD_SEQ", "ORDERS", "ORDER_ID") { Owner = "SEQOWN" }],
        };
        var editor = Editor(new DataCopyHarness(), job);

        editor.TargetOwner = "QA";
        var built = editor.BuildJob();
        Assert.Equal("QA", built.Sequences[0].Owner);
        Assert.Equal("SEQOWN", built.Sequences[1].Owner); // 다른 계정에 둔 시퀀스는 그대로
        Assert.Equal("QA", CopyOwners.SequenceOwner(built, built.Sequences[0]));
    }

    [Fact]
    public void Removing_a_rule_removes_its_sequence_link()
    {
        var job = MixedJob() with { Sequences = [new SequenceRule("ORDERS_SEQ", "APP2.ORDERS", "ORDER_ID"), new SequenceRule("CUST_SEQ", "CUSTOMERS", "CUST_ID")] };
        var h = new DataCopyHarness();
        var editor = Editor(h, job);
        editor.RemoveRule(editor.Rules.Single(r => r.Table == "ORDERS"));

        var built = editor.BuildJob();
        Assert.Equal(["CUSTOMERS"], built.Sequences.Select(s => s.Table));
        Assert.Null(editor.Validate()); // 지운 규칙의 시퀀스 연결이 남아 점검으로 못 넘어가는 일이 없다
    }

    [Fact]
    public async Task After_a_run_the_new_base_job_still_relinks_sequences()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(MixedJob());
        await vm.GoToPrecheckAsync();
        vm.ConfirmRun = _ => Task.FromResult(true);
        await vm.StartRunAsync();
        Assert.Equal(1, h.Runner.Calls);

        vm.Editor.SourceOwner = "APP2"; // APP2.ORDERS 의 키가 ORDERS 로 바뀐다
        var built = vm.Editor.BuildJob();
        Assert.Equal("ORDERS", Assert.Single(built.Sequences).Table);
        Assert.Empty(CopyOwners.Problems(built));
    }

    [Fact]
    public void Empty_target_cell_uses_the_rule_default_target()
    {
        var h = new DataCopyHarness();
        var editor = Editor(h, TestJobs.Job("빈 칸", "SRC", "TGT", TestJobs.Rule("ORDERS")) with { TargetOwner = "DST" });
        var orders = editor.Rules[0];
        RuleEditorViewModel.ChooseTargetOwner(orders, "QA");

        RuleEditorViewModel.ChooseTargetOwner(orders, "  ");
        Assert.Equal(string.Empty, orders.TargetOwner);
        Assert.True(orders.FollowsDefaultTarget);
        Assert.True(orders.IsTargetDefaultShown);
        Assert.Equal("DST", orders.EffectiveTarget);
        Assert.True(orders.IsOtherTargetOwner); // DST ≠ 접속 계정 TGT_USER
        Assert.Equal("DST", editor.BuildJob().Tables[0].TargetOwner);

        editor.TargetOwner = "TGT_USER";
        Assert.Equal("TGT_USER", orders.EffectiveTarget);
        Assert.False(orders.IsOtherTargetOwner);
    }

    [Fact]
    public void Run_confirmation_compares_schema_names_letter_for_letter()
    {
        // 계정은 문장에 따옴표로 그대로 들어간다: "tgt_user" 는 접속 계정 TGT_USER 가 아니다.
        var job = TestJobs.Job("글자", rules: TestJobs.Rule("ORDERS") with { TargetOwner = "tgt_user" });
        var confirmation = RunConfirmation.For(job, "TGT_USER");
        Assert.NotNull(confirmation);
        Assert.False(Assert.Single(confirmation!.Owners).IsSessionUser);

        var two = job with { Tables = [.. job.Tables, TestJobs.Rule("CODES") with { TargetOwner = "TGT_USER" }] };
        Assert.Equal(2, RunConfirmation.For(two, "TGT_USER")!.Owners.Count);
    }
}
