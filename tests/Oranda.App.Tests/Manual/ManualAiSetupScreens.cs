using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Features.AiSetup.AskAdmin;
using Oranda.App.Features.AiSetup.Choose;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Install;
using Oranda.App.Features.AiSetup.Login;
using Oranda.App.Features.AiSetup.Server;
using Oranda.App.Features.AiSetup.Test;
using Oranda.App.Features.AiSetup.TryIt;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Ai;
using Oranda.App.Tests.Features.AiSetup.Access;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Connection;
using Oranda.Testing;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 「AI 연결 도우미」 쪽 화면 캡처 생성기(<c>ai-setup-*.png</c>). 가짜 claude 준비 서비스만 쓰고 claude·인터넷을 부르지 않는다.
/// 번호의 뜻은 <c>site/src/content/docs/ai/setup.md</c> 의 번호별 설명과 같아야 한다. 갈래 B·C(회사 AI 서버·접속·해 보기·담당자에게 묻기)는
/// 가짜 접속 목록·프로필 조회·편집기 동작으로 그린다(DB·서버·AI 를 부르지 않는다).
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualAiSetupScreens
{
    private const double Width = 760;
    private const double Height = 560;

    /// <summary>연결 시험만 실패로 돌려주는 가짜(나머지는 <see cref="FakeClaude"/> 그대로).</summary>
    private sealed class FailingTest(FakeClaude inner, AiSetupErrorKind kind) : IClaudeSetupService
    {
        public Task<ClaudeInstallation?> FindAsync(CancellationToken ct) => inner.FindAsync(ct);

        public Task<ClaudeInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct) => inner.InstallAsync(progress, ct);

        public string ManualInstallCommand(AiHostPlatform platform) => inner.ManualInstallCommand(platform);

        public Task<ClaudeLoginStart> StartLoginAsync(IProgress<string>? browserUrl, CancellationToken ct) => inner.StartLoginAsync(browserUrl, ct);

        public Task<ClaudeLoginStatus> GetLoginStatusAsync(CancellationToken ct) => inner.GetLoginStatusAsync(ct);

        public Task<AiSetupTestResult> TestConnectionAsync(CancellationToken ct) => Task.FromResult(new AiSetupTestResult(false, TimeSpan.FromSeconds(60), kind));
    }

    /// <summary>로그인 단계: 상태 확인 주기를 멈춰 둔다(화면이 그대로 있게).</summary>
    private sealed class StillLoginFactory : IAiSetupStepFactory
    {
        public AiSetupStepId StepId => AiSetupStepId.Login;

        public IAiSetupStep Create(AiSetupContext context) => new LoginStep(context, (_, ct) => Task.Delay(Timeout.Infinite, ct));
    }

    private static AiSetupWindow Open(IClaudeSetupService claude)
    {
        var context = new AiSetupContext(new SettingsService(new InMemorySettingsStore()))
        {
            Claude = claude,
            Text = new ShellText(language: "ko"),
            Platform = AiHostPlatform.Windows,
            AppVersion = "0.1.5",
            CopyText = _ => Task.CompletedTask,
        };
        IAiSetupStepFactory[] real = [new ChooseStepFactory(), new InstallStepFactory(), new StillLoginFactory(), new AiSetupTestStepFactory()];
        var factories = FakeStepFactory.All(AiSetupStepId.Choose, AiSetupStepId.Install, AiSetupStepId.Login, AiSetupStepId.Test).Values.Cast<IAiSetupStepFactory>().Concat(real);
        var window = new AiSetupWindow(new AiSetupWizardViewModel(context, factories)) { Width = Width, Height = Height };
        window.Show();
        HeadlessHost.Settle(window, 6);
        return window;
    }

    private static async Task CloseAsync(AiSetupWindow window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        window.ViewModel.AnswerLeave(true);
        await HeadlessHost.WaitUntil(() => closed);
    }

    private static Border StatusBox(Control root) => ManualCapture.Find<Border>(root, b => b.Classes.Contains(AiSetupUi.StatusClass) && b.IsEffectivelyVisible);

    /// <summary>방법 고르기에서 갈래 A 를 골라 <paramref name="target"/> 단계까지 간다(설치됨·로그인됨이면 저절로 끝남).</summary>
    private static async Task GoToAsync(AiSetupWindow window, AiSetupStepId target)
    {
        var vm = window.ViewModel;
        await vm.Idle;
        ((ChooseStep)vm.CurrentStep).Select(AiSetupPath.ClaudeAccount);
        while (vm.CurrentStep.StepId != target)
        {
            await vm.NextAsync();
            await vm.Idle;
        }

        HeadlessHost.Settle(window, 6);
    }

    [Fact]
    public Task Setup_choose() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var window = Open(new FakeClaude());
        try
        {
            await window.ViewModel.Idle;
            var choose = (ChooseStep)window.ViewModel.CurrentStep;
            await choose.Refreshing;
            choose.Select(AiSetupPath.ClaudeAccount);
            HeadlessHost.Settle(window, 6);

            ManualCapture.Save(window, "ai-setup-choose",
            [
                new(window.Rail, 1, 120, 34),
                new(choose.Cards[AiSetupPath.ClaudeAccount], 2, 8, 8),
                new(choose.Cards[AiSetupPath.CompanyServer], 3, 8, 8),
                new(choose.Cards[AiSetupPath.NotSure], 4, 8, 8),
                new(window.NextButton, 5),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task Setup_install_failed() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var claude = new FakeClaude { InstallFails = AiSetupErrorKind.Network };
        claude.InstallLines.Clear();
        claude.InstallLines.AddRange(["Setting up Claude Code...", "Failed to download binary: Unable to connect to the remote server"]);
        var window = Open(claude);
        try
        {
            await GoToAsync(window, AiSetupStepId.Install);
            var install = (InstallStep)window.ViewModel.CurrentStep;
            await install.InstallAsync();
            HeadlessHost.Settle(window, 6);
            Assert.Equal(InstallState.Failed, install.State);

            ManualCapture.Save(window, "ai-setup-install-failed",
            [
                new(StatusBox(window.StepHost), 1, 8, 8),
                new(ManualCapture.FindByText(window.StepHost, ClaudeInstallCommands.Windows), 2, 230),
                new(ManualCapture.FindByText(window.StepHost, "복사"), 3),
                new(install.RecheckButton!, 4),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task Setup_login() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var claude = new FakeClaude { Installed = new ClaudeInstallation(@"C:\Users\user\.local\bin\claude.exe", "2.1.330"), LoginUrl = "https://claude.ai/oauth/authorize?…" };
        var window = Open(claude);
        try
        {
            await GoToAsync(window, AiSetupStepId.Login);
            var login = (LoginStep)window.ViewModel.CurrentStep;
            await login.StartAsync();
            HeadlessHost.Settle(window, 6);
            Assert.Equal(LoginState.Waiting, login.State);

            ManualCapture.Save(window, "ai-setup-login",
            [
                new(login.StartButton!, 1),
                new(StatusBox(window.StepHost), 2, 8, 8),
                new(ManualCapture.FindByText(window.StepHost, "인터넷 창이 안 열려요"), 3),
                new(login.DoneButton!, 4),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task Setup_test_failed() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var inner = new FakeClaude
        {
            Installed = new ClaudeInstallation(@"C:\Users\user\.local\bin\claude.exe", "2.1.330"),
            LastStatus = new ClaudeLoginStatus(true, "user@example.com", StatusSupported: true),
        };
        var window = Open(new FailingTest(inner, AiSetupErrorKind.Network));
        try
        {
            await GoToAsync(window, AiSetupStepId.Test);
            var test = (AiSetupTestStep)window.ViewModel.CurrentStep;
            await HeadlessHost.WaitUntil(() => test.Model.State == AiSetupTestState.Failed);
            HeadlessHost.Settle(window, 6);

            ManualCapture.Save(window, "ai-setup-test-failed",
            [
                new(test.StatusBox, 1, 8, 8),
                new(test.RetryButton, 2),
                new(test.CopyButton, 3),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    private const string ExampleFile = "{ \"format\": \"oranda-ai\", \"version\": 1, \"name\": \"사내 AI\", \"baseUrl\": \"https://ai.example.com/v1\", \"apiFormat\": \"openai\", \"model\": \"big-model\" }";

    /// <summary>해 보기 단계의 가짜 편집기·AI(연습 탭을 열었다고 하고, 정해 둔 답을 준다).</summary>
    private sealed class ExampleTryIt : ITryItActions
    {
        public Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql) => Guid.NewGuid();

        public Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct) =>
            Task.FromResult(new TryItExplainResult(true, "DUAL 은 Oracle 이 늘 한 행만 돌려주는 시험용 표입니다. 이 문장은 그 한 행(열 DUMMY = 'X')을 그대로 읽습니다."));

        public Task OpenExternalAgentAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ExampleTryItFactory : IAiSetupStepFactory
    {
        public AiSetupStepId StepId => AiSetupStepId.TryIt;

        public IAiSetupStep Create(AiSetupContext context) => new TryItStep(context, new ExampleTryIt());
    }

    private static ConnectionProfile Connection(string name, ConnectionEnvironment environment) =>
        new(name, "app", "db.example:1521/SAMPLE", Id: Guid.NewGuid(), Environment: environment);

    /// <summary>방법 고르기를 가짜로 두고 <paramref name="path"/> 를 고른 뒤 다음 단계로 간 창.</summary>
    private static async Task<(AiSetupWindow Window, AiSetupContext Context)> OpenPathAsync(AiSetupPath path, double height)
    {
        var profiles = await ProfileHarness.CreateAsync();
        var connections = new FakeConnections();
        connections.Profiles.AddRange([Connection("dev-erp", ConnectionEnvironment.Development), Connection("test-erp", ConnectionEnvironment.Test), Connection("prd-erp", ConnectionEnvironment.Production)]);
        var context = new AiSetupContext(profiles.Settings)
        {
            Text = new ShellText(language: "ko"),
            Connections = connections,
            Probe = profiles.Probe,
            Credentials = profiles.Credentials,
            Bus = profiles.Bus,
            Platform = AiHostPlatform.Windows,
            AppVersion = "0.1.5",
            CopyText = _ => Task.CompletedTask,
        };
        var choose = new FakeStepFactory(AiSetupStepId.Choose);
        IAiSetupStepFactory[] factories =
        [
            choose, new ServerStepFactory(), new AiSetupTestStepFactory(), new AccessStepFactory(), new ExampleTryItFactory(), new AskAdminStepFactory(),
            new FakeStepFactory(AiSetupStepId.Install), new FakeStepFactory(AiSetupStepId.Login),
        ];
        var window = new AiSetupWindow(new AiSetupWizardViewModel(context, factories)) { Width = Width, Height = height };
        window.Show();
        HeadlessHost.Settle(window, 6);
        await window.ViewModel.Idle;
        context.ChosenPath = path;
        choose.Last.IsComplete = true;
        await window.ViewModel.NextAsync();
        await window.ViewModel.Idle;
        HeadlessHost.Settle(window, 6);
        return (window, context);
    }

    private static async Task NextAsync(AiSetupWindow window)
    {
        await HeadlessHost.WaitUntil(() => window.ViewModel.CanGoNext);
        await window.ViewModel.NextAsync();
        await window.ViewModel.Idle;
        HeadlessHost.Settle(window, 6);
    }

    private static T Named<T>(Control root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    [Fact]
    public Task Setup_server() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (window, _) = await OpenPathAsync(AiSetupPath.CompanyServer, Height);
        try
        {
            var server = (ServerStep)window.ViewModel.CurrentStep;
            server.ViewModel.LoadFileText(ExampleFile);
            HeadlessHost.Settle(window, 6);

            ManualCapture.Save(window, "ai-setup-server",
            [
                new(Named<Control>(window.StepHost, "ServerDropZone"), 1, 8, 8),
                new(Named<Control>(window.StepHost, "ServerFileSummary"), 2, 230, 14),
                new(Named<Control>(window.StepHost, "ServerAddressOnly"), 3),
                new(Named<Control>(window.StepHost, "ServerKey"), 4, 500, 12),
                new(Named<Control>(window.StepHost, "ServerConnect"), 5),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task Setup_access() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (window, _) = await OpenPathAsync(AiSetupPath.CompanyServer, Height);
        try
        {
            var server = (ServerStep)window.ViewModel.CurrentStep;
            server.ViewModel.LoadFileText(ExampleFile);
            await server.ViewModel.ConnectAsync();
            await NextAsync(window); // 연결 시험(가짜 조회 = 성공)
            await NextAsync(window); // AI 를 쓸 접속
            Assert.IsType<AccessStep>(window.ViewModel.CurrentStep);

            // 목록 줄은 내용을 옮겨 붙이면 새로 만들어지므로 겹침판을 먼저 얹고 줄을 찾는다.
            var overlay = ManualCapture.AddOverlay(window);
            var rows = window.StepHost.GetVisualDescendants().OfType<Border>().ToList();
            ManualCapture.SaveOverlaid(window, overlay, "ai-setup-access",
            [
                new(rows.First(b => b.Classes.Contains("ai-setup-connection")), 1, 8, 8),
                new(rows.First(b => b.Classes.Contains("ai-setup-production")), 2, 8, 8),
                new(ManualCapture.Find<ToggleSwitch>(rows.First(b => b.Classes.Contains("ai-setup-production"))), 3),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task Setup_try_it() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (window, _) = await OpenPathAsync(AiSetupPath.CompanyServer, 720);
        try
        {
            var server = (ServerStep)window.ViewModel.CurrentStep;
            server.ViewModel.LoadFileText(ExampleFile);
            await server.ViewModel.ConnectAsync();
            await NextAsync(window);
            await NextAsync(window);
            await NextAsync(window); // 해 보기
            var tryIt = (TryItStep)window.ViewModel.CurrentStep;
            await tryIt.ViewModel.ExplainAsync();
            HeadlessHost.Settle(window, 6);

            ManualCapture.Save(window, "ai-setup-tryit",
            [
                new(Named<Control>(window.StepHost, "TryItConnection"), 1),
                new(Named<Control>(window.StepHost, "TryItOpenTab"), 2),
                new(Named<Control>(window.StepHost, "TryItExplain"), 3),
                new(Named<Control>(window.StepHost, "TryItAnswer"), 4, 500, 26),
                new(Named<Control>(window.StepHost, "TryItTips"), 5),
                new(Named<Control>(window.StepHost, "TryItAlsoAgent"), 6),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task Setup_ask_admin() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (window, _) = await OpenPathAsync(AiSetupPath.NotSure, Height);
        try
        {
            Assert.IsType<AskAdminStep>(window.ViewModel.CurrentStep);

            ManualCapture.Save(window, "ai-setup-askadmin",
            [
                new(Named<Control>(window.StepHost, "AskAdminMessage"), 1, 500, 96),
                new(Named<Control>(window.StepHost, "AskAdminCopy"), 2),
            ]);
        }
        finally
        {
            await CloseAsync(window);
        }
    });
}
