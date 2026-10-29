using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>리터럴 가림이 들어간 화면의 확인: 운영 접속 시작 확인 창과 설정 확인란이 창 안에 들어가고, <c>ORANDA_RENDER_DIR</c> 가 있으면 PNG 로 남긴다.</summary>
public class AiMaskScreenTests
{
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

    private static bool Inside(Control control, Window window)
    {
        var origin = control.TranslatePoint(default, window);
        return origin is { } p && p.X >= -0.5 && p.Y >= -0.5 && p.X + control.Bounds.Width <= window.Bounds.Width + 0.5 && p.Y + control.Bounds.Height <= window.Bounds.Height + 0.5;
    }

    [Fact]
    public Task Production_start_dialog_shows_the_mask_line_the_notice_and_the_three_models() => HeadlessHost.Run(() =>
    {
        var info = AiTuningLauncher.BuildStartInfo(
            null,
            new AiContext("PROD-ERP", ConnectionEnvironment.Production, AiAccessLevel.Query),
            new AiSettings { Model = "sonnet" },
            maskLiterals: true) with { Title = "AI 튜닝 — 커서 위치 문장", StatementSummary = "8~15행 · SELECT d.dept_id, d.dept_nm, COUNT(*) …" };
        var dialog = new TuningStartDialog { DataContext = new TuningStartViewModel(info) };
        dialog.Show();

        Assert.True(dialog.FindControl<TextBlock>("MaskLine")!.IsVisible);
        Assert.True(dialog.FindControl<Border>("ProductionBanner")!.IsVisible);
        Assert.True(dialog.FindControl<CheckBox>("BenchmarkCheck")!.IsEnabled);
        Assert.Equal(3, dialog.FindControl<ComboBox>("ModelCombo")!.ItemCount);
        Assert.True(Inside(dialog.FindControl<Button>("StartButton")!, dialog));
        Assert.True(Inside(dialog.FindControl<Button>("CancelButton")!, dialog));
        Render(dialog, "ai-start-production");
        dialog.Close();
    });

    [Fact]
    public Task Settings_view_keeps_the_mask_checkbox_inside_the_window() => HeadlessHost.Run(() =>
    {
        var window = new Window { Width = 640, Height = 220, Content = new AiModelSettingsView { DataContext = new AiModelSettingsPage(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet" }) } };
        window.Show();

        var check = ((AiModelSettingsView)window.Content!).FindControl<CheckBox>("MaskLiteralsCheck")!;
        Assert.True(Inside(check, window));
        Render(window, "ai-settings-mask");
        window.Close();
    });
}
