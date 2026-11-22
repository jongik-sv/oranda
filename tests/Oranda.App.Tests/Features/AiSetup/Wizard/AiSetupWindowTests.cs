using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Setup;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Wizard;

/// <summary>U1 창 틀(Headless): 단추·목록·단계 화면, 닫기 = 이어 하기 저장, 런처 두 번 = 창 하나.</summary>
public sealed class AiSetupWindowTests
{
    private static (AiSetupWindow Window, Dictionary<AiSetupStepId, FakeStepFactory> Factories, AiSetupContext Context, SettingsService Settings) Make()
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        var factories = FakeStepFactory.All(AiSetupStepId.Login);
        var context = new AiSetupContext(settings) { Text = new Oranda.App.Shell.ShellText(language: "ko") };
        return (new AiSetupWindow(new AiSetupWizardViewModel(context, factories.Values)), factories, context, settings);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public Task Buttons_rail_and_step_view_follow_the_flow() => HeadlessHost.Run(async () =>
    {
        var (window, factories, context, _) = Make();
        window.Show();
        try
        {
            HeadlessHost.Settle(window);
            Assert.Equal(AiSetupIds.WindowName, window.Name);
            Assert.False(window.NextButton.IsEnabled);
            Assert.False(window.BackButton.IsEnabled);
            Assert.Same(factories[AiSetupStepId.Choose].Last.View, window.StepHost.Content);

            context.ChosenPath = AiSetupPath.ClaudeAccount;
            factories[AiSetupStepId.Choose].Last.IsComplete = true;
            HeadlessHost.Settle(window);
            Assert.True(window.NextButton.IsEnabled);
            Assert.Equal(6, window.Rail.ItemCount);

            Click(window.NextButton);
            await HeadlessHost.WaitUntil(() => window.ViewModel.CurrentStep.StepId == AiSetupStepId.Install);
            factories[AiSetupStepId.Install].Last.IsComplete = true;
            Click(window.NextButton);
            await HeadlessHost.WaitUntil(() => window.ViewModel.CurrentStep.StepId == AiSetupStepId.Login);
            HeadlessHost.Settle(window);

            // 로그인 팩터리가 없으니 「준비 중」 자리 표시.
            var texts = window.StepHost.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(AiSetupWizardText.PlaceholderHeadline(false), texts);
            Assert.True(window.NextButton.IsEnabled);
            Assert.Contains(window.Rail.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains(AiSetupWindow.DoneClass));

            Click(window.BackButton);
            await HeadlessHost.WaitUntil(() => window.ViewModel.CurrentStep.StepId == AiSetupStepId.Install);
        }
        finally
        {
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            await HeadlessHost.WaitUntil(() => closed);
        }
    });

    [Fact]
    public Task Closing_the_window_saves_resume_values() => HeadlessHost.Run(async () =>
    {
        var (window, factories, context, settings) = Make();
        window.Show();
        HeadlessHost.Settle(window);
        context.ChosenPath = AiSetupPath.CompanyServer;
        factories[AiSetupStepId.Choose].Last.IsComplete = true;
        Click(window.NextButton);
        await HeadlessHost.WaitUntil(() => window.ViewModel.CurrentStep.StepId == AiSetupStepId.Server);

        var closed = false;
        window.Closed += (_, _) => closed = true;
        Click(window.CloseButton);
        await HeadlessHost.WaitUntil(() => closed);
        Assert.Equal(AiSetupPath.CompanyServer, settings.Current.Ai.Setup.LastPath);
        Assert.Equal(AiSetupStepId.Server, settings.Current.Ai.Setup.LastStep);
        Assert.True(factories[AiSetupStepId.Server].Last.Disposed);
    });

    [Fact]
    public Task Launcher_opened_twice_keeps_one_window() => HeadlessHost.Run(async () =>
    {
        var owner = new Window();
        owner.Show();
        var made = 0;
        var launcher = new AiSetupLauncher(() => owner, (_, _) =>
        {
            made++;
            return Make().Window;
        });
        try
        {
            var first = launcher.OpenAsync(AiSetupEntry.Menu, null, CancellationToken.None);
            HeadlessHost.Pump(2);
            var second = launcher.OpenAsync(AiSetupEntry.FailureBand, AiSetupErrorKind.NotLoggedIn, CancellationToken.None);
            Assert.Equal(1, made);
            Assert.Equal(AiSetupEntry.FailureBand, launcher.LastEntry);
            Assert.NotNull(launcher.OpenWindow);

            launcher.OpenWindow!.Close();
            await HeadlessHost.WaitUntil(() => first.IsCompleted && second.IsCompleted);
            Assert.Null(launcher.OpenWindow);

            var third = launcher.OpenAsync(AiSetupEntry.Menu, null, CancellationToken.None);
            Assert.Equal(2, made);
            launcher.OpenWindow!.Close();
            await HeadlessHost.WaitUntil(() => third.IsCompleted);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task Launcher_without_owner_does_nothing() => HeadlessHost.Run(async () =>
    {
        var made = 0;
        var launcher = new AiSetupLauncher(() => null, (_, _) =>
        {
            made++;
            return null;
        });
        await launcher.OpenAsync(AiSetupEntry.Menu, null, CancellationToken.None);
        Assert.Equal(0, made);
    });
}
