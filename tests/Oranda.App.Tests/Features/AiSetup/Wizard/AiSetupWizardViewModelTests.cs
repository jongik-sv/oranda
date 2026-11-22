using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Settings;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Wizard;

/// <summary>U1 창 틀 뷰모델: 갈래별 단계 목록·[다음] 켜짐·[이전]·갈래 바꾸기·이어 하기·없는 단계 자리 표시.</summary>
public sealed class AiSetupWizardViewModelTests
{
    private sealed class Harness
    {
        public Harness(AiSetupSettings? resume = null, params AiSetupStepId[] missing)
        {
            Settings = new SettingsService(new InMemorySettingsStore());
            if (resume is not null)
            {
                Settings.UpdateAsync(s => s with { Ai = s.Ai with { Setup = resume } }).GetAwaiter().GetResult();
            }

            Factories = FakeStepFactory.All(missing);
        }

        public SettingsService Settings { get; }

        public Dictionary<AiSetupStepId, FakeStepFactory> Factories { get; }

        public AiSetupContext Context { get; private set; } = null!;

        public AiSetupWizardViewModel Create(AiSetupErrorKind? reason = null)
        {
            Context = new AiSetupContext(Settings) { Reason = reason, Text = new Oranda.App.Shell.ShellText(language: "ko") };
            return new AiSetupWizardViewModel(Context, Factories.Values);
        }

        public FakeStep Step(AiSetupStepId id) => Factories[id].Last;

        /// <summary>방법 고르기 가짜 단계에서 갈래를 고른다(실제 단계처럼 갈래를 넣고 끝남).</summary>
        public void Choose(AiSetupPath path)
        {
            Context.ChosenPath = path;
            Step(AiSetupStepId.Choose).IsComplete = true;
        }
    }

    private static AiSetupStepId[] RailIds(AiSetupWizardViewModel vm) => [.. vm.Rail.Select(r => r.StepId)];

    [Fact]
    public async Task Rail_follows_the_chosen_path_for_A_B_and_C()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.StartAsync();
        Assert.Equal([AiSetupStepId.Choose], RailIds(vm));

        h.Choose(AiSetupPath.ClaudeAccount);
        Assert.Equal(AiSetupFlow.StepsFor(AiSetupPath.ClaudeAccount), RailIds(vm));
        Assert.Equal(AiSetupRailState.Current, vm.Rail[0].State);
        Assert.Equal(AiSetupRailState.Pending, vm.Rail[1].State);

        h.Choose(AiSetupPath.CompanyServer);
        Assert.Equal(AiSetupFlow.StepsFor(AiSetupPath.CompanyServer), RailIds(vm));

