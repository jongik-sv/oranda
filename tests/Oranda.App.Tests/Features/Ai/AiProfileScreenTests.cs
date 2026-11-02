using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 설정 「AI 모델」 쪽과 프로필 관리 창의 화면 확인(헤드리스). <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG 를 그 폴더에 쓴다
/// (<c>ai-settings-*.png</c>, <c>ai-profile-manager-*.png</c>). 시험 자체는 요소가 보이고 상태에 맞게 바뀌는지만 본다.
/// </summary>
public class AiProfileScreenTests
{
    private static void Render(Control control, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                if (control is Window window)
                {
                    window.UpdateLayout();
                }

                var size = new PixelSize(Math.Max(1, (int)control.Bounds.Width), Math.Max(1, (int)control.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(control);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }

    // 설정 창은 항상 서비스를 넘기므로 「프로필 관리」 단추는 활성이다. 시험 화면도 같게 맞춘다.
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
    public Task Settings_page_shows_the_claude_code_state_then_the_profile_state_with_the_http_warning() => HeadlessHost.Run(() =>
    {
        var exampleProfile = ProfileSamples.Example() with { Model = "example-model" };
        var page = Page(new AiSettings { Profiles = [exampleProfile, ProfileSamples.Ollama()] });
        var window = Host(page);
        try
        {
            Assert.Contains("selected", Find<Button>(window, "DirectCard").Classes);
            Assert.DoesNotContain("selected", Find<Button>(window, "NoneCard").Classes);
            Assert.True(Find<Button>(window, "ManageButton").IsEnabled);
            Assert.Equal("Claude Code", Find<TextBlock>(window, "ProfileTitle").Text);
            Assert.True(Find<ComboBox>(window, "ModelBox").IsEffectivelyVisible);
            Assert.False(Find<Border>(window, "ProfileInfo").IsEffectivelyVisible);
            Render(window, "ai-settings-claude");

            page.Choose(page.Choices.First(c => c.ProfileId == exampleProfile.Id));
            window.UpdateLayout();
            Assert.Equal("예제 LLM", Find<TextBlock>(window, "ProfileTitle").Text);
            Assert.False(Find<ComboBox>(window, "ModelBox").IsEffectivelyVisible);
            Assert.True(Find<Border>(window, "ProfileInfo").IsEffectivelyVisible);
            Assert.Equal("example-model", Find<TextBlock>(window, "ProfileModelText").Text);
            Assert.True(Find<StackPanel>(window, "PlainTextWarning").IsEffectivelyVisible);
            Assert.True(Find<CheckBox>(window, "MaskLiteralsCheck").IsEffectivelyVisible); // 항상 유지
            Render(window, "ai-settings-profile");

            page.IsNone = true;
            window.UpdateLayout();
            Assert.Contains("selected", Find<Button>(window, "NoneCard").Classes);
            Assert.False(Find<Button>(window, "ProfileBox").IsEffectivelyVisible);
            Assert.True(Find<CheckBox>(window, "MaskLiteralsCheck").IsEffectivelyVisible);
            Render(window, "ai-settings-none");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Settings_page_shows_why_a_deleted_profile_cannot_run() => HeadlessHost.Run(() =>
    {
        var page = Page(new AiSettings { ActiveProfileId = Guid.NewGuid(), Profiles = [ProfileSamples.Example()] });
        var window = Host(page);
        try
        {
            Assert.True(Find<StackPanel>(window, "ProblemPanel").IsEffectivelyVisible);
            Assert.Contains("찾을 수 없습니다", Find<TextBlock>(window, "ProblemText").Text);
            Assert.Equal("삭제된 프로필", Find<TextBlock>(window, "ProfileTitle").Text);
            Render(window, "ai-settings-missing");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_dropdown_lists_the_groups_and_a_row_click_chooses_the_profile() => HeadlessHost.Run(() =>
    {
        var exampleProfile = ProfileSamples.Example();
        var page = Page(new AiSettings { Profiles = [exampleProfile, ProfileSamples.Ollama()] });
        var window = Host(page);
        try
        {
            Find<Button>(window, "ProfileBox").Command!.Execute(null);
            Assert.True(page.IsDropdownOpen);
            Dispatcher.UIThread.RunJobs();

            var popup = window.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "ProfilePopup");
            var list = popup.Child!.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "ChoiceList");
            Assert.Equal(page.Choices.Count, list.ItemsSource is System.Collections.IEnumerable e ? e.Cast<object>().Count() : -1);
            Render(popup.Child as Control ?? window, "ai-settings-dropdown");

            page.Choose(page.Choices.First(c => c.ProfileId == exampleProfile.Id));
            Assert.False(page.IsDropdownOpen);
            Assert.Equal(exampleProfile.Id, page.ActiveProfileId);
        }
        finally
        {
            window.Close();
        }
    });

    private static async Task<(Window Window, AiProfileManagerViewModel Model)> OpenManagerAsync(ProfileHarness harness, Guid? select = null, bool startNew = false)
    {
        var model = harness.Manager(select: select, startNew: startNew);
        await model.Ready;
        var window = new AiProfileManagerWindow { DataContext = model, Width = 1040, Height = 900 };
        window.Show();
        window.UpdateLayout();
        return (window, model);
    }

    [Fact]
    public async Task Manager_window_shows_the_profile_list_the_seven_cards_and_the_result_band() => await HeadlessHost.Run(async () =>
    {
        var exampleProfile = ProfileSamples.Example() with { Model = "example-model", MaxInputTokens = 200000, MaxOutputTokens = 10000 };
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile, ProfileSamples.Ollama()));
        harness.Probe.TestResult = new AiConnectionTestResult(true, TimeSpan.FromSeconds(1), "응답 확인");
        var (window, model) = await OpenManagerAsync(harness, exampleProfile.Id);
        try
        {
            Assert.Equal(7, Find<ItemsControl>(window, "PresetList").GetVisualDescendants().OfType<Button>().Count());
            Assert.Equal(2, Find<ItemsControl>(window, "ProfileList").GetVisualDescendants().OfType<Button>().Count());
            Assert.Equal("http://llm.example:8080", Find<TextBox>(window, "BaseUrlBox").Text);
            Assert.Equal("예제 LLM", Find<TextBox>(window, "NameBox").Text);
            Assert.True(Find<StackPanel>(window, "PlainTextWarning").IsEffectivelyVisible);
            Assert.True(Find<Button>(window, "DeleteButton").IsEnabled);
            Assert.True(Find<Button>(window, "TestButton").IsEnabled);
            Assert.False(Find<StackPanel>(window, "ResultBand").IsEffectivelyVisible);

            await model.TestConnectionAsync(null);
            window.UpdateLayout();
            Assert.True(Find<StackPanel>(window, "ResultBand").IsEffectivelyVisible);
            Assert.Equal("연결 성공 · 1.0 초 · 응답 확인", Find<TextBlock>(window, "ResultText").Text);
            Render(window, "ai-profile-manager-existing");

            // 키 칸은 바인딩되지 않으며 창이 읽어 간다. 보기 전환은 가림 문자만 바꾼다.
            var keyBox = Find<TextBox>(window, "KeyBox");
            keyBox.Text = "secret-example";
            Assert.Equal("secret-example", model.EnteredKeyProvider!());
            Assert.Equal('●', keyBox.PasswordChar);
            model.ToggleKeyVisibleCommand.Execute(null);
            Assert.Equal('\0', keyBox.PasswordChar);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public async Task Manager_window_new_draft_shows_the_format_choice_only_for_custom_and_blocks_a_bad_save_with_messages() => await HeadlessHost.Run(async () =>
    {
        var harness = await ProfileHarness.CreateAsync();
        var (window, model) = await OpenManagerAsync(harness, startNew: true);
        try
        {
            Assert.True(Find<RadioButton>(window, "FormatAnthropicRadio").IsEnabled);
            Assert.False(Find<Button>(window, "DeleteButton").IsEnabled); // 저장 전 초안은 지울 수 없다

            model.Name = string.Empty;
            model.ModelName = string.Empty;
            await model.SaveCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Assert.True(Find<TextBlock>(window, "NameErrorText").IsEffectivelyVisible);
            Assert.True(Find<TextBlock>(window, "ModelErrorText").IsEffectivelyVisible);
            Assert.True(Find<TextBlock>(window, "UrlErrorText").IsEffectivelyVisible);
            Render(window, "ai-profile-manager-errors");

            model.SelectPreset(AiProfilePreset.Ollama);
            window.UpdateLayout();
            Assert.False(Find<RadioButton>(window, "FormatAnthropicRadio").IsEnabled);
            Assert.True(Find<RadioButton>(window, "FormatOpenAiRadio").IsChecked);
            Assert.Equal("http://localhost:11434/v1", Find<TextBox>(window, "BaseUrlBox").Text);
            Render(window, "ai-profile-manager-ollama");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public async Task Closing_the_manager_window_cancels_a_running_probe() => await HeadlessHost.Run(async () =>
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (window, model) = await OpenManagerAsync(harness);

        var running = model.TestConnectionAsync(null);
        Assert.True(model.IsBusy);
        Assert.True(Find<Button>(window, "StopButton").IsEffectivelyVisible);

        window.Close();
        await running.WaitAsync(Limit); // 취소는 예외로 새지 않고, 창이 닫힌 뒤 결과는 반영되지 않는다
        Assert.False(model.IsBusy);
    });

    [Fact]
    public async Task Refreshing_models_keeps_the_typed_name_and_picking_the_second_model_shows_it() => await HeadlessHost.Run(async () =>
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        harness.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("model-a"), new AiModelInfo("model-b")]);
        var (window, model) = await OpenManagerAsync(harness);
        try
        {
            var typed = model.ModelName;
            var box = Find<ComboBox>(window, "ModelBox");
            await model.RefreshModelsAsync(null);
            window.UpdateLayout();
            Assert.Equal(2, model.Models.Count);
            Assert.Equal(typed, model.ModelName); // 목록이 바뀌어도 입력한 모델명은 그대로
            Assert.Equal(typed, box.Text);

            box.SelectedIndex = 1;
            window.UpdateLayout();
            Assert.Equal("model-b", model.ModelName);
            Assert.Equal("model-b", box.Text);

            await model.RefreshModelsAsync(null); // 항목을 고른 뒤 다시 새로고침해도 고른 값이 남는다
            window.UpdateLayout();
            Assert.Equal("model-b", model.ModelName);
            Assert.Equal("model-b", box.Text);
        }
        finally
        {
            window.Close();
        }
    });

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_typed_key_does_not_follow_to_another_profile_and_is_cleared_after_a_save() => await HeadlessHost.Run(async () =>
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile, ProfileSamples.Ollama()));
        var (window, model) = await OpenManagerAsync(harness, exampleProfile.Id);
        try
        {
            var keyBox = Find<TextBox>(window, "KeyBox");
            keyBox.Text = "typed-example";
            await model.SelectProfileAsync(harness.Ai.Profiles.First(p => p.Id != exampleProfile.Id).Id);
            Assert.True(string.IsNullOrEmpty(keyBox.Text));

            keyBox.Text = "typed-example";
            Assert.True(await model.SaveProfileAsync(keyBox.Text));
            Assert.True(string.IsNullOrEmpty(keyBox.Text)); // 저장하면 입력한 키 글이 칸에 남지 않는다
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public async Task Closing_with_unsaved_changes_asks_first_and_stays_open_when_declined() => await HeadlessHost.Run(async () =>
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        var asked = 0;
        var answer = false;
        var model = harness.Manager(confirm: _ =>
        {
            asked++;
            return Task.FromResult(answer);
        });
        await model.Ready;
        var window = new AiProfileManagerWindow { DataContext = model, Width = 1040, Height = 900 };
        window.Show();
        model.Name = "고친 이름";

        window.Close();
        await HeadlessHost.WaitUntil(() => asked == 1, 20000);
        Assert.True(window.IsVisible); // 거절하면 닫히지 않는다

        answer = true;
        window.Close();
        await HeadlessHost.WaitUntil(() => !window.IsVisible, 20000);
        Assert.Equal(2, asked);
    });

    [Fact]
    public async Task The_settings_view_opens_the_manager_window_on_top_of_its_owner() => await HeadlessHost.Run(async () =>
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        var host = Host(harness.Page());
        try
        {
            var view = (AiModelSettingsView)host.Content!;
            var opened = view.OpenManagerAsync(new ProfileManagerRequestedEventArgs(null, startNew: true));
            await HeadlessHost.WaitUntil(() => host.OwnedWindows.OfType<AiProfileManagerWindow>().Any(), 20000);
            var manager = host.OwnedWindows.OfType<AiProfileManagerWindow>().Single();
            Assert.IsType<AiProfileManagerViewModel>(manager.DataContext);
            manager.Close();
            await opened.WaitAsync(Limit);
        }
        finally
        {
            host.Close();
        }
    });

    [Fact]
    public async Task The_settings_view_model_gets_the_real_credential_and_probe_services() => await HeadlessHost.Run(() =>
    {
        using var shell = new ShellHarness();
        var vm = new SettingsViewModel(shell.Get<SettingsService>(), new Oranda.App.Shell.ShellText(language: "ko"));
        Assert.NotNull(vm.AiModel.Services!.Credentials);
        Assert.NotNull(vm.AiModel.Services.Probe);
        Assert.NotNull(vm.AiModel.Services.Bus);
    });

    [Fact]
    public async Task Typing_in_the_key_box_marks_the_form_changed() => await HeadlessHost.Run(async () =>
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        var (window, model) = await OpenManagerAsync(harness);
        try
        {
            Assert.False(model.IsDirty);
            Find<TextBox>(window, "KeyBox").Text = "typed-example";
            await HeadlessHost.WaitUntil(() => model.IsDirty, 5000);
            Assert.True(model.IsDirty);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public async Task The_manage_button_command_opens_the_manager_through_the_page_event() => await HeadlessHost.Run(async () =>
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        var host = Host(harness.Page());
        try
        {
            ((AiModelSettingsPage)((Control)host.Content!).DataContext!).ManageProfilesCommand.Execute(null);
            await HeadlessHost.WaitUntil(() => host.OwnedWindows.OfType<AiProfileManagerWindow>().Any(), 20000);
            var manager = host.OwnedWindows.OfType<AiProfileManagerWindow>().Single();
            manager.Close();
            await HeadlessHost.WaitUntil(() => !host.OwnedWindows.OfType<AiProfileManagerWindow>().Any(), 20000);
        }
        finally
        {
            host.Close();
        }
    });
}
