using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Test;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Tests.Features.AiSetup.Test;

/// <summary>
/// 연결 시험 단계 화면(Headless): 창 틀 안 흐름(들어오면 저절로 시험 → [다음] 켜짐, 실패 → 다시 시험), 렌더 점검(성공·실패·도는 중 × 한/영 × 밝음/어둠, 글 잘림·겹침 없음).
/// <c>ORANDA_RENDER_DIR</c> 가 있을 때만 PNG(<c>ai-setup-test-*.png</c>)를 쓴다.
/// </summary>
public sealed class AiSetupTestStepScreenTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public Task Entering_the_step_in_the_wizard_runs_the_test_and_enables_next() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaudeSetup();
        var context = AiSetupTestRig.ClaudeContext(claude);
        context.ChosenPath = null; // 창 틀은 방법 고르기에서 갈래를 받는다(아래에서 고름)
        var factories = FakeStepFactory.All(AiSetupStepId.Test);
        foreach (var factory in factories.Values)
        {
            factory.OnCreate = step => step.CompleteOnEnter = true;
        }

        var window = new AiSetupWindow(new AiSetupWizardViewModel(context, [.. factories.Values, new AiSetupTestStepFactory()]));
        window.Show();
        try
        {
            HeadlessHost.Settle(window);
            context.ChosenPath = AiSetupPath.ClaudeAccount;
            factories[AiSetupStepId.Choose].Last.IsComplete = true;
            foreach (var target in new[] { AiSetupStepId.Install, AiSetupStepId.Login, AiSetupStepId.Test })
            {
                await HeadlessHost.WaitUntil(() => window.NextButton.IsEnabled);
                Click(window.NextButton);
                await HeadlessHost.WaitUntil(() => window.ViewModel.CurrentStep.StepId == target);
            }

            var step = Assert.IsType<AiSetupTestStep>(window.ViewModel.CurrentStep);
            await HeadlessHost.WaitUntil(() => step.IsComplete);
            HeadlessHost.Settle(window);

            Assert.Equal(1, claude.Tests);
            Assert.True(window.NextButton.IsEnabled);
            Assert.Same(step.View, window.StepHost.Content);
            Assert.False(step.RetryButton.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_failure_shows_retry_and_copy_and_retry_can_succeed() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaudeSetup();
        claude.Results.Enqueue(new AiSetupTestResult(false, TimeSpan.FromSeconds(1), AiSetupErrorKind.NotLoggedIn));
        claude.Results.Enqueue(new AiSetupTestResult(true, TimeSpan.FromSeconds(2)));
        string? copied = null;
        var context = new AiSetupContext(AiSetupTestRig.ClaudeContext(claude).Settings)
        {
            Text = new Oranda.App.Shell.ShellText(language: "ko"),
            Claude = claude,
            ChosenPath = AiSetupPath.ClaudeAccount,
            CopyText = text =>
            {
                copied = text;
                return Task.CompletedTask;
            },
        };
        var step = (AiSetupTestStep)new AiSetupTestStepFactory().Create(context);
        var window = Host(step.View, 560);
        try
        {
            await step.EnterAsync(CancellationToken.None);
            await HeadlessHost.WaitUntil(() => step.Model.State == AiSetupTestState.Failed);
            HeadlessHost.Settle(window);

            Assert.Contains(AiSetupErrorText.For(AiSetupErrorKind.NotLoggedIn, false), StatusTexts(step));
            Assert.True(step.RetryButton.IsVisible);
            Assert.True(step.CopyButton.IsVisible);

            Click(step.CopyButton);
            await HeadlessHost.WaitUntil(() => copied is not null);
            Assert.Contains("NotLoggedIn", copied, StringComparison.Ordinal);

            Click(step.RetryButton);
            await HeadlessHost.WaitUntil(() => step.IsComplete);
            HeadlessHost.Settle(window);
            Assert.False(step.RetryButton.IsVisible);
            Assert.Equal(2, claude.Tests);
        }
        finally
        {
            step.Dispose();
            window.Close();
        }
    });

    [Fact]
    public Task Coming_back_runs_the_test_again() => HeadlessHost.Run(async () =>
    {
        // 앞 단계(서버 주소·열쇠·계정)가 바뀌었을 수 있어 들어올 때마다 다시 시험한다.
        var claude = new FakeClaudeSetup();
        var step = (AiSetupTestStep)new AiSetupTestStepFactory().Create(AiSetupTestRig.ClaudeContext(claude));

        await step.EnterAsync(CancellationToken.None);
        await HeadlessHost.WaitUntil(() => step.IsComplete);
        await step.LeaveAsync(CancellationToken.None);
        await step.EnterAsync(CancellationToken.None);
        await HeadlessHost.WaitUntil(() => claude.Tests == 2 && step.IsComplete);

        step.Dispose();
    });

    [Fact]
    public Task Leaving_while_running_stops_the_test() => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaudeSetup { Gate = new TaskCompletionSource() };
        var step = (AiSetupTestStep)new AiSetupTestStepFactory().Create(AiSetupTestRig.ClaudeContext(claude));

        await step.EnterAsync(CancellationToken.None);
        await HeadlessHost.WaitUntil(() => step.Model.State == AiSetupTestState.Running);
        await step.LeaveAsync(CancellationToken.None);
        await HeadlessHost.WaitUntil(() => step.Model.State == AiSetupTestState.Idle);

        Assert.True(claude.Cancelled);
        step.Dispose();
    });

    public static TheoryData<string, bool, bool> RenderCases()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var state in new[] { "ok", "fail", "wait" })
        {
            foreach (var english in new[] { false, true })
            {
                foreach (var dark in new[] { false, true })
                {
                    data.Add(state, english, dark);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RenderCases))]
    public Task Each_state_renders_without_clipping_or_overlap(string state, bool english, bool dark) => HeadlessHost.Run(async () =>
    {
        var claude = new FakeClaudeSetup();
        if (state == "wait")
        {
            claude.Gate = new TaskCompletionSource();
        }
        else if (state == "fail")
        {
            claude.Results.Enqueue(new AiSetupTestResult(false, TimeSpan.FromSeconds(1), AiSetupErrorKind.Network));
        }
        else
        {
            claude.Results.Enqueue(new AiSetupTestResult(true, TimeSpan.FromMilliseconds(2800)));
        }

        var step = (AiSetupTestStep)new AiSetupTestStepFactory().Create(AiSetupTestRig.ClaudeContext(claude, english));
        // 창 틀의 가운데 칸 폭(좁은 쪽)에 맞춘다.
        var window = Host(step.View, 460, dark);
        try
        {
            await step.EnterAsync(CancellationToken.None);
            var expected = state switch { "ok" => AiSetupTestState.Succeeded, "fail" => AiSetupTestState.Failed, _ => AiSetupTestState.Running };
            await HeadlessHost.WaitUntil(() => step.Model.State == expected);
            HeadlessHost.Settle(window);

            AssertFits(window, step.View);
            Assert.Contains(step.Model.Headline, StatusTexts(step));
            Assert.False(string.IsNullOrWhiteSpace(step.Model.Headline));
            Render(window, $"ai-setup-test-{state}-{(english ? "en" : "ko")}-{(dark ? "dark" : "light")}");
        }
        finally
        {
            step.Dispose();
            window.Close();
        }
    });

    private static List<string?> StatusTexts(AiSetupTestStep step) =>
        step.StatusBox.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();

    private static Window Host(Control view, double width, bool dark = false)
    {
        var window = new Window
        {
            Width = width,
            Height = 560,
            Content = new Border { Padding = new Thickness(16), Child = view },
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        HeadlessHost.Settle(window);
        return window;
    }

    /// <summary>보이는 글·단추가 창 폭 안에 있고, 한 줄로 잘리지 않으며(줄바꿈 글은 폭 안), 같은 부모 안 형제끼리 겹치지 않는다.</summary>
    private static void AssertFits(Window window, Control view)
    {
        var limit = window.ClientSize.Width + 0.5;
        var visible = view.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0).ToList();
        foreach (var control in visible.Where(c => c is TextBlock or Button))
        {
            var box = control.TransformToVisual(window) is { } m ? new Rect(control.Bounds.Size).TransformToAABB(m) : default;
            Assert.True(box.Bottom <= window.ClientSize.Height + 0.5, $"{control.GetType().Name} '{(control as TextBlock)?.Text ?? (control as Button)?.Content}' 이 창 아래로 잘렸다({box.Bottom:0}).");
            Assert.True(box.Right <= limit, $"{control.GetType().Name} '{(control as TextBlock)?.Text ?? (control as Button)?.Content}' 이 창 밖으로 나갔다({box.Right:0} > {limit:0}).");
            if (control is TextBlock text && text.TextWrapping == Avalonia.Media.TextWrapping.NoWrap && text.Text is { Length: > 0 })
            {
                text.Measure(Size.Infinity);
                var needed = text.DesiredSize.Width - text.Margin.Left - text.Margin.Right; // DesiredSize 는 여백을 품는다
                Assert.True(needed <= text.Bounds.Width + 0.5, $"글 '{text.Text}' 이 잘렸다.");
            }
        }

        // 컨트롤 템플릿 안(진행 막대의 움직이는 막대 등)은 빼고 이 화면이 놓은 패널만 본다.
        foreach (var panel in visible.OfType<Panel>().Where(p => p.TemplatedParent is null))
        {
            var children = panel.Children.Where(c => c.IsVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0).ToList();
            for (var i = 0; i < children.Count; i++)
            {
                for (var j = i + 1; j < children.Count; j++)
                {
                    var a = children[i].Bounds.Deflate(0.5);
                    var b = children[j].Bounds.Deflate(0.5);
                    Assert.False(a.Intersects(b), $"{panel.GetType().Name} 안의 {children[i].GetType().Name} 와 {children[j].GetType().Name} 가 겹친다.");
                }
            }
        }
    }

    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.ClientSize.Width), (int)Math.Ceiling(window.ClientSize.Height)));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }
}
