using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Choose;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Choose;

/// <summary>U2 방법 고르기: 카드 고름 = 끝남, 설치됨 배지, 이미 연결됨 줄, 조회만 Role 안내·단추.</summary>
public sealed class ChooseStepTests
{
    private static AiSetupContext Context(FakeClaude? claude, Func<AppSettings, AppSettings>? change = null, string language = "ko")
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        if (change is not null)
        {
            settings.UpdateAsync(change).GetAwaiter().GetResult();
        }

        return new AiSetupContext(settings) { Claude = claude, Text = new ShellText(language: language), Platform = Oranda.Core.Ai.Profiles.AiHostPlatform.Linux };
    }

    private static AppSettings NoAi(AppSettings s) => s with { Ai = s.Ai with { Driver = AiDriverKind.None } };

    [Fact]
    public async Task Picking_a_card_sets_the_path_and_completes()
    {
        var context = Context(new FakeClaude(), NoAi);
        using var step = new ChooseStep(context);
        await step.EnterAsync(CancellationToken.None);
        Assert.False(step.IsComplete);
        Assert.True(step.ShowsCards);

        step.Select(AiSetupPath.CompanyServer);
        Assert.True(step.IsComplete);
        Assert.Equal(AiSetupPath.CompanyServer, context.ChosenPath);
    }

    [Fact]
    public async Task Installed_badge_follows_finding_claude()
    {
        var claude = new FakeClaude();
        using (var none = new ChooseStep(Context(claude, NoAi)))
        {
            await none.EnterAsync(CancellationToken.None);
            await none.Refreshing;
            Assert.False(none.ClaudeInstalled);
            Assert.False(none.AlreadyConnected);
        }

        claude.Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330");
        using var found = new ChooseStep(Context(claude, NoAi));
        await found.EnterAsync(CancellationToken.None);
        await found.Refreshing;
        Assert.True(found.ClaudeInstalled);
        Assert.Equal(2, claude.Finds);
    }

    [Fact]
    public async Task Already_connected_line_when_claude_code_is_the_target_and_found()
    {
        var claude = new FakeClaude { Installed = new ClaudeInstallation("/usr/bin/claude", null) };
        using (var notSignedIn = new ChooseStep(Context(claude)))
        {
            await notSignedIn.EnterAsync(CancellationToken.None);
            await notSignedIn.Refreshing;
            Assert.False(notSignedIn.AlreadyConnected);
        }

        claude.LastStatus = new ClaudeLoginStatus(true, null, StatusSupported: true);
        using var step = new ChooseStep(Context(claude));
        await step.EnterAsync(CancellationToken.None);
        await step.Refreshing;
        Assert.True(step.AlreadyConnected);

        // 기본 대상이 Claude Code 여도 claude 가 없으면 아직 아니다. AI 를 끈 설정도 아니다.
        Assert.False(ChooseStep.IsUsable(new AiSettings(), claudeFound: false, Oranda.Core.Ai.Profiles.AiHostPlatform.Linux));
        Assert.False(ChooseStep.IsUsable(new AiSettings { Driver = AiDriverKind.None }, claudeFound: true, Oranda.Core.Ai.Profiles.AiHostPlatform.Linux));
    }

    [Fact]
    public async Task Read_only_role_shows_the_notice_and_the_button_opens_role_settings()
    {
        var opened = 0;
        var context = Context(new FakeClaude(), s => s with { Features = s.Features with { Roles = [RoleIds.Viewer] } });
        using var step = new ChooseStep(context, () =>
        {
            opened++;
            return Task.CompletedTask;
        });
        await step.EnterAsync(CancellationToken.None);
        Assert.True(step.Blocked);
        Assert.True(step.IsReadOnlyRole);
        Assert.False(step.ShowsCards);
        Assert.Equal(ChooseText.ChangeRole(false), step.BlockedAction);

        step.Select(AiSetupPath.ClaudeAccount);
        Assert.False(step.IsComplete);
        Assert.Null(context.ChosenPath);

        // 기능 설정에서 Role 을 바꾸고 돌아오면 카드가 보인다.
        var changed = new List<string>();
        step.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        await context.Settings.UpdateAsync(s => s with { Features = s.Features with { Roles = [RoleIds.SqlDev] } });
        await step.OpenRolesAsync();
        Assert.Equal(1, opened);
        Assert.False(step.Blocked);
        Assert.True(step.ShowsCards);
        Assert.Contains(nameof(ChooseStep.ShowsCards), changed);
    }

    [Fact]
    public async Task Ai_tuning_turned_off_shows_the_feature_notice()
    {
        using var step = new ChooseStep(Context(new FakeClaude(), s => s with { Features = s.Features with { Disabled = [FeatureIds.AiTuning] } }));
        await step.EnterAsync(CancellationToken.None);
        Assert.True(step.Blocked);
        Assert.False(step.IsReadOnlyRole);
        Assert.Equal(ChooseText.OpenFeatures(false), step.BlockedAction);
    }

    [Fact]
    public async Task Restored_path_keeps_the_step_complete()
    {
        var context = Context(new FakeClaude(), NoAi);
        context.ChosenPath = AiSetupPath.NotSure;
        using var step = new ChooseStep(context);
        Assert.True(step.IsComplete);
        await step.EnterAsync(CancellationToken.None);
        Assert.True(step.IsComplete);
    }

    [Fact]
    public Task Cards_render_with_badges_and_clicking_selects() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330") };
        var context = Context(claude, NoAi);
        using var step = new ChooseStep(context);
        var window = new Window { Width = 600, Height = 500, Content = step.View };
        window.Show();
        try
        {
            await step.EnterAsync(CancellationToken.None);
            await step.Refreshing;
            HeadlessHost.Settle(window);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(ChooseText.Recommended(false), texts);
            Assert.Contains(ChooseText.Installed(false), texts);
            Assert.DoesNotContain(ChooseText.ReadOnlyHeadline(false), texts);

            step.Cards[AiSetupPath.ClaudeAccount].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(AiSetupPath.ClaudeAccount, context.ChosenPath);
            Assert.Contains(ChooseStep.SelectedClass, step.Cards[AiSetupPath.ClaudeAccount].Classes);
            Assert.DoesNotContain(ChooseStep.SelectedClass, step.Cards[AiSetupPath.NotSure].Classes);
        }
        finally
        {
            window.Close();
        }
    });
}