        h.Choose(AiSetupPath.NotSure);
        Assert.Equal([AiSetupStepId.Choose, AiSetupStepId.AskAdmin], RailIds(vm));
        Assert.Equal("1 / 2", vm.ProgressText.Split(' ')[0] + " / " + vm.ProgressText.Split(' ')[2]);
    }

    [Fact]
    public async Task Next_is_enabled_only_when_the_current_step_is_complete_and_back_keeps_completion()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.StartAsync();
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoBack);

        h.Choose(AiSetupPath.ClaudeAccount);
        Assert.True(vm.CanGoNext);
        await vm.NextAsync();
        Assert.Equal(AiSetupStepId.Install, vm.CurrentStep.StepId);
        Assert.Equal(1, h.Step(AiSetupStepId.Choose).Leaves);
        Assert.Equal(1, h.Step(AiSetupStepId.Install).Enters);
        Assert.False(vm.CanGoNext);
        Assert.True(vm.CanGoBack);
        Assert.Equal(AiSetupRailState.Done, vm.Rail[0].State);

        // 아직 안 끝난 단계에서 [다음]을 눌러도 움직이지 않는다.
        Assert.False(await vm.NextAsync());
        Assert.Equal(AiSetupStepId.Install, vm.CurrentStep.StepId);

        h.Step(AiSetupStepId.Install).IsComplete = true;
        Assert.True(vm.CanGoNext);
        h.Step(AiSetupStepId.Install).IsComplete = false;
        Assert.False(vm.CanGoNext);
        h.Step(AiSetupStepId.Install).IsComplete = true;

        await vm.BackAsync();
        Assert.Equal(AiSetupStepId.Choose, vm.CurrentStep.StepId);
        Assert.Equal(AiSetupRailState.Done, vm.Rail[1].State);
        Assert.Single(h.Factories[AiSetupStepId.Choose].Created);
        Assert.Equal(2, h.Step(AiSetupStepId.Choose).Enters);
    }

    [Fact]
    public async Task Changing_path_resets_later_steps_and_makes_them_again()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.StartAsync();
        h.Choose(AiSetupPath.ClaudeAccount);
        await vm.NextAsync();
        var firstInstall = h.Step(AiSetupStepId.Install);
        firstInstall.IsComplete = true;
        await vm.BackAsync();

        h.Choose(AiSetupPath.CompanyServer);
        Assert.Equal(AiSetupStepId.Choose, vm.CurrentStep.StepId);
        Assert.True(firstInstall.Disposed);
        Assert.All(vm.Rail.Skip(1), r => Assert.Equal(AiSetupRailState.Pending, r.State));

        h.Choose(AiSetupPath.ClaudeAccount);
        await vm.NextAsync();
        Assert.NotSame(firstInstall, h.Step(AiSetupStepId.Install));
        Assert.Equal(2, h.Factories[AiSetupStepId.Install].Created.Count);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public async Task Missing_step_shows_a_placeholder_that_can_be_passed()
    {
        var h = new Harness(null, AiSetupStepId.Install);
        using var vm = h.Create();
        await vm.StartAsync();
        h.Choose(AiSetupPath.ClaudeAccount);
        await vm.NextAsync();

        Assert.IsType<AiSetupPlaceholderStep>(vm.CurrentStep);
        Assert.Equal(AiSetupStepId.Install, vm.CurrentStep.StepId);
        Assert.True(vm.CanGoNext);
        await vm.NextAsync();
        Assert.Equal(AiSetupStepId.Login, vm.CurrentStep.StepId);
    }

    [Fact]
    public async Task Next_requested_moves_only_when_the_step_is_complete()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.StartAsync();
        h.Choose(AiSetupPath.ClaudeAccount);
        await vm.NextAsync();
        var install = h.Step(AiSetupStepId.Install);

        install.RequestNext();
        await vm.Idle;
        Assert.Equal(AiSetupStepId.Install, vm.CurrentStep.StepId);

        install.IsComplete = true;
        install.RequestNext();
        await vm.Idle;
        Assert.Equal(AiSetupStepId.Login, vm.CurrentStep.StepId);
    }

    [Fact]
    public async Task Next_requested_while_entering_is_not_lost()
    {
        var h = new Harness();
        h.Factories[AiSetupStepId.Install].OnCreate = s =>
        {
            s.CompleteOnEnter = true;
            s.RequestNextOnEnter = true;
        };
        using var vm = h.Create();
        await vm.StartAsync();
        h.Choose(AiSetupPath.ClaudeAccount);
        await vm.NextAsync();
        await vm.Idle;
        Assert.Equal(AiSetupStepId.Login, vm.CurrentStep.StepId);
    }

    [Fact]
    public async Task Close_during_a_slow_enter_waits_and_does_not_make_more_steps()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Factories[AiSetupStepId.Install].OnCreate = s => s.EnterGate = gate;
        using var vm = h.Create();
        await vm.StartAsync();
        h.Choose(AiSetupPath.ClaudeAccount);
        var moving = vm.NextAsync();
        Assert.False(vm.CanGoNext);
        Assert.False(await vm.NextAsync());

        var closing = vm.CloseAsync();
        Assert.False(closing.IsCompleted);
        gate.SetResult();
        await moving;
        await closing;

        var install = h.Step(AiSetupStepId.Install);
        Assert.Equal(1, install.Leaves);
        Assert.True(install.Disposed);
        Assert.Empty(h.Factories[AiSetupStepId.Login].Created);
        Assert.Equal(AiSetupStepId.Install, h.Settings.Current.Ai.Setup.LastStep);
    }

    [Fact]
    public async Task Factory_failure_falls_back_to_the_placeholder()
    {
        var h = new Harness();
        h.Factories[AiSetupStepId.Install].OnCreate = _ => throw new InvalidOperationException("boom");
        using var vm = h.Create();
        await vm.StartAsync();
        h.Choose(AiSetupPath.ClaudeAccount);
        await vm.NextAsync();
        Assert.IsType<AiSetupPlaceholderStep>(vm.CurrentStep);
    }

    [Fact]
    public async Task Close_saves_path_and_step_and_reopen_starts_there()
    {
        var h = new Harness();
        using (var vm = h.Create())
        {
            await vm.StartAsync();
            h.Choose(AiSetupPath.ClaudeAccount);
            await vm.NextAsync();
            h.Step(AiSetupStepId.Install).IsComplete = true;
            await vm.NextAsync();
            await vm.CloseAsync();
            Assert.True(h.Step(AiSetupStepId.Login).Disposed);
            Assert.Equal(1, h.Step(AiSetupStepId.Login).Leaves);
        }

        var saved = h.Settings.Current.Ai.Setup;
        Assert.Equal(AiSetupPath.ClaudeAccount, saved.LastPath);
        Assert.Equal(AiSetupStepId.Login, saved.LastStep);

        using var again = h.Create();
        await again.StartAsync();
        Assert.Equal(AiSetupStepId.Login, again.CurrentStep.StepId);
        Assert.Equal(AiSetupPath.ClaudeAccount, h.Context.ChosenPath);
        Assert.Equal(AiSetupRailState.Done, again.Rail[0].State);
        Assert.Equal(AiSetupRailState.Done, again.Rail[1].State);
        Assert.False(again.CanGoNext);

        // 앞 단계로 돌아가면 그 단계가 다시 확인한다: 단계가 끝나지 않았다고 하면 [다음]이 꺼진다.
        await again.BackAsync();
        Assert.Equal(AiSetupStepId.Install, again.CurrentStep.StepId);
        Assert.False(again.CanGoNext);
    }

    [Fact]
    public async Task Finish_runs_finish_then_leave_and_clears_the_resume_values()
    {
        var h = new Harness(new AiSetupSettings { FirstRunPromptClosed = true });
        using var vm = h.Create();
        var finished = 0;
        vm.Finished += (_, _) => finished++;
        await vm.StartAsync();
        h.Choose(AiSetupPath.NotSure);
        await vm.NextAsync();
        Assert.True(vm.IsLastStep);
        Assert.Equal(AiSetupText.Finish(false), vm.NextText);
        Assert.False(vm.CanGoNext);

        var ask = h.Step(AiSetupStepId.AskAdmin);
        ask.IsComplete = true;
        Assert.True(vm.CanGoNext);
        Assert.True(await vm.NextAsync());
        Assert.Equal(1, ask.Finishes);
        Assert.Equal(1, ask.Leaves);
        Assert.True(ask.Disposed);
        Assert.Equal(1, finished);

        var saved = h.Settings.Current.Ai.Setup;
        Assert.Null(saved.LastPath);
        Assert.Null(saved.LastStep);
        Assert.Null(saved.LastProfileId);
        Assert.True(saved.FirstRunPromptClosed);
    }

    [Fact]
    public async Task Not_logged_in_reason_without_resume_starts_at_login()
    {
        var h = new Harness();
        using var vm = h.Create(AiSetupErrorKind.NotLoggedIn);
        await vm.StartAsync();
        Assert.Equal(AiSetupStepId.Login, vm.CurrentStep.StepId);
        Assert.Equal(AiSetupPath.ClaudeAccount, h.Context.ChosenPath);
    }

    [Fact]
    public async Task English_context_uses_english_titles()
    {
        var h = new Harness();
        var context = new AiSetupContext(h.Settings) { Text = new Oranda.App.Shell.ShellText(language: "en") };
        using var vm = new AiSetupWizardViewModel(context, h.Factories.Values);
        await vm.StartAsync();
        Assert.Equal("Choose a way", vm.Rail[0].Title);
        Assert.Equal(AiSetupText.Next(true), vm.NextText);
        Assert.StartsWith("Step 1 of 1", vm.ProgressText, StringComparison.Ordinal);
    }
}
