using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>계획 트리 열 너비 조절(p1·p2): 모델(최소·기본·저장 왕복)과 화면(머리글·줄 동시 변경, 끌기·두 번 누르기).</summary>
public class PlanColumnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-plan-cols-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Set_clamps_to_minimum_and_reset_returns_default()
    {
        var columns = new PlanColumnWidths();
        var changed = new List<string>();
        columns.Changed += (_, key) => changed.Add(key);

        columns.Set("c-op", 5);
        Assert.Equal(PlanColumnWidths.MinOf("c-op"), columns.Get("c-op"));
        columns.Set("c-op", 5); // 같은 값이면 알리지 않는다
        columns.Set("c-op", 400);
        columns.Reset("c-op");

        Assert.Equal(PlanColumnWidths.DefaultOf("c-op"), columns.Get("c-op"));
        Assert.Equal(["c-op", "c-op", "c-op"], changed);
        Assert.Throws<ArgumentException>(() => columns.Set("c-nope", 50));
    }

    [Fact]
    public void Save_and_load_round_trip_keeps_only_changed_widths_and_survives_a_broken_file()
    {
        var columns = new PlanColumnWidths(_dir);
        columns.Set("c-obj", 222);
        columns.Set("c-arows", 70); // 실제 계획 열도 저장된다
        columns.Save();

        var json = File.ReadAllText(Path.Combine(_dir, PlanColumnWidths.FileName));
        Assert.Contains("c-obj", json);
        Assert.DoesNotContain("c-op", json);

        var reloaded = new PlanColumnWidths(_dir);
        Assert.Equal(222, reloaded.Get("c-obj"));
        Assert.Equal(70, reloaded.Get("c-arows"));
        Assert.Equal(PlanColumnWidths.DefaultOf("c-op"), reloaded.Get("c-op"));

        File.WriteAllText(Path.Combine(_dir, PlanColumnWidths.FileName), "{ not json");
        Assert.Equal(PlanColumnWidths.DefaultOf("c-obj"), new PlanColumnWidths(_dir).Get("c-obj"));
    }

    [Fact]
    public Task Changing_a_width_moves_header_and_every_row_together_and_the_drag_and_double_click_work() => HeadlessHost.Run(() =>
    {
        var vm = new PlanViewModel { Columns = new PlanColumnWidths(_dir) };
        vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), string.Empty), "예상");
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1600, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var header = view.FindControl<Border>("TreeHeader")!;
            var list = view.FindControl<ListBox>("PlanList")!;
            Control HeadCell(string key) => header.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains(key));
            IEnumerable<Control> RowCells(string key) =>
                list.GetVisualDescendants().OfType<ListBoxItem>().Select(i => i.GetVisualDescendants().OfType<Control>().First(c => c.Classes.Contains(key)));

            var before = HeadCell("c-obj").Bounds.Width;
            Assert.Equal(PlanColumnWidths.DefaultOf("c-obj"), before, 1);

            // 끌기: 손잡이를 눌러 오른쪽으로 40 DIP.
            var grip = HeadCell("c-obj").GetVisualDescendants().OfType<PlanColumnGrip>().Single();
            var at = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseMove(at + new Point(40, 0));
            window.MouseUp(at + new Point(40, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(before + 40, HeadCell("c-obj").Bounds.Width, 1);
            Assert.All(RowCells("c-obj"), c => Assert.Equal(before + 40, c.Bounds.Width, 1));
            // 뒤 칸은 그만큼 밀리고 앞 칸은 그대로: 머리글과 줄이 계속 같은 자리.
            Assert.Equal(
                HeadCell("c-risk").TranslatePoint(default, window)!.Value.X,
                RowCells("c-risk").First().TranslatePoint(default, window)!.Value.X,
                1);
            Assert.Equal(before + 40, new PlanColumnWidths(_dir).Get("c-obj")); // 놓을 때 저장

            // 최소 너비 아래로는 줄지 않는다.
            window.MouseDown(at + new Point(40, 0), MouseButton.Left);
            window.MouseMove(at + new Point(-400, 0));
            window.MouseUp(at + new Point(-400, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PlanColumnWidths.MinOf("c-obj"), HeadCell("c-obj").Bounds.Width, 1);

            // 두 번 누르면 기본 폭.
            var now = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(now, MouseButton.Left);
            window.MouseUp(now, MouseButton.Left);
            window.MouseDown(now, MouseButton.Left);
            window.MouseUp(now, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PlanColumnWidths.DefaultOf("c-obj"), HeadCell("c-obj").Bounds.Width, 1);
            Assert.All(RowCells("c-obj"), c => Assert.Equal(PlanColumnWidths.DefaultOf("c-obj"), c.Bounds.Width, 1));

            Render(window);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Saved_widths_apply_when_a_new_view_opens() => HeadlessHost.Run(() =>
    {
        var first = new PlanColumnWidths(_dir);
        first.Set("c-op", 380);
        first.Save();

        var vm = new PlanViewModel { Columns = new PlanColumnWidths(_dir) };
        vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), string.Empty), "예상");
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1600, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var cell = view.FindControl<ListBox>("PlanList")!.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("c-op"));
            Assert.Equal(380, cell.Bounds.Width, 1);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    /// <summary><c>ORANDA_RENDER_DIR</c> 가 있으면 창을 PNG 로 저장한다(화면 확인용).</summary>
    private static void Render(Window window)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        window.UpdateLayout();
        using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, "plan-cols-resized.png"));
#pragma warning restore CS0618
    }
}
