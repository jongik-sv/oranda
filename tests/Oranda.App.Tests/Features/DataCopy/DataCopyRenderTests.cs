using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.DataCopy;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// 데이터 복사 창 화면(Headless): 3단계가 각각 글자 잘림 없이 그려지고 단계에 맞는 영역만 보인다.
/// <c>ORANDA_RENDER_DIR</c> 이 있으면 단계마다 라이트·다크 PNG(<c>datacopy-*.png</c>)를 남긴다.
/// </summary>
[Collection("UiLanguage")]
public sealed class DataCopyRenderTests : IDisposable
{
    private readonly IShellText? _previousText = UiText.Current;

    public void Dispose() => UiText.Current = _previousText;

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
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(20);
            Pump();
            window.UpdateLayout();
        }
    }

    private static void Shots(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"datacopy-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    private static CopySessions Sessions() => new(new FakeSession(), new FakeSession());

    private static async Task<(DataCopyWindow Window, DataCopyViewModel Vm)> OpenWindowAsync(DataCopyHarness h, bool readColumns = true)
    {
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        LiteralTranslator.Install(text);

        // 규칙 표에 보일 테이블: 기간(문자열 날짜·생성 시각), 전체, 부모 키, 직접 조건.
        var orders = TestJobs.Table("ORDERS",
            TestJobs.Column("ORDER_ID", "NUMBER", 1),
            TestJobs.Column("ORDER_DT", "VARCHAR2", 2, length: 8),
            TestJobs.Column("CUST_NAME", "VARCHAR2", 3, length: 50),
            TestJobs.Column("CREATED_AT", "TIMESTAMP(6)", 4),
            TestJobs.Column("UPDATED_AT", "TIMESTAMP(6)", 5));
        var rules = new[]
        {
            TestJobs.Rule("ORDERS", RuleKind.Period) with
            {
                PeriodColumns = [new PeriodColumn("ORDER_DT", "YYYYMMDD"), new PeriodColumn("UPDATED_AT")],
                Masks = [new MaskRule("CUST_NAME", MaskKind.Hash)],
                ExtraWhere = "STATUS <> 'CANCELED'",
            },
            TestJobs.Rule("ORDER_ITEMS", RuleKind.ParentKey),
            TestJobs.Rule("CODES"),
            TestJobs.Rule("AUDIT_LOG", RuleKind.Custom) with { CustomWhere = "LOG_DT >= :FROM_DT" },
        };
        var job = TestJobs.Job("월말 운영 데이터 복사", "SRC", "TGT", rules) with { Period = new CopyPeriod(PeriodKind.LastMonth) };
        var vm = await h.OpenAsync(job);
        if (readColumns)
        {
            vm.Editor.Rules[0].SetSource(orders);
        }

        vm.Editor.SelectedRule = vm.Editor.Rules[0];

        var window = new DataCopyWindow { DataContext = vm };
        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        return (window, vm);
    }

    /// <summary>컨트롤이 부모 칸보다 넓게 그려져 오른쪽이 잘리지 않는다(최소 폭이 칸 폭보다 클 때 생긴다).</summary>
    private static void AssertInsideParent(Control control)
    {
        var parent = (Control)control.GetVisualParent()!;
        Assert.True(control.Bounds.Right <= parent.Bounds.Width + 0.5,
            $"{AutomationProperties.GetName(control)}: 오른쪽 {control.Bounds.Right} > 부모 폭 {parent.Bounds.Width}");
        Assert.True(control.DesiredSize.Width <= control.Bounds.Width + 0.5,
            $"{AutomationProperties.GetName(control)}: 원하는 폭 {control.DesiredSize.Width} > 받은 폭 {control.Bounds.Width}");
    }

    private static void AssertNotClipped(Window window, string state)
    {
        var clipped = TextClipping.Find(window);
        Assert.True(clipped.Count == 0, $"{state}: " + string.Join(" | ", clipped));
    }

    [Fact]
    public Task Rule_edit_step_renders_without_clipping_and_shows_only_its_panel() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            Assert.True(window.FindControl<Button>("ToPrecheckButton")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("RunButton")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("StopButton")!.IsEffectivelyVisible);
            Assert.Equal(4, vm.Editor.Rules.Count);
            var grid = window.FindControl<MonitorGrid>("RuleGrid")!;
            Assert.Equal(4, grid.Model!.RowCount); // 규칙 표 = 조회 결과와 같은 그리드
            Assert.Equal(0, grid.Model.Active.Row); // 고른 규칙(ORDERS)이 현재 행 ▶
            AssertFillsWidth(grid);
            AssertNotClipped(window, "규칙 편집");
            Shots(window, "1-rules");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Job_variables_render_expanded_with_count_usage_and_problems() => HeadlessHost.Run(async () =>
    {
        // 작업 변수(코일 목록·MO 하나·잘못된 숫자): 변수 칸이 펼쳐지고 값 수·조건에 쓰는 꼴·문제를 보인다.
        var h = new DataCopyHarness();
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            var job = vm.Editor.BuildJob() with
            {
                Variables =
                [
                    new CopyVariable("COILS", CopyVariableType.Text, ["C2611001", "C2611002", "C2611003", "C2611004"]),
                    new CopyVariable("MO_NO", CopyVariableType.Text, ["MO-26-0042"]),
                    new CopyVariable("MAT_CD", CopyVariableType.Number, ["1001", "ABC"]),
                ],
                Tables = [.. vm.Editor.BuildJob().Tables.Select(t => t.Kind == RuleKind.Custom ? t with { CustomWhere = "COIL_NO IN (:COILS) AND MO_NO = :MO_NO" } : t)],
            };
            await vm.OpenJobAsync(job);
            vm.Editor.SelectedRule = vm.Editor.Rules.Single(r => r.Kind == RuleKind.Custom);
            Settle(window);

            var expander = window.FindControl<Expander>("VariablesExpander")!;
            Assert.False(expander.IsExpanded); // 변수가 있어도 접힌 채로 연다(머리줄의 변수 수로 안다)
            expander.IsExpanded = true;
            Settle(window);
            Assert.Equal(["4개", "1개", "2개"], vm.Editor.Variables.Select(v => v.CountText));
            Assert.Equal(["IN (:COILS)", "= :MO_NO", "IN (:MAT_CD)"], vm.Editor.Variables.Select(v => v.UsageText));
            Assert.Contains("숫자", vm.Editor.Variables[2].Problem, StringComparison.Ordinal);
            Assert.Contains("숫자", vm.Editor.Validate(), StringComparison.Ordinal); // 잘못된 값은 점검으로 넘어가지 못한다
            // 변수 줄마다 「조건에 넣기」 단추: 고른 규칙(직접 조건)이 WHERE 를 받으므로 켜져 있다.
            var inserts = expander.GetVisualDescendants().OfType<Button>().Where(b => Equals(ToolTip.GetTip(b), "고른 규칙의 WHERE 조건에 넣기")).ToList();
            Assert.Equal(3, inserts.Count);
            Assert.All(inserts, b => Assert.True(b.IsEffectivelyEnabled));
            AssertNotClipped(window, "변수");
            Shots(window, "1-variables");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Saved_job_shows_period_columns_before_columns_are_read() => HeadlessHost.Run(async () =>
    {
        // 저장된 작업을 열고 「열 정보 읽기」 전: 기간 열 콤보 둘과 형식 칸이 저장된 값을 보인다(빈칸이 아니다).
        var h = new DataCopyHarness();
        var (window, vm) = await OpenWindowAsync(h, readColumns: false);
        try
        {
            Assert.False(vm.Editor.Rules[0].HasColumns);
            var first = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "기간 열");
            var second = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "두 번째 기간 열");
            var format = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "문자열 날짜 형식");
            Assert.Equal("ORDER_DT", Assert.IsType<PeriodColumnOption>(first.SelectedItem).Column);
            Assert.Equal(0, first.SelectedIndex);
            Assert.Equal("UPDATED_AT", Assert.IsType<PeriodColumnOption>(second.SelectedItem).Column);
            Assert.True(format.IsEffectivelyVisible);
            Assert.Equal("YYYYMMDD", format.Text);
            AssertNotClipped(window, "열 정보 읽기 전");
            Shots(window, "1-rules-saved-before-columns");
        }
        finally
        {
            window.Close();
        }
    });

    // 그리드가 직접 그리는 칸은 TextClipping 이 못 본다: 마지막 열을 뺀 모든 열이 값 글자 수만큼 넓은지(그리드 자동 크기와 같은 계산, 상한 360) 본다.
    private static void AssertColumnsFit(MonitorGrid grid)
    {
        var m = grid.Model!;
        var cw = new Oranda.App.Features.ResultGrid.GridTextFactory(new Avalonia.Media.Typeface(grid.GridControl.GridFontFamily), grid.GridControl.GridFontSize).CharWidth;
        for (var c = 0; c < m.Columns.Count - 1; c++)
        {
            for (var r = 0; r < m.RowCount; r++)
            {
                var text = m.GetCellText(r, c);
                var need = Math.Min(360, (Oranda.App.Features.ResultGrid.ResultGridModel.DisplayWidth(text) * cw) + 16);
                Assert.True(m.GetColumnWidth(c) >= need - 0.5, $"{grid.Name} 열 {m.Columns[c].Name}: '{text}' 에 {need} 가 필요한데 {m.GetColumnWidth(c)}");
            }
        }
    }

    // 마지막 열이 남은 폭에 맞아 그리드 오른쪽에 빈 끝 칸도, 가로 스크롤도 생기지 않는다(열 폭 합 ≈ 그리드 폭, ±2).
    // 앞 열들이 넓어 마지막 열 자리가 최소값(GridColumnFit 의 120)보다 좁을 때만 가로 스크롤을 허용하고, 그때도 빈 끝 칸은 없어야 한다.
    // 반환: 남은 폭에 맞추는 경우(엄격 검사)였는가.
    private static bool AssertFillsWidth(MonitorGrid grid)
    {
        var c = grid.GridControl;
        var m = grid.Model!;
        var others = c.ExtentWidth - c.RowHeaderWidth - m.GetColumnWidth(m.VisibleColumns[^1]);
        var room = c.ViewportWidth - c.RowHeaderWidth - others - 1;
        var widths = string.Join(", ", m.VisibleColumns.Select(i => $"{m.Columns[i].Name}={m.GetColumnWidth(i)}"));
        if (room >= 120)
        {
            Assert.True(Math.Abs(c.ExtentWidth - c.ViewportWidth) <= 2, $"{grid.Name}: 그리드 폭 {c.ViewportWidth} 에 열 폭 합 {c.ExtentWidth} ({widths})");
            return true;
        }

        Assert.True(c.ExtentWidth >= c.ViewportWidth - 2, $"{grid.Name}: 그리드 폭 {c.ViewportWidth} 에 열 폭 합 {c.ExtentWidth} ({widths})");
        return false;
    }

    [Fact]
    public Task Rule_grid_fits_width_after_duplicating_a_job() => HeadlessHost.Run(async () =>
    {
        // 복제·새 작업처럼 열 정보 없이 다른 작업을 열어 규칙 표를 비우고 다시 채운다. 부모 키 조건 글이 남은 폭보다 조금 길어도
        // 가로 스크롤 없이 마지막 열이 남은 폭에 맞아야 한다.
        var h = new DataCopyHarness();
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            var copy = TestJobs.Job("월말 운영 데이터 복사 복사본", "SRC", "TGT",
                TestJobs.Rule("ORDER_ITEMS", RuleKind.ParentKey) with
                {
                    Parent = new ParentLink("CUSTOMERS", ["CUST_ID"], ["CUST_ID"], "FK_ORDER_ITEMS_CUSTOMERS"),
                },
                TestJobs.Rule("ORDERS"),
                TestJobs.Rule("CODES"));
            h.Store.Jobs[copy.Id] = copy;
            await vm.OpenJobAsync(copy);
            Settle(window);

            var grid = window.FindControl<MonitorGrid>("RuleGrid")!;
            Assert.Equal(3, grid.Model!.RowCount);
            AssertFillsWidth(grid);

            // 열 정보 읽기 전에도 상세 칸 「부모 테이블(FK)」 콤보에 저장된 부모 표가 보인다.
            vm.Editor.SelectedRule = vm.Editor.Rules.Single(r => r.Table == "ORDER_ITEMS");
            Settle(window);
            var parentCombo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "부모 키 연결");
            Assert.Equal(0, parentCombo.SelectedIndex);
            Assert.Equal("CUSTOMERS", Assert.IsType<ParentLink>(parentCombo.SelectedItem).ParentTable);

            // 헤드리스 글꼴은 글자가 넓다: 조건 칸이 값에 필요한 폭(상한 360)보다 남은 폭이 좁고 최소값보다는 넓은 창 폭에서 본다
            // (계정 열 둘이 늘어 묶음 B 부터 창 폭을 넓혔다).
            foreach (var width in new[] { 1900.0, 2000 })
            {
                window.Width = width;
                Settle(window);
                Assert.True(AssertFillsWidth(grid), $"창 폭 {width}: 마지막 열 자리가 최소값보다 좁아 검사가 느슨해졌습니다");
                Assert.True(grid.GridControl.MaxOffsetX <= 0, $"창 폭 {width}: 가로 스크롤이 생겼습니다");
            }

            Shots(window, "1-rules-duplicated");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Rule_edit_step_fits_a_narrow_window() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var (window, _) = await OpenWindowAsync(h);
        try
        {
            window.Width = 1000;
            Settle(window);
            AssertNotClipped(window, "규칙 편집(폭 1000)");
            Shots(window, "1-rules-narrow");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Custom_period_and_long_date_format_fit_a_narrow_window() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            vm.Editor.PeriodChoice = vm.Editor.PeriodKinds.Single(k => k.Kind == PeriodKind.Custom);
            vm.Editor.CustomFrom = new DateTime(2026, 10, 1);
            vm.Editor.CustomTo = new DateTime(2026, 11, 1);
            vm.Editor.Rules[0].FirstFormat = "YYYY-MM-DD HH24:MI:SS";
            vm.Editor.Rules[0].Masks[0].Kind = MaskKind.Fixed; // 값 칸이 생긴다
            window.Width = 1000;
            Settle(window);
            AssertNotClipped(window, "직접 지정 기간(폭 1000)");

            // 끝 날짜 옆 「미만」·「끝 날짜 포함」 콤보가 보이고 칸 안에 들어간다.
            var endBox = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "끝 날짜 셈");
            Assert.True(endBox.IsEffectivelyVisible);
            Assert.True(endBox.Bounds.Width >= 60, $"끝 날짜 셈 폭 {endBox.Bounds.Width}");
            AssertInsideParent(endBox);

            // 마스킹 고정값 칸이 줄 오른쪽 밖으로 밀려나 잘리지 않는다.
            var valueBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "고정값");
            Assert.True(valueBox.IsEffectivelyVisible);
            AssertInsideParent(valueBox);
            Shots(window, "1-rules-custom-narrow");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Precheck_step_renders_without_clipping() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var items = new[]
        {
            new CheckItem("ORDERS", "TargetTriggers", CheckLevel.Confirm, "대상 트리거 TRG_ORDERS 가 복사한 행에도 실행됩니다.") { Choices = [CheckChoice.DisableTriggers] },
            new CheckItem("ORDER_ITEMS", "ChildRowsMayBlock", CheckLevel.Confirm, "대상의 자식 테이블이 이 테이블을 참조합니다(ORA-02292 예상). 대상 처리를 MERGE 로 바꾸세요.")
                { Choices = [CheckChoice.SwitchToMerge] },
            new CheckItem("CODES", "TargetRequiredColumnMissing", CheckLevel.Error, "원본에 없는 NOT NULL 열 CODE_GRP 가 대상에 있습니다."),
            new CheckItem(null, "ReadConsistency", CheckLevel.Pass, "예상 읽기 시간이 원본 UNDO_RETENTION 안에 끝납니다."),
        };
        var preview = new[]
        {
            new PreviewStatement("DELETE FROM \"APP\".\"ORDERS\" WHERE (\"ORDER_DT\" >= :FROM_S AND \"ORDER_DT\" < :TO_S) AND (STATUS <> 'CANCELED')", PreviewSide.Target, PolicyDecisionKind.Allow, null, null),
            new PreviewStatement("DELETE FROM \"APP\".\"CODES\"", PreviewSide.Target, PolicyDecisionKind.Confirm, "WHERE 없는 DELETE", null),
            new PreviewStatement("SELECT * FROM \"APP\".\"ORDERS\" WHERE \"ORDER_DT\" >= :FROM_S AND \"ORDER_DT\" < :TO_S", PreviewSide.Source, PolicyDecisionKind.Allow, null, null),
        };
        h.Precheck = new FakePrecheck(TestJobs.Report(items, [TestJobs.Plan("ORDERS", canDisableTriggers: true), TestJobs.Plan("ORDER_ITEMS")], preview));
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            await vm.Precheck.RunAsync(Sessions(), vm.CurrentJob!, new ResolvedPeriod(null, null));
            vm.Step = DataCopyStep.Precheck;
            Settle(window);
            vm.Precheck.SelectedPreview = vm.Precheck.Preview[0]; // 아래 코드 칸에 추가 조건(AND)까지 문장 전체가 보이는 장면
            Settle(window);

            Assert.True(window.FindControl<Button>("RunButton")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("RunButton")!.IsEffectivelyEnabled); // 오류가 있어 꺼진다
            Assert.False(window.FindControl<Button>("ToPrecheckButton")!.IsEffectivelyVisible);
            Assert.Equal("ORDERS", vm.Precheck.SelectedItem?.Table); // 첫 「확인 필요」를 골라 상세 칸에 고를 거리가 보인다
            var checks = window.FindControl<MonitorGrid>("CheckGrid")!.Model!;
            Assert.Equal(4, checks.RowCount);
            // 「항목」 칸은 사람이 읽는 이름, 코드는 상세 칸에만.
            var names = Enumerable.Range(0, 4).Select(r => checks.GetCellText(r, 2)).ToList();
            Assert.Contains("대상 트리거", names);
            Assert.DoesNotContain("TargetTriggers", names);
            AssertFillsWidth(window.FindControl<MonitorGrid>("SqlGrid")!);
            AssertNotClipped(window, "사전 점검");
            Shots(window, "2-precheck");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Precheck_step_with_sequence_candidates_renders_without_clipping() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var items = new[]
        {
            new CheckItem("ORDERS", "SequenceGap", CheckLevel.Pass, "ORDERS.ORDER_ID: 시퀀스 후보 ORDERS_SEQ. 작업에 연결하면 복사 뒤 값을 맞춥니다.")
                { Column = "ORDER_ID", Sequence = "ORDERS_SEQ" },
            new CheckItem("ORDER_ITEMS", "SequenceGap", CheckLevel.Pass, "ORDER_ITEMS.ITEM_ID: 시퀀스 후보 ORDER_ITEMS_SEQ. 작업에 연결하면 복사 뒤 값을 맞춥니다.")
                { Column = "ITEM_ID", Sequence = "ORDER_ITEMS_SEQ" },
            new CheckItem("CODES", "SequenceGap", CheckLevel.Confirm, "CODES.CODE_ID: 복사 뒤 시퀀스 CODES_SEQ 를 약 1,250번 소비해 값을 앞당깁니다.") { Column = "CODE_ID" },
        };
        h.Precheck = new FakePrecheck(TestJobs.Report(items, [TestJobs.Plan("ORDERS"), TestJobs.Plan("ORDER_ITEMS"), TestJobs.Plan("CODES")]));
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            vm.CurrentJob = vm.CurrentJob! with { Sequences = [new SequenceRule("CODES_SEQ", "CODES", "CODE_ID")] };
            await vm.Precheck.RunAsync(Sessions(), vm.CurrentJob!, new ResolvedPeriod(null, null));
            vm.Step = DataCopyStep.Precheck;
            Settle(window);

            Assert.True(window.FindControl<StackPanel>("LinkedSequencesPanel")!.IsEffectivelyVisible);
            Assert.Equal(2, vm.Precheck.Items.Count(i => i.HasSequenceLink));
            AssertNotClipped(window, "사전 점검(시퀀스 후보)");
            Shots(window, "2-precheck-sequences");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Read_only_target_notice_renders_in_the_rule_edit_step() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        h.Profiles[1] = h.Target with { ReadOnly = true };
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            Assert.True(window.FindControl<TextBlock>("TargetReadOnlyNoticeText")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("ToPrecheckButton")!.IsEffectivelyEnabled); // 다음 단계로 못 간다
            AssertNotClipped(window, "규칙 편집(읽기 전용 대상)");
            Shots(window, "1-rules-readonly-target");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Run_step_renders_without_clipping() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var failed = new FailedRow("ORDERS", new Dictionary<string, string?> { ["ORDER_ID"] = "1003" }, "ORA-02291: integrity constraint violated - parent key not found");
        var release = new TaskCompletionSource();
        h.Runner.Behavior = async _ => { await release.Task; return new CopyRunResult(
            CopyOutcome.PartiallyFailed,
            [
                new TableRunResult("CODES", 120, 120, TimeSpan.FromSeconds(1), CopyOutcome.Succeeded, null),
                new TableRunResult("ORDERS", 15_000, 14_999, TimeSpan.FromSeconds(9), CopyOutcome.PartiallyFailed, "1 행 실패"),
                new TableRunResult("ORDER_ITEMS", 42_300, 42_300, TimeSpan.FromSeconds(20), CopyOutcome.Succeeded, null),
            ],
            [failed],
            ["FK FK_ITEMS_ORDERS 를 NOVALIDATE 로 켰습니다(부모 없는 행 3개)."],
            new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)); };
        var at = new DateTimeOffset(2026, 11, 9, 14, 3, 0, TimeSpan.FromHours(9));
        void Log(int sec, string phase, string? table, string text, CopyLogKind kind = CopyLogKind.Info) =>
            h.Runner.Reports.Add(CopyProgress.ForLog(new CopyLogEntry(at.AddSeconds(sec), phase, table, text, kind)));
        Log(0, "시작", null, "복사 시작: 작업 「월말 운영 데이터 복사」 · 원본 SRC → 대상 TGT · 테이블 3개 · 커밋 테이블마다 · 배치 5,000행 · 실패 행 상한 100 · 같은 시점 기준 읽기");
        Log(0, "시작", null, "기간: 2026-10-01 00:00:00 이상 ~ 2026-11-01 00:00:00 미만");
        Log(0, "원본 준비", null, "원본: ROLLBACK  — 앞 트랜잭션 닫기", CopyLogKind.Statement);
        Log(0, "원본 준비", null, "원본: SET TRANSACTION READ ONLY  — 모든 테이블을 이 시점 기준으로 읽기", CopyLogKind.Statement);
        Log(1, "끄기", "ORDERS", "대상: ALTER TRIGGER \"TGT\".\"TRG_ORDERS_AUDIT\" DISABLE", CopyLogKind.Statement);
        Log(1, "끄기", "ORDERS", "→ 완료 · 0.03초");
        Log(1, "정리", "ORDERS", "대상: DELETE FROM \"TGT\".\"ORDERS\" WHERE \"ORDER_DT\" >= :B1_FROM AND \"ORDER_DT\" < :B1_TO AND (CSM_DOC_NO IN (:CSM_DOC_NOS_1, …))  [바인드 :B1_FROM = 2026-10-01 00:00:00, :B1_TO = 2026-11-01 00:00:00, :CSM_DOC_NOS = 23개]", CopyLogKind.Statement);
        Log(2, "정리", "ORDERS", "→ 완료 · 14,120행 · 0.84초");
        Log(2, "적재", "ORDERS", "원본: SELECT \"ORDER_ID\", \"ORDER_DT\", \"CUST_NAME\" FROM \"SRC\".\"ORDERS\" WHERE …  [바인드 :B1_FROM = 2026-10-01 00:00:00, :B1_TO = 2026-11-01 00:00:00, :CSM_DOC_NOS = 23개]", CopyLogKind.Statement);
        Log(2, "적재", "ORDERS", "대상: INSERT INTO \"TGT\".\"ORDERS\" (\"ORDER_ID\", \"ORDER_DT\", \"CUST_NAME\") VALUES (:C1, :C2, :C3)  [바인드 배열 :C1…:C3, 배치 5,000행씩]", CopyLogKind.Statement);
        Log(2, "적재", "ORDERS", "마스킹(앱에서 바꿔 보냄, 값은 남기지 않음): CUST_NAME(해시)");
        Log(4, "적재", "ORDERS", "배치 1 · 5,000행 보냄 · 누적 5,000행");
        Log(6, "적재", "ORDERS", "실패 행 1: ORA-02291: integrity constraint violated - parent key not found", CopyLogKind.Error);
        Log(9, "적재", "ORDERS", "적재 끝 · 원본 15,000건 읽음 · 14,999행 넣음 · 실패 1행 · 9.02초");
        Log(30, "건수 비교", "ORDERS", "→ 원본 15,000건 · 대상 14,999건 · 다름", CopyLogKind.Warning);
        Log(31, "마무리", null, "끝: 일부 실패 · 테이블 3개(성공 2) · 57,419행 넣음 · 실패 행 1 · 걸린 시간 31.40초", CopyLogKind.Warning);
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            var report = TestJobs.Report(order: [TestJobs.Plan("CODES"), TestJobs.Plan("ORDERS"), TestJobs.Plan("ORDER_ITEMS")]);
            var run = vm.Run.StartAsync(Sessions(), vm.CurrentJob!, report,
                new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false), new ResolvedPeriod(null, null));
            vm.Step = DataCopyStep.Run;
            await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Settle(window); // 그리드가 「대기」·빈 건수로 먼저 그려진다
            release.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Settle(window);

            Assert.True(window.FindControl<Button>("StopButton")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("CsvButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("RunButton")!.IsEffectivelyVisible);
            var runGrid = window.FindControl<MonitorGrid>("RunGrid")!;
            Assert.Equal(3, runGrid.Model!.RowCount);
            Assert.Equal("✓ 같음", runGrid.Model.GetCellText(0, 5)); // 끝난 뒤 값(단계·건수)이 그리드에 반영된다
            AssertColumnsFit(runGrid); // 「대기」로 처음 정한 너비가 「문제 있음」·「일부 실패 · …」에 맞게 넓어진다
            AssertFillsWidth(runGrid);
            AssertNotClipped(window, "실행·검증");
            Assert.Equal(1, vm.Run.DetailTab); // 실패 행이 있어 실패한 행 탭으로 옮겼다
            Shots(window, "3-run");

            // 작업 로그 탭: 실행기가 보낸 줄이 모두 보이고(끝으로 스크롤), 복사·저장 단추가 켜진다.
            vm.Run.DetailTab = 0;
            Settle(window);
            Assert.Equal(16, vm.Run.Log.Lines.Count);
            Assert.True(window.FindControl<CopyRunLogView>("RunLogView")!.IsEffectivelyVisible);
            var logView = window.FindControl<CopyRunLogView>("RunLogView")!;
            Assert.True(logView.FindControl<Button>("CopyLogButton")!.IsEffectivelyEnabled);
            Assert.True(logView.FindControl<Button>("SaveLogButton")!.IsEffectivelyEnabled);
            AssertNotClipped(window, "작업 로그");
            Shots(window, "3-run-log");
        }
        finally
        {
            window.Close();
        }
    });
    // 묶음 B: 기본 계정 APP→TGT_USER(접속 계정), 시노님으로 더한 APP2.ORDERS(부모 APP.CUSTOMERS), 다른 대상 계정 SRC2.CODES → DST.
    private static async Task<(DataCopyWindow Window, DataCopyViewModel Vm)> OpenOwnersWindowAsync(DataCopyHarness h)
    {
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        LiteralTranslator.Install(text);
        var job = TestJobs.Job("여러 계정 복사", "SRC", "TGT",
            TestJobs.Rule("CUSTOMERS") with { Action = TargetAction.MergeByPk, TargetOwner = "TGT_USER" },
            TestJobs.Rule("ORDERS", RuleKind.ParentKey) with
            {
                SourceOwner = "APP2",
                TargetOwner = "APP2",
                WrittenAs = "ORDERS",
                Parent = new ParentLink("CUSTOMERS", ["CUST_ID"], ["CUST_ID"], "FK_ORDERS_CUST") { ParentOwner = "APP" },
            },
            TestJobs.Rule("CODES") with { SourceOwner = "SRC2", TargetOwner = "DST", Action = TargetAction.MergeByPk },
            TestJobs.Rule("NOTICES") with { SourceOwner = "TGT_USER", TargetOwner = "TGT_USER" }) with
        {
            TargetOwner = "TGT_USER",
        };
        var vm = await h.OpenAsync(job);
        vm.Editor.SelectedRule = vm.Editor.Rules[1];
        var window = new DataCopyWindow { DataContext = vm };
        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        return (window, vm);
    }

    [Fact]
    public Task Rule_grid_shows_schema_columns_and_the_synonym_detail() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var (window, vm) = await OpenOwnersWindowAsync(h);
        try
        {
            // 규칙을 바꿔 골라도 대상 계정 칸이 앞 규칙에 새 규칙 값을 쓰지 않는다.
            foreach (var pick in (int[])[0, 2, 1])
            {
                vm.Editor.SelectedRule = vm.Editor.Rules[pick];
                Settle(window);
                Assert.Equal(vm.Editor.Rules[pick].TargetOwner, window.FindControl<ComboBox>("TargetOwnerBox")!.Text);
            }

            Assert.Equal(["TGT_USER", "APP2", "DST", "TGT_USER"], vm.Editor.Rules.Select(r => r.TargetOwner));
            var grid = window.FindControl<MonitorGrid>("RuleGrid")!.Model!;
            Assert.Equal(["원본 계정", "테이블", "대상 계정", "규칙", "대상 처리", "조건"], grid.Columns.Select(c => c.Name));
            Assert.Equal("APP2", grid.GetCellText(1, 0));
            Assert.Equal("APP2 (!)", grid.GetCellText(1, 2)); // 접속 계정(TGT_USER)이 아닌 계정: 경고색
            Assert.True(grid.IsWarned(1, 2));
            Assert.False(grid.IsWarned(0, 2));
            Assert.False(grid.IsMuted(1, 2)); // 원본과 같아도 경고가 먼저
            Assert.Equal("APP.CUSTOMERS 의 키 · CUST_ID", grid.GetCellText(1, 5));
            Assert.Equal("DST (!)", grid.GetCellText(2, 2));

            Assert.Equal("APP2.ORDERS", window.FindControl<TextBlock>("RuleHeaderText")!.Text);
            Assert.Equal("APP2", window.FindControl<TextBlock>("RuleSourceOwnerText")!.Text);
            var targetOwner = window.FindControl<ComboBox>("TargetOwnerBox")!;
            Assert.Equal("APP2", targetOwner.Text);
            Assert.Equal(["APP2", "TGT_USER"], targetOwner.Items.OfType<TargetOwnerOption>().Select(o => o.Owner));
            Assert.True(window.FindControl<TextBlock>("OtherTargetOwnerText")!.IsEffectivelyVisible);
            AssertNotClipped(window, "규칙 편집(여러 계정)");
            Shots(window, "1-rules-owners");

            // 대상 계정을 접속 계정으로 고르면 경고가 사라지고 원본과 다르니 흐린 글자도 아니다.
            targetOwner.SelectedIndex = 1;
            Settle(window);
            Assert.Equal("TGT_USER", vm.Editor.Rules[1].TargetOwner);
            Assert.False(window.FindControl<TextBlock>("OtherTargetOwnerText")!.IsEffectivelyVisible);
            var after = window.FindControl<MonitorGrid>("RuleGrid")!.Model!;
            Assert.Equal("TGT_USER", after.GetCellText(1, 2));
            Assert.False(after.IsWarned(1, 2));

            // 원본과 같은 대상 계정: 접속 계정이면 흐린 글자, 아니면 경고색이 먼저.
            Assert.True(after.IsMuted(3, 2)); // TGT_USER.NOTICES → TGT_USER
            vm.Editor.Rules[0].TargetOwner = "APP";
            Settle(window);
            var same = window.FindControl<MonitorGrid>("RuleGrid")!.Model!;
            Assert.True(same.IsWarned(0, 2)); // APP ≠ TGT_USER
            Assert.False(same.IsMuted(0, 2));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Target_schema_choices_follow_the_default_source_and_an_empty_cell_shows_the_default_target() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var (window, vm) = await OpenOwnersWindowAsync(h);
        try
        {
            vm.Editor.SelectedRule = vm.Editor.Rules[0]; // APP.CUSTOMERS → TGT_USER(기본을 따름)
            Settle(window);
            var targetOwner = window.FindControl<ComboBox>("TargetOwnerBox")!;
            Assert.Equal("APP", targetOwner.Items.OfType<TargetOwnerOption>().First().Owner);

            // 기본 원본 계정을 바꾸면 「원본과 같음」 이 새 계정을 가리킨다(목록을 다시 만든다).
            vm.Editor.SourceOwner = " app5 ";
            Settle(window);
            Assert.Equal("APP5", vm.Editor.Rules[0].SourceOwner);
            Assert.Equal("APP5", targetOwner.Items.OfType<TargetOwnerOption>().First().Owner);
            Assert.Equal("TGT_USER", targetOwner.Text);

            // 대상 칸을 비우면 실제로 쓰는 기본 대상을 흐린 글자로 보인다.
            targetOwner.Text = string.Empty;
            Settle(window);
            Assert.Equal(string.Empty, vm.Editor.Rules[0].TargetOwner);
            var grid = window.FindControl<MonitorGrid>("RuleGrid")!.Model!;
            Assert.Equal("TGT_USER", grid.GetCellText(0, 2));
            Assert.True(grid.IsMuted(0, 2));
            Assert.False(grid.IsWarned(0, 2));
            Shots(window, "1-rules-default-target");

            // 기본 대상이 접속 계정이 아니게 되면 비운 칸의 기본 대상도 경고색.
            vm.Editor.TargetOwner = "qa";
            Settle(window);
            var warned = window.FindControl<MonitorGrid>("RuleGrid")!.Model!;
            Assert.Equal("QA (!)", warned.GetCellText(0, 2));
            Assert.True(warned.IsWarned(0, 2));
            Assert.Equal("QA", vm.Editor.BuildJob().Tables[0].TargetOwner);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Run_confirmation_dialog_lists_schemas_and_defaults_to_cancel() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var (window, vm) = await OpenOwnersWindowAsync(h);
        try
        {
            var confirmation = RunConfirmation.For(vm.Editor.BuildJob(), "TGT_USER")!;
            var dialog = DataCopyDialogs.BuildRunConfirmDialog(confirmation, out var accepted);
            dialog.Show(window);
            LiteralTranslator.Translate(dialog);
            Settle(dialog);
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
            var cancel = Assert.Single(buttons, b => b.IsDefault);
            Assert.True(cancel.IsCancel); // 기본 단추(Enter) = 취소
            Assert.Contains(buttons, b => !b.IsDefault && Equals(b.Content, "복사 실행"));
            var lines = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains(lines, l => l.StartsWith("APP2 · 테이블 1개", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("DST · 테이블 1개", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("TGT_USER (접속 계정)", StringComparison.Ordinal));
            AssertNotClipped(dialog, "실행 확인");
            Shots(dialog, "3-run-confirm");
            Assert.False(accepted());
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Precheck_results_show_schema_dot_table_and_the_new_checks() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var items = new[]
        {
            new CheckItem(null, CopyCheckCodes.OtherOwnerTargets, CheckLevel.Confirm, "대상 접속 계정(TGT_USER)이 아닌 계정 APP2, DST 의 테이블 2개를 지우고 넣습니다."),
            new CheckItem("APP2.ORDERS", CopyCheckCodes.SynonymChanged, CheckLevel.Confirm, "더할 때 적은 시노님 ORDERS 가 지금은 APP3.ORDERS 를 가리킵니다. 작업은 저장된 APP2.ORDERS 를 복사합니다."),
            new CheckItem("SRC2.CODES", "TargetNoWrite", CheckLevel.Error, "DST.CODES 에 INSERT·DELETE 권한이 없습니다."),
            new CheckItem("CUSTOMERS", "ReadConsistency", CheckLevel.Pass, "예상 읽기 시간이 원본 UNDO_RETENTION 안에 끝납니다."),
        };
        var preview = new[]
        {
            new PreviewStatement("DELETE FROM \"APP2\".\"ORDERS\" t WHERE EXISTS (SELECT 1 FROM \"APP\".\"CUSTOMERS\" p WHERE p.\"CUST_ID\" = t.\"CUST_ID\")", PreviewSide.Target, PolicyDecisionKind.Allow, null, null),
            new PreviewStatement("MERGE INTO \"DST\".\"CODES\" t USING …", PreviewSide.Target, PolicyDecisionKind.Allow, null, null),
        };
        h.Precheck = new FakePrecheck(TestJobs.Report(items, [], preview));
        var (window, vm) = await OpenOwnersWindowAsync(h);
        try
        {
            await vm.Precheck.RunAsync(Sessions(), vm.Editor.BuildJob(), new ResolvedPeriod(null, null));
            vm.Step = DataCopyStep.Precheck;
            Settle(window);
            vm.Precheck.SelectedItem = vm.Precheck.Items.Single(i => i.Code == CopyCheckCodes.SynonymChanged);
            Settle(window);

            var checks = window.FindControl<MonitorGrid>("CheckGrid")!.Model!;
            var tables = Enumerable.Range(0, 4).Select(r => checks.GetCellText(r, 1)).ToList();
            Assert.Contains("APP2.ORDERS", tables);
            Assert.Contains("SRC2.CODES", tables);
            Assert.Contains("APP.CUSTOMERS", tables); // 기본 계정의 표도 계정.표
            var names = Enumerable.Range(0, 4).Select(r => checks.GetCellText(r, 2)).ToList();
            Assert.Contains("다른 계정 테이블에 씀", names);
            Assert.Contains("시노님이 가리키는 테이블 바뀜", names);
            AssertNotClipped(window, "사전 점검(여러 계정)");
            Shots(window, "2-precheck-owners");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Read_only_job_banner_renders_and_turns_off_the_precheck() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        LiteralTranslator.Install(text);
        var job = TestJobs.Job("새 버전 작업", rules: TestJobs.Rule("ORDERS"));
        h.Store.ReadOnlyIds.Add(job.Id);
        var vm = await h.OpenAsync(job);
        var window = new DataCopyWindow { DataContext = vm };
        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        try
        {
            Assert.True(window.FindControl<TextBlock>("ReadOnlyJobNoticeText")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("ToPrecheckButton")!.IsEffectivelyEnabled);
            AssertNotClipped(window, "새 버전 작업");
            Shots(window, "1-rules-newer-job");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Closing_a_running_window_is_cancelled_until_the_user_confirms_stop() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var release = new TaskCompletionSource<CopyRunResult>();
        h.Runner.Behavior = _ => release.Task;
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            var report = TestJobs.Report(order: [TestJobs.Plan("ORDERS")]);
            var run = vm.Run.StartAsync(Sessions(), vm.CurrentJob!, report,
                new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false), new ResolvedPeriod(null, null));
            await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.Run.IsRunning);

            window.Close(); // 확인 창은 기본(NoDialogs)에서 「아니오」 → 닫히지 않는다
            Settle(window);

            Assert.True(window.IsVisible);
            Assert.True(vm.Run.IsRunning);
            Assert.False(h.Runner.Token.IsCancellationRequested);

            release.SetResult(TestJobs.Result());
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            window.Close();
        }
    });
}
