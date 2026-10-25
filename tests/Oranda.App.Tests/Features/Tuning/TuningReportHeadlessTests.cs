using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Tuning.Assistant;
using Oranda.App.Features.Tuning.Report;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>
/// TSK-06-07 「튜닝 보고서 만들기」 대화 상자(Headless): 상태마다(준비·운영·팩 있음·접속 없음·만드는 중·오류) 창이 뜨고 단추가 맞게 켜진다.
/// <c>ORANDA_RENDER_DIR</c> 이 있으면 라이트·다크 PNG 와 보고서 예시(sample-report.html)를 남긴다.
/// </summary>
public sealed class TuningReportHeadlessTests
{
    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static TuningReportDialog Show(TuningReportDialogViewModel vm)
    {
        var dialog = new TuningReportDialog { DataContext = vm };
        dialog.Show();
        Pump();
        dialog.UpdateLayout();
        return dialog;
    }

    private static void Shots(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        try
        {
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                Directory.CreateDirectory(dir);
                var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"tn-report-{name}-{suffix}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new Xunit.Sdk.XunitException("없는 컨트롤: " + name);

    private static bool Shows(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    private static bool ShowsPart(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.Contains(text, StringComparison.Ordinal) == true);

    // 저장 경로는 이 OS 에서 완전한 경로여야 「만들기」가 켜진다(macOS·Linux 에서 C:\ 는 상대 경로).
    private static readonly string SamplePath = OperatingSystem.IsWindows()
        ? @"C:\Users\tester\Documents\Oranda\tuning_7h35uxf5uhmm1_20261024_1412.html"
        : "/Users/tester/Documents/Oranda/tuning_7h35uxf5uhmm1_20261024_1412.html";

    private static TuningReportDialogViewModel Vm(ConnectionEnvironment env = ConnectionEnvironment.Production, LicensePack pack = LicensePack.None) =>
        new(ReportFixtures.Session(env, pack), new FakeReportTuning(), new FakeReportMonitor(), ReportFixtures.SqlId)
        {
            SourceText = "SQL 모니터에서 고른 SQL",
            FilePath = SamplePath,
        };

    [Fact]
    public Task Production_without_pack_shows_the_form_with_awr_off_and_the_notice() => Ui.Run(async () =>
    {
        var vm = Vm();
        var dialog = Show(vm);
        try
        {
            await vm.LoadChildrenAsync();
            Pump();
            Assert.True(Named<Button>(dialog, "CreateButton").IsEnabled);
            Assert.True(Named<Button>(dialog, "CancelButton").IsDefault);
            Assert.False(Named<CheckBox>(dialog, "AwrBox").IsEnabled);
            Assert.False(Named<CheckBox>(dialog, "AwrBox").IsChecked);
            Assert.False(Named<CheckBox>(dialog, "BindValuesBox").IsChecked);
            Assert.True(Shows(dialog, TuningReportDialogViewModel.ProductionNotice));
            Assert.True(Shows(dialog, "운영"));
            Assert.True(ShowsPart(dialog, "Diagnostics Pack 선언 없음"));
            Assert.Equal("전체 (4개 · plan_hash 2)", Named<ComboBox>(dialog, "ChildBox").SelectedItem?.ToString());
            Assert.False(Named<StackPanel>(dialog, "ProgressPanel").IsVisible);
            Shots(dialog, "ready-prod");
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Test_connection_with_diagnostics_enables_awr() => Ui.Run(() =>
    {
        var vm = Vm(ConnectionEnvironment.Test, LicensePack.Diagnostics);
        var dialog = Show(vm);
        try
        {
            Assert.True(Named<CheckBox>(dialog, "AwrBox").IsEnabled);
            Assert.True(Named<CheckBox>(dialog, "AwrBox").IsChecked);
            Assert.False(Shows(dialog, TuningReportDialogViewModel.ProductionNotice));
            Assert.True(ShowsPart(dialog, "ASH"));
            Shots(dialog, "ready-test-pack");
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task No_connection_and_bad_sql_id_block_create() => Ui.Run(() =>
    {
        var vm = new TuningReportDialogViewModel();
        var dialog = Show(vm);
        try
        {
            Assert.False(Named<Button>(dialog, "CreateButton").IsEnabled);
            Assert.True(ShowsPart(dialog, "접속이 없습니다"));
            Shots(dialog, "no-session");
        }
        finally
        {
            dialog.Close();
        }

        var bad = Vm(ConnectionEnvironment.Development);
        bad.SqlId = "select 1";
        var d2 = Show(bad);
        try
        {
            Assert.False(Named<Button>(d2, "CreateButton").IsEnabled);
            Assert.True(ShowsPart(d2, "sql_id 가 아닙니다"));
            Shots(d2, "bad-sqlid");
        }
        finally
        {
            d2.Close();
        }
    });

    [Fact]
    public Task Running_state_shows_progress_and_only_cancel() => Ui.Run(() =>
    {
        var vm = Vm();
        var dialog = Show(vm);
        try
        {
            vm.IsRunning = true;
            vm.ProgressValue = 68;
            vm.ProgressText = "대기 분해 표본 모으는 중… 12 / 30 초";
            vm.ProgressCount = "절 8 / 11";
            vm.FinishedText = "끝난 절: SQL 전문 · 자식 커서·불일치 사유 · 실행 계획 · 계획 그래프 · 실행 통계 · 표·인덱스·열 통계";
            Pump();
            Assert.True(Named<StackPanel>(dialog, "ProgressPanel").IsVisible);
            Assert.False(Named<Button>(dialog, "CreateButton").IsVisible);
            Assert.True(Named<Button>(dialog, "CancelButton").IsVisible);
            Assert.True(Shows(dialog, "절 8 / 11"));
            Shots(dialog, "running");
            vm.IsRunning = false;
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Error_state_shows_the_reason_and_keeps_the_form() => Ui.Run(async () =>
    {
        var vm = Vm(ConnectionEnvironment.Development);
        vm.WaitSampleSeconds = 1;
        vm.CreateCollector = (t, m) => new TuningReportCollector(t, m) { Delay = (_, _) => Task.CompletedTask };
        vm.WriteFile = (_, _, _) => throw new UnauthorizedAccessException("C:\\Windows 에 쓸 권한이 없습니다");
        var dialog = Show(vm);
        try
        {
            Assert.Null(await vm.CreateAsync());
            Pump();
            Assert.True(ShowsPart(dialog, "파일을 쓰지 못했습니다"));
            Assert.True(Named<Button>(dialog, "CreateButton").IsEnabled);
            Shots(dialog, "error");
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task English_dialog_has_no_korean_left() => Ui.Run(() =>
    {
        Oranda.App.Features.Localization.LiteralTranslator.Install(new Oranda.App.Shell.ShellText(language: "en"));
        try
        {
            var vm = Vm();
            var dialog = Show(vm);
            Oranda.App.Features.Localization.LiteralTranslator.Translate(dialog);
            Pump();
            try
            {
                var korean = dialog.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible && t.Text is { } s && System.Text.RegularExpressions.Regex.IsMatch(s, "[가-힣]") && !s.Contains("SQL 모니터", StringComparison.Ordinal))
                    .Select(t => t.Text).ToList();
                Assert.Empty(korean);
                Shots(dialog, "english");
            }
            finally
            {
                dialog.Close();
            }
        }
        finally
        {
            Oranda.App.Features.Localization.LiteralTranslator.Install(new Oranda.App.Shell.ShellText(language: "ko"));
        }
    });

    [Fact]
    public Task Assistant_toolbar_has_the_report_button() => Ui.Run(() =>
    {
        var view = new TuningAssistantView { DataContext = new TuningAssistantViewModel() };
        var window = new Window { Width = 1280, Height = 400, Content = view };
        window.Show();
        Pump();
        try
        {
            var button = Named<Button>(view, "ReportButton");
            Assert.Contains("HTML 파일 하나로", ToolTip.GetTip(button) as string, StringComparison.Ordinal);
            Assert.False(button.IsEnabled); // 접속 없음
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public async Task Sample_report_is_saved_next_to_the_shots()
    {
        var data = await ReportFixtures.CollectAsync(session: ReportFixtures.Session(pack: LicensePack.None));
        var html = TuningReportHtml.Build(data);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "sample-report.html"), html);
            var pack = await ReportFixtures.CollectAsync(session: ReportFixtures.Session(ConnectionEnvironment.Test, LicensePack.Diagnostics), options: ReportFixtures.Options(binds: true));
            await File.WriteAllTextAsync(Path.Combine(dir, "sample-report-pack-binds.html"), TuningReportHtml.Build(pack));
        }
    }
}
