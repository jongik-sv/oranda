using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Choose;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Install;
using Oranda.App.Features.AiSetup.Login;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Wizard;

/// <summary>
/// U5 렌더 점검: 창 틀 + 방법 고르기·설치·로그인의 상태마다 × 한/영 × 밝음/어둠 × 창 최소 크기.
/// 글 잘림·창 밖으로 나감·단추 넘침이 없고, 운영 빨강(<c>OrandaEnvProductionBrush</c>)을 쓰지 않는다.
/// 영어 차례는 정적 글 번역기(<c>LiteralTranslator</c>)를 바꾸므로 언어 시험 묶음(<c>UiLanguage</c>, 병렬 끔)에서 돈다 — 다른 시험이 영어 글을 보지 않게.
/// </summary>
[Collection("UiLanguage")]
public sealed class AiSetupRenderTests
{
    public static TheoryData<string, string> Variants => new()
    {
        { "ko", "Light" },
        { "ko", "Dark" },
        { "en", "Light" },
        { "en", "Dark" },
    };

    private sealed class Rig
    {
        public required AiSetupWindow Window { get; init; }

        public required AiSetupContext Context { get; init; }

        public required FakeClaude Claude { get; init; }

        public AiSetupWizardViewModel Vm => Window.ViewModel;
    }

    private static Rig Make(string language, string theme, FakeClaude claude, Func<AppSettings, AppSettings>? change = null)
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        if (change is not null)
        {
            settings.UpdateAsync(change).GetAwaiter().GetResult();
        }

        var context = new AiSetupContext(settings) { Claude = claude, Text = new ShellText(language: language), Platform = Oranda.Core.Ai.Profiles.AiHostPlatform.Windows };
        IAiSetupStepFactory[] real =
        [
            new ChooseStepFactory(),
            new InstallStepFactory(),
            new LoginFactory(),
        ];
        var factories = FakeStepFactory.All(AiSetupStepId.Choose, AiSetupStepId.Install, AiSetupStepId.Login).Values.Cast<IAiSetupStepFactory>().Concat(real);
        var window = new AiSetupWindow(new AiSetupWizardViewModel(context, factories))
        {
            Width = AiSetupWindow.MinimumWidth,
            Height = AiSetupWindow.MinimumHeight,
            RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        return new Rig { Window = window, Context = context, Claude = claude };
    }

    /// <summary>로그인 단계는 시험 시계 없이 멈춰 있게(확인 주기가 돌지 않게) 만든다.</summary>
    private sealed class LoginFactory : IAiSetupStepFactory
    {
        public AiSetupStepId StepId => AiSetupStepId.Login;

        public IAiSetupStep Create(AiSetupContext context) => new LoginStep(context, (_, ct) => Task.Delay(Timeout.Infinite, ct));
    }

    private static void Check(Rig rig, string state)
    {
        HeadlessHost.Settle(rig.Window, 6);
        var top = rig.Window;
        var problems = TextClipping.Find(top).ToList();
        foreach (var button in top.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
        {
            if (IsInsideScroll(button))
            {
                continue;
            }

            var origin = button.TranslatePoint(new Point(0, 0), top);
            var far = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), top);
            if (origin is { } o && far is { } f && (o.X < -0.75 || f.X > top.Bounds.Width + 0.75 || f.Y > top.Bounds.Height + 0.75))
            {
                problems.Add($"단추 '{button.Content}' 가 창 밖으로 나감 ({o.X:0},{o.Y:0})-({f.X:0},{f.Y:0})");
            }
        }

        // 가운데 단계 화면은 가로로 넘치면 안 된다(가로 스크롤 없음).
        var host = rig.Window.StepHost;
        if (host.Content is Control view && view.Bounds.Width > host.Bounds.Width + 0.75)
        {
            problems.Add($"단계 화면 폭 {view.Bounds.Width:0} > 자리 {host.Bounds.Width:0}");
        }

