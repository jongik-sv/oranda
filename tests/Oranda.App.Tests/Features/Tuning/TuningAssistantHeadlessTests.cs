using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Tuning.Assistant;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Language.Tuning;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>
/// TSK-06-05 튜닝 도우미 화면(Headless): 상태마다 화면이 뜨고 단추·표가 맞게 켜진다. <c>ORANDA_RENDER_DIR</c> 이 있으면 라이트·다크 PNG 를 남긴다.
/// </summary>
public sealed class TuningAssistantHeadlessTests
{
    private const int Width = 1280;
    private const int Height = 820;

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (TuningAssistantView View, Window Window) Show(TuningAssistantViewModel vm)
    {
        var view = new TuningAssistantView { DataContext = vm };
        var window = new Window { Width = Width, Height = Height, Content = view };
        window.Show();
        Pump();
        window.UpdateLayout();
        return (view, window);
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
                using var bmp = new RenderTargetBitmap(new PixelSize(Width, Height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"tn-assist-{name}-{suffix}.png"));
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

    /// <summary>실제 포인터로 누르고 뗀다(<paramref name="at"/> 은 <paramref name="target"/> 안 좌표).</summary>
    private static void Click(Window window, Visual target, Point at)
    {
        var p = target.TranslatePoint(at, window)!.Value;
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Pump();
    }

    private static Panel BenchCell(ListBoxItem row) => row.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("bench-cell"));

