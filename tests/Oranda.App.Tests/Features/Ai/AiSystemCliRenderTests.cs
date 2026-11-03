using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 시스템 CLI 가 「준비 중」 인 화면의 헤드리스 확인(cli-render): 설정 드롭다운의 「준비 중」 줄, 설정 파일에 CLI id 가 남은 경우의 설정 화면,
/// 같은 경우의 튜닝 시작 창 모델 줄. <c>ORANDA_RENDER_DIR</c> 가 있을 때만 PNG(<c>cli-*.png</c>)를 쓴다. 저장소에 넣지 않는다.
/// </summary>
public class AiSystemCliRenderTests
{
    private static readonly AiContext Connection = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);

    private static void Render(Control control, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        if (control is Window window)
        {
            window.UpdateLayout();
        }

        using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))));
        bmp.Render(control);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    private static AiModelSettingsPage Page(AiSettings ai) => new(ai, new AiProfileServices(new SettingsService(new MemorySettingsStore())));

    private static Window Host(AiModelSettingsPage page)
    {
        var view = new AiModelSettingsView { DataContext = page, Margin = new Thickness(24) };
        var window = new Window { Width = 640, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static T Find<T>(Window window, string name)
        where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task The_open_dropdown_lists_both_system_clis_as_coming_soon_rows() => HeadlessHost.Run(() =>
    {
        var page = Page(new AiSettings { Profiles = [ProfileSamples.Example()] });
        var window = Host(page);
        try
        {
            Find<Button>(window, "ProfileBox").Command!.Execute(null);
            Assert.True(page.IsDropdownOpen);
            Dispatcher.UIThread.RunJobs();

            var disabled = page.Choices.Where(c => c.Kind == AiChoiceKind.DisabledCli).ToList();
            Assert.Equal(["Antigravity CLI", "Codex CLI"], disabled.Select(c => c.Title));
            Assert.All(disabled, c => Assert.False(c.Selectable));

            var popup = window.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "ProfilePopup");
            var list = popup.Child!.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "ChoiceList");
            Assert.Equal(page.Choices.Count, list.ItemsSource is System.Collections.IEnumerable e ? e.Cast<object>().Count() : -1);
            Render(popup.Child as Control ?? window, "cli-settings-dropdown");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(AiProfilePresets.CodexCliId, "cli-settings-stored-codex")]
    [InlineData(AiProfilePresets.AntigravityCliId, "cli-settings-stored-antigravity")]
    public Task A_stored_cli_id_shows_the_problem_note_and_its_coming_soon_row_in_the_settings_page(string id, string file) => HeadlessHost.Run(() =>
    {
        var page = Page(new AiSettings { SystemCliId = id });
        var window = Host(page);
        try
        {
            Assert.True(Find<StackPanel>(window, "ProblemPanel").IsEffectivelyVisible);
            Assert.NotEmpty(Find<TextBlock>(window, "ProblemText").Text ?? string.Empty);
            Assert.False(Find<ComboBox>(window, "ModelBox").IsEffectivelyVisible); // Claude 모델 칸을 보이지 않는다
            Render(window, file);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(AiProfilePresets.CodexCliId, "Codex CLI", "cli-tuning-start-model")]
    [InlineData(AiProfilePresets.AntigravityCliId, "Antigravity CLI", "cli-tuning-start-model-antigravity")]
    public Task A_stored_cli_id_shows_one_model_row_named_after_the_cli_in_the_tuning_start_window(string id, string display, string file) => HeadlessHost.Run(() =>
    {
        var info = AiTuningLauncher.BuildStartInfo(null, Connection, new AiSettings { SystemCliId = id, Model = "sonnet", Effort = "high" });
        var dialog = new TuningStartDialog { DataContext = new TuningStartViewModel(info) };
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            var model = dialog.FindControl<ComboBox>("ModelCombo")!;
            Assert.Equal(1, model.ItemCount);
            Assert.Equal(display, ((TuningModelOption)model.SelectedItem!).Title);
            Assert.False(dialog.FindControl<ComboBox>("EffortCombo")!.IsEffectivelyVisible); // Claude 의 노력 줄이 없다
            Render(dialog, file);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task The_claude_code_tuning_start_window_still_has_the_three_model_rows() => HeadlessHost.Run(() =>
    {
        var info = AiTuningLauncher.BuildStartInfo(null, Connection, new AiSettings { Model = "sonnet", Effort = "high" });
        var dialog = new TuningStartDialog { DataContext = new TuningStartViewModel(info) };
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            Assert.Equal(3, dialog.FindControl<ComboBox>("ModelCombo")!.ItemCount);
            Render(dialog, "cli-tuning-start-model-claude");
        }
        finally
        {
            dialog.Close();
        }
    });
}
