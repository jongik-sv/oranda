using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 화면 뼈대의 배치 확인(ai-ui u7): 창 폭 안에서 잘리지 않고, 시안의 핵심 칸이 보인다.</summary>
public class AiScreenLayoutTests
{
    /// <summary><c>ORANDA_RENDER_DIR</c> 가 있으면 창을 PNG 로 저장한다(시안 대조용). 없으면 아무것도 하지 않는다.</summary>
    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    /// <summary>컨트롤이 창 안에 완전히 들어 있다(Headless 글자는 실제보다 넓으므로 여기서 안 잘리면 실제로도 안 잘린다).</summary>
    private static bool Inside(Control control, Window window)
    {
        var origin = control.TranslatePoint(default, window);
        return origin is { } p && p.X >= -0.5 && p.Y >= -0.5 && p.X + control.Bounds.Width <= window.Bounds.Width + 0.5 && p.Y + control.Bounds.Height <= window.Bounds.Height + 0.5;
    }

    [Fact]
    public Task Start_dialog_keeps_buttons_and_options_inside_its_width() => HeadlessHost.Run(() =>
    {
        var model = new TuningStartViewModel(new TuningStartInfo(
            "AI 튜닝 — 커서 위치 문장", "8~15행 · SELECT d.dept_id, d.dept_nm, COUNT(*) …", "sql_id 7h35uxf5uhmm1 · 실행 1,240회 · 실행당 1.82 s",
            "문장 + 참조 객체: TB_ORDER 1,823만 행 · TB_DEPT 120 행 · CLOSED_DEPTS 36 행 (통계 어제)",
            "실제 계획 · 인덱스(열 순서·clustering factor) · 표·열 통계 · 히스토그램 개수", "행 값 · 히스토그램 끝값 · 바인드 값 · 비밀번호·호스트",
            new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query), [new TuningModelOption("default", "기본 모델 — Claude Code · opus")]));
        var dialog = new TuningStartDialog { DataContext = model };
        dialog.Show();

        Assert.Equal(470, dialog.Width);
        Assert.True(Inside(dialog.FindControl<Button>("StartButton")!, dialog));
        Assert.True(Inside(dialog.FindControl<Button>("CancelButton")!, dialog));
        Assert.True(dialog.FindControl<CheckBox>("BenchmarkCheck")!.IsEnabled);
        Assert.False(dialog.FindControl<Border>("BlockedBanner")!.IsVisible);
        Render(dialog, "ai-start");
        dialog.Close();
    });

    [Fact]
    public Task Chat_view_keeps_the_input_row_inside_a_440_wide_panel_and_marks_running_tools() => HeadlessHost.Run(() =>
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝 — 부서별 주문 수 (8~15행)");
        vm.BeginTurn("이 문장 튜닝 · sql_id 7h35uxf5uhmm1 · 벤치마크 켬");
        vm.AddToolCall("get_cursor_plan", "1.4 KB", "허용");
        vm.AddToolCall("benchmark_queries ×3", "실행 중", "허용", done: false);
        var window = new Window { Width = 440, Height = 600, Content = new AiChatView { DataContext = vm } };
        window.Show();

        var view = (AiChatView)window.Content!;
        Assert.True(Inside(view.FindControl<Button>("StopButton")!, window));
        Assert.True(Inside(view.FindControl<Button>("SendButton")!, window));
        Assert.Equal(["✓", "…"], vm.Entries.OfType<ChatToolEntry>().Select(e => e.Mark));
        Render(window, "ai-chat");
        window.Close();
    });

    [Fact]
    public Task Access_view_fits_five_levels_in_one_row_and_caps_production_at_plan() => HeadlessHost.Run(() =>
    {
        var vm = new ConnectionAiAccessViewModel(new ConnectionProfile("PROD-ERP", "APP_OWNER", "db-example", Environment: ConnectionEnvironment.Production, AiAccess: AiAccessLevel.Plan));
        var window = new Window { Width = 720, Height = 560, Content = new ConnectionAiAccessView { DataContext = vm } };
        window.Show();

        var view = (ConnectionAiAccessView)window.Content!;
        var toggles = view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t is not CheckBox).ToList(); // CheckBox 도 ToggleButton 이다
        Assert.Equal(5, toggles.Count);
        Assert.Single(toggles.Select(t => Math.Round(t.TranslatePoint(default, window)!.Value.Y)).Distinct()); // 한 줄
        Assert.All(toggles, t => Assert.True(Inside(t, window)));
        Assert.Equal([true, true, true, false, false], toggles.Select(t => t.IsEnabled)); // 운영 상한 = 계획
        Render(window, "ai-access");
        window.Close();
    });
}