    [Fact]
    public Task Rewrite_rows_select_on_click_and_the_bench_cell_toggles_while_warning_only_rows_say_why() => Ui.Run(async () =>
    {
        var (vm, _, _) = await TuningAssistantTests.LoadedAsync();
        vm.CandidateTab = 1;
        var (view, window) = Show(vm);
        try
        {
            var list = Named<ListBox>(view, "RewriteList");
            Assert.Equal(3, list.ItemCount); // NOT IN(바꾼 SQL, 꺼 둠)·TRUNC(바꾼 SQL, 켜 둠)·형 변환(경고만)
            for (var i = 0; i < list.ItemCount; i++)
            {
                var row = (ListBoxItem)list.ContainerFromIndex(i)!;
                var item = (RewriteCandidateItem)row.DataContext!;

                // 줄 선택은 체크 가능 여부와 상관없이 된다 → 오른쪽 상세가 그 규칙으로 바뀐다.
                Click(window, row, new Point(200, row.Bounds.Height / 2));
                Assert.Same(item, vm.SelectedRewrite);
                Assert.True(Shows(view, item.Title));

                var cell = BenchCell(row);
                var box = cell.GetVisualDescendants().OfType<CheckBox>().Single();
                var before = item.IsChecked;
                if (item.CanCheck)
                {
                    Assert.True(box.IsEffectivelyVisible);
                    Click(window, box, new Point(box.Bounds.Width / 2, box.Bounds.Height / 2)); // 상자 가운데
                    Assert.Equal(!before, item.IsChecked);
                    Click(window, cell, new Point(3, cell.Bounds.Height / 2)); // 상자 밖 칸 왼쪽 끝
                    Assert.Equal(before, item.IsChecked);
                    Click(window, cell, new Point(cell.Bounds.Width - 3, 3)); // 칸 오른쪽 위 구석
                    Assert.Equal(!before, item.IsChecked);
                    Assert.Same(item, vm.SelectedRewrite);
                }
                else
                {
                    // 흐린 상자 대신 「경고만」 과 까닭이 줄에 보이고, 눌러도 체크되지 않는다.
                    Assert.False(box.IsEffectivelyVisible);
                    Assert.Contains(cell.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "경고만");
                    Assert.True(Shows(row, item.NoRewriteReason));
                    Assert.Contains(item.NoRewriteReason, (string)ToolTip.GetTip(cell)!, StringComparison.Ordinal);
                    Click(window, cell, new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2));
                    Assert.False(item.IsChecked);
                    Assert.Same(item, vm.SelectedRewrite);
                }
            }

            Assert.True(vm.HasWarningOnlyRewrite);
            Assert.True(Shows(view, TuningAssistantViewModel.RewriteCheckNote));
            Shots(window, "rewrite-bench-cell");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Hint_rows_toggle_from_the_bench_cell_and_disabled_hints_show_a_dash_with_the_reason() => Ui.Run(async () =>
    {
        var (vm, _, _) = await TuningAssistantTests.LoadedAsync();
        vm.CandidateTab = 2;
        var (view, window) = Show(vm);
        try
        {
            var list = Named<ListBox>(view, "HintList");
            Assert.True(vm.HasDisabledHint); // 병렬 힌트는 꺼져 있다
            Assert.True(Shows(view, TuningAssistantViewModel.HintCheckNote));
            for (var i = 0; i < list.ItemCount; i++)
            {
                var row = (ListBoxItem)list.ContainerFromIndex(i)!;
                var item = (HintCandidateItem)row.DataContext!;
                Click(window, row, new Point(300, row.Bounds.Height / 2));
                Assert.Same(item, vm.SelectedHint);
                var cell = BenchCell(row);
                var before = item.IsChecked;
                Click(window, cell, new Point(3, cell.Bounds.Height / 2));
                if (item.CanCheck)
                {
                    Assert.Equal(!before, item.IsChecked);
                }
                else
                {
                    Assert.False(item.IsChecked);
                    Assert.Contains(cell.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "—");
                    Assert.Equal(item.Advice.DisabledReason, ToolTip.GetTip(cell));
                }
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Ready_state_with_benchmark_results_renders_candidates_and_the_table() => Ui.Run(async () =>
    {
        var tuning = new TuningAssistantTests.FakeTuning
        {
            Result = c => c.Sql.Contains("INDEX(", StringComparison.Ordinal) ? (64, 1440, 12, 1208, 3392001144)
                : c.Sql.Contains(">= TRUNC(", StringComparison.Ordinal) ? (58, 1402, 10, 1203, 3392001144)
                : c.Sql.Contains("NOT EXISTS", StringComparison.Ordinal) ? (912, 41960, 380, 1208, 2201733460)
                : (1842, 42180, 31022, 1208, 1838229974),
        };
        var (vm, _, _) = await TuningAssistantTests.LoadedAsync(tuning: tuning);
        vm.RewriteItems.First(r => r.Advice.Rule == RewriteRule.NotInToNotExists).IsChecked = true;
        await vm.RunBenchmarkAsync();
        var (view, window) = Show(vm);
        try
        {
            Assert.Equal(4, Named<ListBox>(view, "BenchmarkList").ItemCount);
            Assert.Equal(vm.IndexItems.Count, Named<ListBox>(view, "IndexList").ItemCount);
            Assert.True(Named<Button>(view, "RunButton").IsEnabled);
            Assert.False(Named<Button>(view, "CancelButton").IsVisible);
            Assert.True(Shows(view, "가장 나음 · 28.8 배"));
            Assert.True(Shows(view, "행 수 다름 (−5)"));
            Assert.True(Shows(view, "1,842 ms"));
            // 숫자 칸은 오른쪽 맞춤(num 클래스)
            var elapsed = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "1,842 ms");
            Assert.Equal(Avalonia.Media.TextAlignment.Right, elapsed.TextAlignment);
            Shots(window, "ready");

            vm.CandidateTab = 1;
            vm.SelectedRewrite = vm.RewriteItems.First(r => r.Advice.Rule == RewriteRule.NotInToNotExists);
            Pump();
            Assert.True(Shows(view, vm.RewriteCaution!));
            Shots(window, "rewrite");

            vm.ShowRewriteDiff = true;
            Pump();
            Shots(window, "rewrite-diff");

            vm.CandidateTab = 2;
            Pump();
            Assert.Equal(vm.HintItems.Count, Named<ListBox>(view, "HintList").ItemCount);
            Shots(window, "hints");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task No_plan_state_offers_the_plan_buttons() => Ui.Run(async () =>
    {
        var (vm, _, _) = await TuningAssistantTests.LoadedAsync(noPlan: true);
        vm.FetchActualPlan = _ => Task.FromResult<Oranda.Core.Plans.ExecutionPlan?>(null);
        vm.CandidateTab = 0;
        var (view, window) = Show(vm);
        try
        {
            Assert.True(Named<Button>(view, "ActualPlanButton").IsEffectivelyVisible);
            Assert.True(Named<Button>(view, "ActualPlanButton").IsEnabled);
            Assert.False(Named<Button>(view, "EstimatedPlanButton").IsEnabled);
            Assert.True(Shows(view, "실제 계획이 있어야 후보를 고릅니다"));
            Shots(window, "no-plan");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task No_session_no_statement_rejected_and_error_states_render() => Ui.Run(async () =>
    {
        var noSession = new TuningAssistantViewModel(new TuningAssistantTests.FakeTuning()) { PostToUi = a => a() };
        await noSession.LoadAsync(new TuningAssistantInput(TuningAssistantTests.Statement(), null) { SourceTitle = "query3.sql" });
        var (view, window) = Show(noSession);
        try
        {
            Assert.True(Shows(view, noSession.NoticeText));
            Assert.False(Named<Button>(view, "RunButton").IsEnabled);
            Shots(window, "no-session");

            var empty = new TuningAssistantViewModel(new TuningAssistantTests.FakeTuning()) { PostToUi = a => a() };
            await empty.LoadAsync(new TuningAssistantInput(null, TuningAssistantTests.Session()));
            view.DataContext = empty;
            Pump();
            Assert.True(Shows(view, "분석할 문장이 없습니다"));
            Shots(window, "no-statement");

            var (rejected, _, _) = await TuningAssistantTests.LoadedAsync(sql: "UPDATE mes.lot_hist SET evt_cd = 'X' WHERE eqp_id = 'E01'", noPlan: true);
            view.DataContext = rejected;
            Pump();
            Assert.False(Named<Button>(view, "RunButton").IsEnabled);
            Assert.Equal("SELECT 문만 벤치마크합니다(이 문장: UPDATE).", ToolTip.GetTip(Named<Button>(view, "RunButton")));
            Assert.True(rejected.RewriteEmpty);
            Assert.True(Shows(view, TuningAssistantViewModel.BenchmarkEmptyText));
            rejected.CandidateTab = 1;
            Pump();
            Assert.True(Shows(view, TuningAssistantViewModel.RewriteEmptyText));
            Shots(window, "rejected");

            var (failed, _, _) = await TuningAssistantTests.LoadedAsync(noPlan: true);
            failed.FetchEstimatedPlan = _ => throw new InvalidOperationException("ORA-01031: insufficient privileges");
            await failed.FetchEstimatedPlanAsync();
            view.DataContext = failed;
            Pump();
            Assert.True(Shows(view, "ORA-01031: insufficient privileges"));
            Shots(window, "error");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Running_state_shows_progress_and_cancel() => Ui.Run(async () =>
    {
        var tuning = new TuningAssistantTests.FakeTuning { BlockOnCall = 4 };
        var (vm, _, _) = await TuningAssistantTests.LoadedAsync(session: TuningAssistantTests.Session(ConnectionEnvironment.Development), tuning: tuning);
        var (view, window) = Show(vm);
        try
        {
            var run = vm.RunBenchmarkAsync();
            while (!tuning.Blocked.Task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }

            Pump();
            Assert.True(Named<Button>(view, "CancelButton").IsVisible);
            Assert.False(Named<Button>(view, "RunButton").IsEnabled);
            Assert.True(Shows(view, vm.BenchmarkProgress));
            Shots(window, "running");
            vm.CancelBenchmark();
            while (!run.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }

            Pump();
            Assert.False(Named<Button>(view, "CancelButton").IsVisible);
            Shots(window, "cancelled");
        }
        finally
        {
            window.Close();
        }
    });
}