        var production = top.TryFindResource("OrandaEnvProductionBrush", top.ActualThemeVariant, out var p) && p is ISolidColorBrush ps ? ps.Color : (Color?)null;
        if (production is { } red)
        {
            foreach (var v in top.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible))
            {
                IBrush?[] brushes = v switch
                {
                    TextBlock t => [t.Foreground],
                    Border b => [b.Background, b.BorderBrush],
                    _ => [],
                };
                if (brushes.OfType<ISolidColorBrush>().Any(b => b.Color == red))
                {
                    problems.Add($"운영 빨강을 씀: {v.GetType().Name}");
                }
            }
        }

        Assert.True(problems.Count == 0, $"[{state}] " + string.Join(Environment.NewLine, problems));
    }

    private static bool IsInsideScroll(Visual v) => v.GetVisualAncestors().OfType<ScrollViewer>().Any();

    private static async Task GoToAsync(Rig rig, AiSetupStepId target)
    {
        await rig.Vm.Idle;
        var choose = Assert.IsType<ChooseStep>(rig.Vm.CurrentStep);
        choose.Select(AiSetupPath.ClaudeAccount);
        await rig.Vm.NextAsync();
        if (target == AiSetupStepId.Login)
        {
            if (!rig.Vm.CurrentStep.IsComplete)
            {
                rig.Claude.Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330");
                await ((InstallStep)rig.Vm.CurrentStep).RecheckAsync();
            }

            await rig.Vm.NextAsync();
        }

        await rig.Vm.Idle;
    }

    private static Task Run(string language, string theme, FakeClaude claude, Func<Rig, Task> body, Func<AppSettings, AppSettings>? change = null) => HeadlessHost.Run(async () =>
    {
        if (language == "en")
        {
            LiteralTranslator.Install(new ShellText(language: "en"));
        }

        var rig = Make(language, theme, claude, change);
        rig.Window.Show();
        try
        {
            await body(rig);
        }
        finally
        {
            var closed = false;
            rig.Window.Closed += (_, _) => closed = true;
            rig.Window.Close();
            rig.Vm.AnswerLeave(true);
            await HeadlessHost.WaitUntil(() => closed);
            LiteralTranslator.Install(new ShellText(language: "ko"));
        }
    });

    [Theory]
    [MemberData(nameof(Variants))]
    public Task Choose_states_render(string language, string theme) =>
        Run(language, theme, new FakeClaude { Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330") }, async rig =>
        {
            await rig.Vm.Idle;
            rig.Claude.LastStatus = new ClaudeLoginStatus(true, null, StatusSupported: true);
            await rig.Vm.CurrentStep.EnterAsync(CancellationToken.None);
            await ((ChooseStep)rig.Vm.CurrentStep).Refreshing;
            Assert.True(((ChooseStep)rig.Vm.CurrentStep).AlreadyConnected);
            Check(rig, "고르기 · 설치됨 · 이미 연결됨");
            ((ChooseStep)rig.Vm.CurrentStep).Select(AiSetupPath.ClaudeAccount);
            Check(rig, "고르기 · 고름");
        });

    [Theory]
    [MemberData(nameof(Variants))]
    public Task Choose_read_only_renders(string language, string theme) =>
        Run(language, theme, new FakeClaude(), async rig =>
        {
            await rig.Vm.Idle;
            Assert.True(((ChooseStep)rig.Vm.CurrentStep).Blocked);
            Check(rig, "고르기 · 조회만");
        }, s => s with { Features = s.Features with { Roles = [RoleIds.Viewer] } });

    [Theory]
    [MemberData(nameof(Variants))]
    public Task Install_states_render(string language, string theme) =>
        Run(language, theme, new FakeClaude { InstallGate = new TaskCompletionSource(), InstallFails = AiSetupErrorKind.Network }, async rig =>
        {
            await GoToAsync(rig, AiSetupStepId.Install);
            var install = Assert.IsType<InstallStep>(rig.Vm.CurrentStep);
            Check(rig, "설치 · 없음");

            var running = install.InstallAsync();
            HeadlessHost.Pump(4);
            Check(rig, "설치 · 하는 중");

            var back = rig.Vm.BackAsync();
            Check(rig, "설치 · 그만둘까요");
            rig.Vm.AnswerLeave(false);
            await back;

            rig.Claude.InstallGate!.SetResult();
            await running;
            Assert.Equal(InstallState.Failed, install.State);
            Check(rig, "설치 · 실패 · 직접 설치");
        });

    [Theory]
    [MemberData(nameof(Variants))]
    public Task Install_done_renders(string language, string theme) =>
        Run(language, theme, new FakeClaude { Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330") }, async rig =>
        {
            await GoToAsync(rig, AiSetupStepId.Install);
            Assert.True(rig.Vm.CurrentStep.IsComplete);
            Check(rig, "설치 · 됨");
        });

    [Theory]
    [MemberData(nameof(Variants))]
    public Task Login_states_render(string language, string theme) =>
        Run(language, theme, new FakeClaude { Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330"), LoginUrl = "https://login.example.com/oauth/authorize?code=true&client_id=00000000-0000-0000-0000-000000000000&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A54545%2Fcallback" }, async rig =>
        {
            await GoToAsync(rig, AiSetupStepId.Login);
            var login = Assert.IsType<LoginStep>(rig.Vm.CurrentStep);
            Check(rig, "로그인 · 안 함");

            await login.StartAsync();
            foreach (var expander in rig.Window.GetVisualDescendants().OfType<Expander>().Where(e => e.IsEffectivelyVisible))
            {
                expander.IsExpanded = true;
            }

            Check(rig, "로그인 · 기다림 · 주소");

            rig.Claude.LastStatus = new ClaudeLoginStatus(true, "someone.with.a.long.name@example.com", StatusSupported: true);
            await login.ConfirmDoneAsync();
            await rig.Vm.Idle;
            Assert.Equal(AiSetupStepId.Test, rig.Vm.CurrentStep.StepId);
            await rig.Vm.BackAsync();
            Check(rig, "로그인 · 됨");
        });

    [Theory]
    [MemberData(nameof(Variants))]
    public Task Login_failure_renders(string language, string theme) =>
        Run(language, theme, new FakeClaude { Installed = new ClaudeInstallation("/usr/bin/claude", "2.1.330"), LoginStartFails = AiSetupErrorKind.Network }, async rig =>
        {
            await GoToAsync(rig, AiSetupStepId.Login);
            await ((LoginStep)rig.Vm.CurrentStep).StartAsync();
            Check(rig, "로그인 · 실패");
        });
}
