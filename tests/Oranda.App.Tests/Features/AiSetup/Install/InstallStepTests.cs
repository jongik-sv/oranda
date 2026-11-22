using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Install;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Install;

/// <summary>U3 설치 단계(가짜 서비스): 이미 있음, 설치 성공, 실패 → 직접 설치 한 줄·복사, 다시 확인, 취소.</summary>
public sealed class InstallStepTests
{
    private static AiSetupContext Context(FakeClaude? claude, List<string>? copied = null, AiHostPlatform platform = AiHostPlatform.Linux) =>
        new(new SettingsService(new InMemorySettingsStore()))
        {
            Claude = claude,
            Text = new ShellText(language: "ko"),
            Platform = platform,
            CopyText = text =>
            {
                copied?.Add(text);
                return Task.CompletedTask;
            },
        };

    [Fact]
    public async Task Already_installed_shows_the_version_and_completes()
    {
        var claude = new FakeClaude { Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330") };
        using var step = new InstallStep(Context(claude));
        await step.EnterAsync(CancellationToken.None);
        Assert.Equal(InstallState.Installed, step.State);
        Assert.True(step.IsComplete);
        Assert.Contains("2.1.330", step.StatusTitle, StringComparison.Ordinal);
        Assert.False(step.CanInstall);
        Assert.Equal(0, claude.Installs);
    }

    [Fact]
    public async Task Install_success_finds_again_and_completes()
    {
        var claude = new FakeClaude();
        using var step = new InstallStep(Context(claude));
        await step.EnterAsync(CancellationToken.None);
        Assert.Equal(InstallState.Missing, step.State);
        Assert.True(step.CanInstall);
        Assert.False(step.IsComplete);

        await step.InstallAsync();
        Assert.Equal(1, claude.Installs);
        Assert.Equal(InstallState.Installed, step.State);
        Assert.Equal("2.1.330", step.Version);
        Assert.True(step.IsComplete);
        Assert.Contains("installing", step.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_shows_plain_text_manual_line_and_copy()
    {
        var copied = new List<string>();
        var claude = new FakeClaude { InstallFails = AiSetupErrorKind.Network };
        using var step = new InstallStep(Context(claude, copied, AiHostPlatform.Windows));
        await step.EnterAsync(CancellationToken.None);
        await step.InstallAsync();

        Assert.Equal(InstallState.Failed, step.State);
        Assert.False(step.IsComplete);
        Assert.Equal(AiSetupErrorText.For(AiSetupErrorKind.Network, false), step.StatusDetail);
        Assert.True(step.ShowsManualOpen);
        Assert.Equal(ClaudeInstallCommands.Windows, step.ManualCommand);
        Assert.Contains("PowerShell", step.ManualHow, StringComparison.Ordinal);
        Assert.True(step.CanInstall);

        await step.CopyManualAsync();
        Assert.Equal([ClaudeInstallCommands.Windows], copied);
        Assert.Equal(InstallText.Copied(false), step.CopyNote);
    }

    [Fact]
    public async Task Recheck_after_manual_install_completes_and_only_finds()
    {
        var claude = new FakeClaude { InstallFails = AiSetupErrorKind.Unknown };
        using var step = new InstallStep(Context(claude));
        await step.EnterAsync(CancellationToken.None);
        await step.InstallAsync();
        Assert.Equal(InstallState.Failed, step.State);

        await step.RecheckAsync();
        Assert.Equal(InstallState.Failed, step.State);
        Assert.Equal(InstallText.StillMissing(false), step.StatusDetail);

        claude.Installed = new ClaudeInstallation("/usr/bin/claude", "2.2.0");
        await step.RecheckAsync();
        Assert.Equal(InstallState.Installed, step.State);
        Assert.True(step.IsComplete);
        Assert.Equal(1, claude.Installs);
    }

    [Fact]
    public async Task Leaving_while_installing_asks_first_and_cancels()
    {
        var claude = new FakeClaude { InstallGate = new TaskCompletionSource() };
        using var step = new InstallStep(Context(claude));
        await step.EnterAsync(CancellationToken.None);
        var installing = step.InstallAsync();
        Assert.True(step.IsInstalling);
        Assert.Equal(InstallText.LeaveQuestion(false), step.LeaveQuestion);

        await step.LeaveAsync(CancellationToken.None);
        await installing;
        Assert.Equal(InstallState.Missing, step.State);
        Assert.Null(step.LeaveQuestion);
        Assert.False(step.IsComplete);
    }

    [Fact]
    public async Task Without_the_service_only_manual_install_is_offered()
    {
        using var step = new InstallStep(Context(null));
        await step.EnterAsync(CancellationToken.None);
        Assert.Equal(InstallState.Unavailable, step.State);
        Assert.False(step.CanInstall);
        Assert.Equal(ClaudeInstallCommands.Unix, step.ManualCommand);
    }

    [Fact]
    public async Task Wizard_back_during_install_asks_and_no_keeps_the_step()
    {
        var claude = new FakeClaude { InstallGate = new TaskCompletionSource() };
        var context = Context(claude);
        var factories = FakeStepFactory.All(AiSetupStepId.Install).Values.Cast<IAiSetupStepFactory>().Append(new InstallStepFactory());
        using var vm = new AiSetupWizardViewModel(context, factories);
        await vm.StartAsync();
        context.ChosenPath = AiSetupPath.ClaudeAccount;
        ((FakeStep)vm.CurrentStep).IsComplete = true;
        await vm.NextAsync();
        var install = Assert.IsType<InstallStep>(vm.CurrentStep);
        var installing = install.InstallAsync();

        var back = vm.BackAsync();
        Assert.Equal(InstallText.LeaveQuestion(false), vm.PendingQuestion);
        vm.AnswerLeave(false);
        await back;
        Assert.Same(install, vm.CurrentStep);
        Assert.True(install.IsInstalling);

        back = vm.BackAsync();
        vm.AnswerLeave(true);
        await back;
        await installing;
        Assert.Equal(AiSetupStepId.Choose, vm.CurrentStep.StepId);
        Assert.False(install.IsInstalling);
    }

    private static async Task<(AiSetupWizardViewModel Vm, InstallStep Install, Task Installing, FakeClaude Claude)> InstallingWizardAsync()
    {
        var claude = new FakeClaude { InstallGate = new TaskCompletionSource() };
        var context = Context(claude);
        var factories = FakeStepFactory.All(AiSetupStepId.Install).Values.Cast<IAiSetupStepFactory>().Append(new InstallStepFactory());
        var vm = new AiSetupWizardViewModel(context, factories);
        await vm.StartAsync();
        context.ChosenPath = AiSetupPath.ClaudeAccount;
        ((FakeStep)vm.CurrentStep).IsComplete = true;
        await vm.NextAsync();
        var install = Assert.IsType<InstallStep>(vm.CurrentStep);
        return (vm, install, install.InstallAsync(), claude);
    }

    [Fact]
    public async Task Close_while_the_back_question_is_open_keeps_the_install_step_for_resume()
    {
        var (vm, install, installing, _) = await InstallingWizardAsync();
        using var _ = vm;
        var back = vm.BackAsync();
        var close = vm.ConfirmLeaveAsync(forClose: true);
        await back;
        Assert.Same(install, vm.CurrentStep);
        Assert.NotNull(vm.PendingQuestion);

        vm.AnswerLeave(true);
        Assert.True(await close);
        await vm.CloseAsync();
        await installing;
        Assert.Equal(AiSetupStepId.Install, vm.Context.Settings.Current.Ai.Setup.LastStep);
    }

    [Fact]
    public async Task Question_goes_away_by_itself_when_the_install_ends()
    {
        var (vm, install, installing, claude) = await InstallingWizardAsync();
        using var _ = vm;
        var back = vm.BackAsync();
        Assert.NotNull(vm.PendingQuestion);
        claude.InstallGate!.SetResult();
        await installing;
        await back;
        Assert.Null(vm.PendingQuestion);
        Assert.Equal(AiSetupStepId.Choose, vm.CurrentStep.StepId);
        Assert.Equal(InstallState.Installed, install.State);
    }

    [Fact]
    public async Task Recheck_is_ignored_while_installing()
    {
        var claude = new FakeClaude { InstallGate = new TaskCompletionSource() };
        using var step = new InstallStep(Context(claude));
        await step.EnterAsync(CancellationToken.None);
        var installing = step.InstallAsync();
        var finds = claude.Finds;
        Assert.False(step.CanRecheck);
        await step.RecheckAsync();
        Assert.Equal(finds, claude.Finds);
        claude.InstallGate.SetResult();
        await installing;
        Assert.Equal(InstallState.Installed, step.State);
    }

    [Fact]
    public Task Failure_view_shows_the_manual_line_open() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaude { InstallFails = AiSetupErrorKind.Network };
        using var step = new InstallStep(Context(claude));
        var window = new Window { Width = 600, Height = 500, Content = step.View };
        window.Show();
        try
        {
            await step.EnterAsync(CancellationToken.None);
            await step.InstallAsync();
            HeadlessHost.Settle(window);
            Assert.True(step.ManualBox!.IsExpanded);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(ClaudeInstallCommands.Unix, texts);
            Assert.Contains(InstallText.Failed(false), texts);
        }
        finally
        {
            window.Close();
        }
    });
}
