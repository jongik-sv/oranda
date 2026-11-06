using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.DataCopy;
using Oranda.App.Features.Localization;
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

    private static async Task<(DataCopyWindow Window, DataCopyViewModel Vm)> OpenWindowAsync(DataCopyHarness h)
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
            },
            TestJobs.Rule("ORDER_ITEMS", RuleKind.ParentKey),
            TestJobs.Rule("CODES"),
            TestJobs.Rule("AUDIT_LOG", RuleKind.Custom) with { CustomWhere = "LOG_DT >= :FROM_DT" },
        };
        var job = TestJobs.Job("월말 운영 데이터 복사", "SRC", "TGT", rules) with { Period = new CopyPeriod(PeriodKind.LastMonth) };
        var vm = await h.OpenAsync(job);
        vm.Editor.Rules[0].SetSource(orders);
        vm.Editor.SelectedRule = vm.Editor.Rules[0];

        var window = new DataCopyWindow { DataContext = vm };
        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        return (window, vm);
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
            AssertNotClipped(window, "규칙 편집");
            Shots(window, "1-rules");
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
            new CheckItem("ORDERS", "TRIGGERS", CheckLevel.Confirm, "대상 트리거 TRG_ORDERS 가 복사한 행에도 실행됩니다.") { Choices = [CheckChoice.DisableTriggers] },
            new CheckItem("ORDER_ITEMS", "FK_CHILD", CheckLevel.Confirm, "대상의 자식 테이블이 이 테이블을 참조합니다(ORA-02292 예상). 대상 처리를 MERGE 로 바꾸세요.")
                { Choices = [CheckChoice.SwitchToMerge] },
            new CheckItem("CODES", "COLUMN_MISSING", CheckLevel.Error, "원본에 없는 NOT NULL 열 CODE_GRP 가 대상에 있습니다."),
            new CheckItem(null, "READ", CheckLevel.Pass, "원본 SELECT 권한을 확인했습니다."),
        };
        var preview = new[]
        {
            new PreviewStatement("DELETE FROM \"APP\".\"ORDERS\" WHERE \"ORDER_DT\" >= :FROM_S AND \"ORDER_DT\" < :TO_S", PreviewSide.Target, PolicyDecisionKind.Allow, null, null),
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

            Assert.True(window.FindControl<Button>("RunButton")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("RunButton")!.IsEffectivelyEnabled); // 오류가 있어 꺼진다
            Assert.False(window.FindControl<Button>("ToPrecheckButton")!.IsEffectivelyVisible);
            AssertNotClipped(window, "사전 점검");
            Shots(window, "2-precheck");
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
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(
            CopyOutcome.PartiallyFailed,
            [
                new TableRunResult("CODES", 120, 120, TimeSpan.FromSeconds(1), CopyOutcome.Succeeded, null),
                new TableRunResult("ORDERS", 15_000, 14_999, TimeSpan.FromSeconds(9), CopyOutcome.PartiallyFailed, "1 행 실패"),
                new TableRunResult("ORDER_ITEMS", 42_300, 42_300, TimeSpan.FromSeconds(20), CopyOutcome.Succeeded, null),
            ],
            [failed],
            ["FK FK_ITEMS_ORDERS 를 NOVALIDATE 로 켰습니다(부모 없는 행 3개)."],
            new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)));
        var (window, vm) = await OpenWindowAsync(h);
        try
        {
            var report = TestJobs.Report(order: [TestJobs.Plan("CODES"), TestJobs.Plan("ORDERS"), TestJobs.Plan("ORDER_ITEMS")]);
            await vm.Run.StartAsync(Sessions(), vm.CurrentJob!, report,
                new CopyDecisions(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), false), new ResolvedPeriod(null, null));
            vm.Step = DataCopyStep.Run;
            Settle(window);

            Assert.True(window.FindControl<Button>("StopButton")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("CsvButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("RunButton")!.IsEffectivelyVisible);
            AssertNotClipped(window, "실행·검증");
            Shots(window, "3-run");
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
                new CopyDecisions(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), false), new ResolvedPeriod(null, null));
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
