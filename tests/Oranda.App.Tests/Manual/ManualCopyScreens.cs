using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.DataCopy;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.DataCopy;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 「데이터 복사」 장 화면 캡처 생성기(<c>transfer/data-copy.md</c>).
/// DB 에 아무것도 쓰지 않는다. 접속·표·점검 결과·실행 결과는 모두 지어낸 예시 값(가짜 서비스)이고, 실행 단추는 누르지 않는다.
/// 점검 항목 문구와 SQL 은 실제 점검 서비스·SQL 생성기가 만드는 모양을 따른다.
/// 시각은 PC 시간대와 상관없이 같은 글자로 보이게 고정한다.
/// 번호의 뜻은 <c>transfer/data-copy.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualCopyScreens : IDisposable
{
    private readonly IShellText? _previousText = UiText.Current;

    public void Dispose() => UiText.Current = _previousText;

    private static readonly Guid ProdId = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TestId = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid DevId = new("00000000-0000-0000-0000-0000000000a3");

    private static readonly ConnectionProfile Prod = new("prod-db", "acme", "db.example.com:1521/prodsvc", Id: ProdId, Environment: ConnectionEnvironment.Production);
    // 대상 접속 계정 = 예시 작업의 기본 대상 계정(ACME_SALES): 다른 계정에 쓰는 규칙만 경고색으로 보인다(묶음 B).
    private static readonly ConnectionProfile Test = new("test-db", "acme_sales","db.example.com:1521/testsvc", Id: TestId, Environment: ConnectionEnvironment.Test);

    /// <summary>PC 시간대와 상관없이 같은 시계 글자로 보이는 시각(화면은 <c>LocalDateTime</c> 으로 그린다).</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified));

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(20);
            Pump();
            window.UpdateLayout();
        }
    }

    private static T Named<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    private static T ByAutomationName<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => AutomationProperties.GetName(c) == name);

    /// <summary>화면에 보이는 글자가 같은 TextBlock 을 화면 순서대로 모두 돌려준다(꺼 둔 표지는 뺀다).</summary>
    private static List<TextBlock> AllText(Visual root, string text) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text == text)];

    /// <summary>화면에 실제로 보이는 컨트롤 중 글자(TextBlock 이나 단추 내용)가 <paramref name="text"/> 인 첫 컨트롤. 숨은 단계 화면의 같은 글자는 건너뛴다.</summary>
    private static Control VisibleText(Visual root, string text, Visual? under = null) =>
        (under ?? root).GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.IsEffectivelyVisible && c switch
        {
            TextBlock t => t.Text == text,
            ContentControl { Content: string s } => s == text,
            _ => false,
        }) ?? throw new Xunit.Sdk.XunitException($"보이는 글자 '{text}' 를 가진 컨트롤을 찾지 못했다.");

    /// <summary>그리드의 칸(보이는 행·열 순서, 0부터) 왼쪽 위를 기준으로 번호를 둔다. 그리드 칸은 그리드가 직접 그려 컨트롤이 없다.</summary>
    private static ManualCapture.Callout GridCell(MonitorGrid grid, int number, int row, int col = 0, double dx = -12, double dy = 0)
    {
        var c = grid.GridControl;
        var m = grid.Model ?? throw new Xunit.Sdk.XunitException($"그리드 {grid.Name} 에 결과가 없다.");
        var x = c.RowHeaderWidth + Enumerable.Range(0, col).Sum(i => m.GetColumnWidth(m.VisibleColumns[i]));
        var y = row < 0 ? 0 : c.HeaderHeight + (row * c.RowHeight);
        return new(c, number, x + dx, y + (c.RowHeight / 2) + dy);
    }

    private static CheckBox CheckByLabel(Visual root, string label) =>
        ManualCapture.Find<CheckBox>(root, c => c.Content is TextBlock { Text: { } t } && t == label);

    // ---- 지어낸 예시 데이터 --------------------------------------------------------------------

    private static TableInfo OrdersTable() => TestJobs.Table("ORDERS",
        TestJobs.Column("ORDER_ID", "NUMBER", 1),
        TestJobs.Column("ORDER_DT", "VARCHAR2", 2, length: 8),
        TestJobs.Column("CUST_NAME", "VARCHAR2", 3, length: 50),
        TestJobs.Column("AMOUNT", "NUMBER", 4),
        TestJobs.Column("MEMO", "VARCHAR2", 5, length: 200),
        TestJobs.Column("CREATED_AT", "TIMESTAMP(6)", 6),
        TestJobs.Column("UPDATED_AT", "TIMESTAMP(6)", 7));

    private static IReadOnlyList<TableRule> ExampleRules() =>
    [
        new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert)
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT", "YYYYMMDD"), new PeriodColumn("UPDATED_AT")],
            Masks = [new MaskRule("CUST_NAME", MaskKind.Hash)],
            SkipColumns = ["MEMO"],
            ExtraWhere = "STATUS <> 'CANCELED'",
        },
        new TableRule("ORDER_ITEMS", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            Parent = new ParentLink("ORDERS", ["ORDER_ID"], ["ORDER_ID"], "FK_ITEMS_ORDERS"),
        },
        new TableRule("SHIPMENTS", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "STATUS = 'DONE' AND SHIP_DT >= :FROM_DT" },
        new TableRule("CODES", RuleKind.All, TargetAction.MergeByPk),
        new TableRule("AUDIT_LOG", RuleKind.All, TargetAction.DeleteAllInsert),
        new TableRule("TEMP_WORK", RuleKind.Exclude, TargetAction.DeleteAllInsert),
    ];

    private static CopyJob ExampleJob(string id, string name, DateTimeOffset? lastRun = null, CopyOutcome outcome = CopyOutcome.Succeeded, long rows = 0, IReadOnlyList<TableRule>? rules = null, IReadOnlyList<SequenceRule>? sequences = null) => new()
    {
        Id = id,
        Name = name,
        Source = new ConnectionRef(Prod.Name, Prod.User, Prod.Id),
        Target = new ConnectionRef(Test.Name, Test.User, Test.Id),
        SourceOwner = "ACME_SALES",
        TargetOwner = "ACME_SALES",
        MaskSalt = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        Period = new CopyPeriod(PeriodKind.LastMonth),
        Tables = rules ?? [],
        Sequences = sequences ?? [],
        Options = new CopyOptions { DisableTriggersByDefault = true },
        LastRun = lastRun is { } at ? new LastRunInfo(at, outcome, rows) : null,
    };

    private const string MainJobId = "20261106main";

    /// <summary>내 작업 목록에 보일 작업 넷. 첫째가 화면에서 여는 작업이다.</summary>
    private static void SeedJobs(DataCopyHarness h)
    {
        var jobs = new[]
        {
            ExampleJob(MainJobId, "월말 테스트 DB 갱신", Local(2026, 11, 5, 18, 20), CopyOutcome.Succeeded, 61_420, ExampleRules(), [new SequenceRule("SEQ_CODE_ID", "CODES", "CODE_ID")]),
            ExampleJob("20261102mask", "고객 마스킹 복사", Local(2026, 11, 2, 9, 5), CopyOutcome.PartiallyFailed, 880),
            ExampleJob("20261028code", "코드 표 동기화", Local(2026, 10, 28, 14, 40), CopyOutcome.Succeeded, 320),
            ExampleJob("20261020ord3", "주문 3개월 복사"),
        };
        foreach (var job in jobs)
        {
            h.Store.Jobs[job.Id] = job;
        }
    }

    private static async Task<(DataCopyWindow Window, DataCopyViewModel Vm, Canvas Overlay)> OpenWindowAsync(DataCopyHarness h)
    {
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        LiteralTranslator.Install(text);

        h.Profiles.Clear();
        h.Profiles.AddRange([Prod, Test]);
        SeedJobs(h);

        var vm = new DataCopyViewModel(h.Services());
        await vm.InitializeAsync();
        await vm.OpenJobAsync(h.Store.Jobs[MainJobId]);
        vm.Jobs.Selected = vm.Jobs.Items.First(i => i.Job.Id == MainJobId);
        vm.Editor.Rules[0].SetSource(OrdersTable());
        vm.Editor.SelectedRule = vm.Editor.Rules[0];

        var window = new DataCopyWindow { DataContext = vm };
        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        var overlay = ManualCapture.AddOverlay(window); // 내용을 옮기면 목록 항목이 다시 만들어지므로 번호 겹침판을 먼저 얹는다
        return (window, vm, overlay);
    }

    private static CopySessions Sessions() => new(new FakeSession(Prod with { ReadOnly = true }), new FakeSession(Test));

    // ---- 1단계: 규칙 편집 ----------------------------------------------------------------------

    [Fact]
    public Task Rule_edit() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var h = new DataCopyHarness();
        var (window, _, overlay) = await OpenWindowAsync(h);
        try
        {
            ManualCapture.SaveOverlaid(window, overlay, "data-copy-rules",
            [
                new(Named<ListBox>(window, "JobList"), 1, 190, 10),
                new(Named<TextBox>(window, "AddTableBox"), 2),
                new(Named<ComboBox>(window, "SourceBox"), 3),
                new(Named<ComboBox>(window, "TargetBox"), 4),
                new(ByAutomationName<ComboBox>(window, "기간 종류"), 5),
                new(ByAutomationName<ComboBox>(window, "커밋 단위"), 6),
                GridCell(Named<MonitorGrid>(window, "RuleGrid"), 7, 0),
                new(Named<ComboBox>(window, "KindBox"), 8),
                new(VisibleText(window, "마스킹"), 9, -14, 0),
                new(Named<Button>(window, "ToPrecheckButton"), 10, -12, 0),
            ]);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 1단계: 여러 계정·시노님 ------------------------------------------------------------------

    [Fact]
    public Task Rule_edit_with_other_schemas() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var h = new DataCopyHarness();
        var (window, vm, overlay) = await OpenWindowAsync(h);
        try
        {
            // 시노님 ORDER_HIST 로 더한 다른 계정의 표(ACME_ARCH.ORDER_HIST)와 대상 계정을 바꾼 코드 표.
            var job = ExampleJob(MainJobId, "월말 테스트 DB 갱신", rules:
            [
                new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_DT", "YYYYMMDD")] },
                new TableRule("ORDER_HIST", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
                {
                    SourceOwner = "ACME_ARCH",
                    TargetOwner = "ACME_ARCH",
                    WrittenAs = "ORDER_HIST",
                    Parent = new ParentLink("ORDERS", ["ORDER_ID"], ["ORDER_ID"], "FK_HIST_ORDERS") { ParentOwner = "ACME_SALES" },
                },
                new TableRule("CODES", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "ACME_COMMON", TargetOwner = "ACME_SALES" },
            ]);
            h.Store.Jobs[MainJobId] = job;
            await vm.OpenJobAsync(job);
            vm.Editor.SelectedRule = vm.Editor.Rules[1];
            Settle(window);

            var grid = Named<MonitorGrid>(window, "RuleGrid");
            ManualCapture.SaveOverlaid(window, overlay, "data-copy-owners",
            [
                new(ByAutomationName<TextBox>(window, "원본 기본 계정"), 1),
                new(ByAutomationName<TextBox>(window, "대상 기본 계정"), 2),
                GridCell(grid, 3, -1, 0),
                GridCell(grid, 4, 1, 2),
                new(Named<TextBlock>(window, "RuleHeaderText"), 5, -14, 0),
                new(Named<ComboBox>(window, "TargetOwnerBox"), 6),
            ]);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 2단계: 사전 점검 ----------------------------------------------------------------------

    [Fact]
    public Task Precheck() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var h = new DataCopyHarness();

        // 점검 항목 문구는 CopyPrecheckService·PrecheckRules 가 내는 모양을 따른다.
        var items = new[]
        {
            new CheckItem("ORDERS", "UnsupportedType", CheckLevel.Error,
                "ORDERS.MEMO_XML: 복사가 지원하지 않는 형식(XMLTYPE)입니다. 이 열을 건너뛰거나 규칙에서 빼세요.")
            {
                Column = "MEMO_XML",
                Choices = [CheckChoice.SkipColumn],
            },
            new CheckItem("ORDERS", "TargetTriggers", CheckLevel.Confirm,
                "ORDERS: 대상 트리거 2개가 복사한 행에도 실행됩니다. 복사하는 동안 끄고 끝나면 다시 켤 수 있습니다.")
            {
                Choices = [CheckChoice.DisableTriggers],
            },
            new CheckItem("CODES", "ChildRowsMayBlock", CheckLevel.Confirm,
                "CODES: 자식 표 ORDER_ITEMS(FK_ITEMS_CODES) 의 행이 남아 있으면 지울 때 ORA-02292 가 날 수 있습니다. 대상 처리를 PK 기준 MERGE 로 바꾸세요.")
            {
                Choices = [CheckChoice.SwitchToMerge, CheckChoice.DisableForeignKeys],
            },
            new CheckItem("ORDERS", "NarrowerType", CheckLevel.Confirm,
                "ORDERS.CUST_NAME: 대상 길이(30)가 원본(50)보다 짧아 값이 잘리거나 실패할 수 있습니다.") { Column = "CUST_NAME" },
            new CheckItem("CODES", "SequenceGap", CheckLevel.Confirm,
                "CODES.CODE_ID: 복사 뒤 시퀀스 SEQ_CODE_ID 를 약 1,250번 소비해 값을 앞당깁니다.") { Column = "CODE_ID" },
            new CheckItem("ORDERS", "MaskedColumns", CheckLevel.Pass, "ORDERS: 가리는 열 CUST_NAME(해시)"),
            new CheckItem("ORDERS", "SequenceGap", CheckLevel.Pass,
                "ORDERS.ORDER_ID: 시퀀스 후보 SEQ_ORDER_ID. 작업에 연결하면 복사 뒤 값을 맞춥니다.") { Column = "ORDER_ID", Sequence = "SEQ_ORDER_ID" },
        };

        var job = ExampleJob(MainJobId, "월말 테스트 DB 갱신", rules: ExampleRules(), sequences: [new SequenceRule("SEQ_CODE_ID", "CODES", "CODE_ID")]);
        var orders = OrdersTable();
        var period = PeriodCalculator.Resolve(job.Period, new DateTime(2026, 11, 6));
        var builder = new CopySqlBuilder(job, period,
            new Dictionary<string, TableInfo> { ["ORDERS"] = orders }, new Dictionary<string, TableInfo> { ["ORDERS"] = orders });
        // 사전 점검 미리 보기는 원본 조회, 대상 DELETE, 끄는 문장, 시퀀스 소비만 보인다(INSERT·MERGE 는 없다).
        var preview = new[]
        {
            new PreviewStatement(builder.SourceSelect("ORDERS", ["ORDER_ID", "ORDER_DT", "CUST_NAME", "AMOUNT", "UPDATED_AT"]).Sql, PreviewSide.Source, PolicyDecisionKind.Allow, null, null),
            new PreviewStatement(builder.TargetDelete("ORDERS")!.Sql, PreviewSide.Target, PolicyDecisionKind.Allow, null, null),
            new PreviewStatement(builder.TargetDelete("AUDIT_LOG")!.Sql, PreviewSide.Target, PolicyDecisionKind.Confirm, "조건 없는 DELETE", null),
            new PreviewStatement("ALTER TRIGGER \"ACME_SALES\".\"TRG_ORDERS_AUDIT\" DISABLE", PreviewSide.Target, PolicyDecisionKind.Allow, null, null),
            new PreviewStatement("SELECT \"ACME_SALES\".\"SEQ_CODE_ID\".NEXTVAL FROM DUAL CONNECT BY LEVEL <= :N", PreviewSide.Target, PolicyDecisionKind.Allow, null, null),
        };
        var codesPlan = TestJobs.Plan("CODES") with
        {
            DisablableForeignKeys = [new DisabledObject(DisabledKind.ForeignKey, "ACME_SALES", "ORDER_ITEMS", "FK_ITEMS_CODES")],
        };
        h.Precheck = new FakePrecheck(TestJobs.Report(items, [TestJobs.Plan("ORDERS", canDisableTriggers: true), codesPlan], preview));

        var (window, vm, overlay) = await OpenWindowAsync(h);
        try
        {
            await vm.Precheck.RunAsync(Sessions(), vm.CurrentJob!, period);
            vm.Step = DataCopyStep.Precheck;
            Settle(window);
            vm.Precheck.SelectedPreview = vm.Precheck.Preview.First(p => p.IsConfirm); // 아래 칸에 조건 없는 DELETE 전체가 보이게
            Settle(window);

            var checks = Named<MonitorGrid>(window, "CheckGrid");
            var sql = Named<MonitorGrid>(window, "SqlGrid");
            ManualCapture.SaveOverlaid(window, overlay, "data-copy-precheck",
            [
                new(VisibleText(window, "점검 결과"), 1, -14, 0),
                new(VisibleText(window, "연결된 시퀀스(복사 뒤 값을 맞춥니다)"), 2, -14, 0),
                GridCell(checks, 3, 0),
                GridCell(checks, 4, 1),
                new(CheckByLabel(window, "복사하는 동안 트리거를 끄고 끝나면 다시 켜기"), 5, -14, -4),
                new(VisibleText(window, "SQL 미리 보기 (대상에서 실행할 문장과 원본 조회)"), 6, -14, 0),
                GridCell(sql, 7, vm.Precheck.Preview.IndexOf(vm.Precheck.SelectedPreview!), 1),
                new(ByAutomationName<TextBox>(window, "고른 문장"), 8, 0, 4),
                new(Named<Button>(window, "BackToRulesButton"), 9, -8, 0),
                new(Named<Button>(window, "RunButton"), 10, -10, 0),
            ]);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 2단계: 사전 점검(테이블 다시 만들기) ---------------------------------------------------

    [Fact]
    public Task Precheck_recreate() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var h = new DataCopyHarness();
        var dev = new ConnectionProfile("dev-db", "acme_dev", "db.example.com:1521/devsvc", Id: DevId, Environment: ConnectionEnvironment.Development);

        // 점검 항목 문구는 CopyPrecheckService.CheckRecreateAsync 가 내는 모양을 따른다.
        var items = new[]
        {
            new CheckItem("CODES", "RecreateTable", CheckLevel.Confirm,
                "CODES: 대상 표를 지우고 원본 구조로 다시 만든 뒤 넣습니다. 지운 표는 휴지통에 남습니다(PURGE 없음, 공간이 부족하면 휴지통 객체가 자동으로 지워질 수 있음)."
                + " DROP·CREATE 는 DDL 이라 커밋 방식과 상관없이 되돌릴 수 없습니다."
                + " 대상의 트리거 1개·다른 계정에 준 권한·이 표를 참조하던 FK 는 적재 뒤 되살립니다"
                + "(참조 FK 는 지우기 전에 떼어 내고, 대상 계정에 보이지 않는 FK 가 있으면 지우지 않고 멈춥니다)."
                + " 원본과 다르게 만들어지는 것: 문자 길이는 글자 단위(CHAR), DEFAULT ON NULL·IDENTITY 옵션(ALWAYS·시작·증가)·DEFERRABLE PK/UK·COLLATE 은 빠지고,"
                + " 파티션·IOT·임시 표는 일반 표가 됩니다. 되살리지 않는 것: 열 권한 2개."),
            new CheckItem("CODES", "MaskedColumns", CheckLevel.Pass, "CODES: 가리는 열이 없습니다."),
        };

        var rules = new TableRule[] { new("CODES", RuleKind.All, TargetAction.Recreate) };
        var job = ExampleJob(MainJobId, "월말 테스트 DB 갱신", rules: rules);
        var codes = TestJobs.Table("CODES",
            TestJobs.Column("CODE_ID", "NUMBER", 1),
            TestJobs.Column("CODE_NAME", "VARCHAR2", 2, length: 50));
        var period = PeriodCalculator.Resolve(job.Period, new DateTime(2026, 11, 6));
        var builder = new CopySqlBuilder(job, period,
            new Dictionary<string, TableInfo> { ["CODES"] = codes }, new Dictionary<string, TableInfo> { ["CODES"] = codes });
        var preview = new[]
        {
            new PreviewStatement(builder.SourceSelect("CODES", ["CODE_ID", "CODE_NAME"]).Sql, PreviewSide.Source, PolicyDecisionKind.Allow, null, null),
            new PreviewStatement("DROP TABLE \"ACME_SALES\".\"CODES\"", PreviewSide.Target, PolicyDecisionKind.Confirm,
                "위험 명령 DROP 입니다. 계속하려면 대상 이름 'CODES' 을(를) 다시 입력하세요.", "CODES"),
        };
        h.Precheck = new FakePrecheck(TestJobs.Report(items, [TestJobs.Plan("CODES")], preview));

        var (window, vm, overlay) = await OpenWindowAsync(h);
        try
        {
            await vm.Precheck.RunAsync(new CopySessions(new FakeSession(Prod with { ReadOnly = true }), new FakeSession(dev)), vm.CurrentJob!, period);
            vm.Step = DataCopyStep.Precheck;
            Settle(window);

            ManualCapture.SaveOverlaid(window, overlay, "data-copy-recreate",
            [
                GridCell(Named<MonitorGrid>(window, "CheckGrid"), 1, 0),
                new(VisibleText(window, "SQL 미리 보기 (대상에서 실행할 문장과 원본 조회)"), 2, -14, 0),
                GridCell(Named<MonitorGrid>(window, "SqlGrid"), 3, vm.Precheck.Preview.ToList().FindIndex(p => p.IsConfirm), 1),
                new(ByAutomationName<TextBox>(window, "대상 접속 이름 다시 입력"), 4),
                new(Named<Button>(window, "RunButton"), 5, -10, 0),
            ]);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 3단계: 실행·검증 ----------------------------------------------------------------------

    [Fact]
    public Task Run_result() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var h = new DataCopyHarness();
        var failed = new FailedRow("ORDERS", new Dictionary<string, string?> { ["ORDER_ID"] = "100342" },
            "ORA-02291: 부모 키 없음(FK_ORDERS_CUST)");
        var failed2 = new FailedRow("ORDERS", new Dictionary<string, string?> { ["ORDER_ID"] = "100977" },
            "ORA-02291: 부모 키 없음(FK_ORDERS_CUST)");
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(
            CopyOutcome.PartiallyFailed,
            [
                new TableRunResult("CODES", 120, 120, TimeSpan.FromSeconds(1), CopyOutcome.Succeeded, null),
                new TableRunResult("ORDERS", 15_000, 14_998, TimeSpan.FromSeconds(9), CopyOutcome.PartiallyFailed, "2행 실패"),
                new TableRunResult("ORDER_ITEMS", 42_300, 42_300, TimeSpan.FromSeconds(20), CopyOutcome.Succeeded, null),
            ],
            [failed, failed2],
            ["FK ACME_SALES.FK_ITEMS_ORDERS: 부모 없는 행 3 때문에 ENABLE NOVALIDATE 로 켰습니다."],
            Local(2026, 11, 6, 12, 0, 0)));
        var (window, vm, overlay) = await OpenWindowAsync(h);
        try
        {
            var report = TestJobs.Report(order: [TestJobs.Plan("CODES"), TestJobs.Plan("ORDERS"), TestJobs.Plan("ORDER_ITEMS")]);
            await vm.Run.StartAsync(Sessions(), vm.CurrentJob!, report,
                new CopyDecisions(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), false),
                PeriodCalculator.Resolve(vm.CurrentJob!.Period, new DateTime(2026, 11, 6)));
            vm.Step = DataCopyStep.Run;
            Settle(window);

            ManualCapture.SaveOverlaid(window, overlay, "data-copy-run",
            [
                new(Named<Button>(window, "BackFromRunButton"), 1, 6, 24),
                new(Named<Button>(window, "PauseButton"), 2, 6, 24),
                new(Named<Button>(window, "RetryButton"), 3, 6, 24),
                new(Named<Button>(window, "CsvButton"), 4, 6, 24),
                GridCell(Named<MonitorGrid>(window, "RunGrid"), 5, 0),
                GridCell(Named<MonitorGrid>(window, "RunGrid"), 6, -1, 3),
                GridCell(Named<MonitorGrid>(window, "FailedGrid"), 7, 0),
                new(VisibleText(window, "FK ACME_SALES.FK_ITEMS_ORDERS: 부모 없는 행 3 때문에 ENABLE NOVALIDATE 로 켰습니다."), 8, -14, 0),
            ]);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 내 작업과 끝나지 않은 지난 실행 ---------------------------------------------------------

    [Fact]
    public Task Jobs_and_unfinished_run() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var h = new DataCopyHarness();
        h.RunLog.Unfinished.Add(new UnfinishedRun("run-1", Local(2026, 11, 5, 18, 20),
        [
            new DisabledObject(DisabledKind.Trigger, "ACME_SALES", "ORDERS", "TRG_ORDERS_AUDIT"),
            new DisabledObject(DisabledKind.ForeignKey, "ACME_SALES", "ORDER_ITEMS", "FK_ITEMS_ORDERS"),
        ]));
        var (window, _, overlay) = await OpenWindowAsync(h);
        try
        {
            ManualCapture.SaveOverlaid(window, overlay, "data-copy-jobs",
            [
                new(Named<Button>(window, "NewJobButton"), 1, 198, 0),
                new(VisibleText(window, "월말 테스트 DB 갱신", under: Named<ListBox>(window, "JobList")), 2, 130, 0),
                new(VisibleText(window, "지난 실행이 끝나지 않았습니다. 대상 트리거·FK 상태를 확인하세요."), 3, -14, 0),
                new(VisibleText(window, "트리거 ACME_SALES.ORDERS.TRG_ORDERS_AUDIT"), 4, -14, 0),
                new(VisibleText(window, "다시 켜기"), 5, -24, 0),
            ]);
        }
        finally
        {
            window.Close();
        }
    });
}
