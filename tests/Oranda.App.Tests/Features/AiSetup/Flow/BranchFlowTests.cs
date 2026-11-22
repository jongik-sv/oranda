using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Features.AiSetup.AskAdmin;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Server;
using Oranda.App.Features.AiSetup.TryIt;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.Ai;
using Oranda.App.Tests.Features.AiSetup.Access;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.AiSetup.Flow;

/// <summary>
/// B5: 실제 창 틀(U1)에 B1~B4 단계를 붙여 갈래 B(방법 고르기 → 회사 AI 서버 → 연결 시험 자리 → AI 를 쓸 접속 → 해 보기)와
/// 갈래 C(방법 고르기 → 담당자에게 묻기)를 처음부터 끝까지, 실패 경로(파일 거절·저장 실패) 한 바퀴, 창 전체 렌더(한/영·밝음/어둠·최소 폭).
/// 방법 고르기는 가짜 단계, 연결 시험은 팩터리가 없으면 창 틀의 자리 표시로 지나간다.
/// </summary>
public sealed class BranchFlowTests
{
    private const string GoodFile = "{ \"format\": \"oranda-ai\", \"version\": 1, \"name\": \"사내 AI\", \"baseUrl\": \"https://ai.example.com/v1\", \"apiFormat\": \"openai\", \"model\": \"big-model\" }";

    private static ConnectionProfile Profile(string name, ConnectionEnvironment environment, AiAccessLevel? level = null) =>
        new(name, "app", "db.example:1521/SAMPLE", Id: Guid.NewGuid(), Environment: environment, AiAccess: level);

    /// <summary>해 보기 단계를 가짜 편집기·런처로 만드는 팩터리(실제 팩터리는 앱 서비스로 만든다).</summary>
    private sealed class TryItWithFakes(FakeTryIt actions) : IAiSetupStepFactory
    {
        public AiSetupStepId StepId => AiSetupStepId.TryIt;

        public IAiSetupStep Create(AiSetupContext context) => new TryItStep(context, actions);
    }

    private sealed class FakeTryIt : ITryItActions
    {
        public int Explains { get; private set; }

        public int AgentOpens { get; private set; }

        public Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql) => Guid.NewGuid();

        public Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct)
        {
            Explains++;
            return Task.FromResult(new TryItExplainResult(true, "DUAL 은 한 행만 돌려주는 시험용 표입니다."));
        }

        public Task OpenExternalAgentAsync(CancellationToken ct)
        {
            AgentOpens++;
            return Task.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public required ProfileHarness Profiles { get; init; }

        public required AiSetupContext Context { get; init; }

        public required FakeStepFactory Choose { get; init; }

        public required FakeConnections Connections { get; init; }

        public required FakeTryIt TryIt { get; init; }

        public required ShellText Text { get; init; }

        public required List<string> Copied { get; init; }

        public required AiSetupWizardViewModel Wizard { get; init; }

        public static async Task<Rig> CreateAsync(string language = "ko", Func<ConnectionProfile, ConnectionProfile>? saveHook = null)
        {
            var profiles = await ProfileHarness.CreateAsync();
            var connections = new FakeConnections();
            connections.Profiles.AddRange([Profile("dev-erp", ConnectionEnvironment.Development), Profile("prd-erp", ConnectionEnvironment.Production)]);
            var text = new ShellText(language: language);
            var copied = new List<string>();
            var context = new AiSetupContext(profiles.Settings)
            {
                Text = text,
                Connections = connections,
                Probe = profiles.Probe,
                Credentials = profiles.Credentials,
                Bus = profiles.Bus,
                AppVersion = "0.1.5",
                CopyText = t =>
                {
                    copied.Add(t);
                    return Task.CompletedTask;
                },
            };
            var choose = new FakeStepFactory(AiSetupStepId.Choose);
            var tryIt = new FakeTryIt();
            IAiSetupStepFactory[] factories = [choose, new ServerStepFactory(), new AccessStepFactory(), new TryItWithFakes(tryIt), new AskAdminStepFactory()];
            var rig = new Rig
            {
                Profiles = profiles,
                Context = context,
                Choose = choose,
                Connections = connections,
                TryIt = tryIt,
                Text = text,
                Copied = copied,
                Wizard = new AiSetupWizardViewModel(context, factories),
            };
            return rig;
        }

        public async Task PickAsync(AiSetupPath path)
        {
            await Wizard.StartAsync();
            Context.ChosenPath = path;
            Choose.Last.IsComplete = true;
            await NextAsync();
        }

        public async Task NextAsync()
        {
            Assert.True(Wizard.CanGoNext, $"[다음]이 꺼져 있음: {Wizard.CurrentStep.StepId}");
            Assert.True(await Wizard.NextAsync());
            await Wizard.Idle;
        }

        public T Current<T>()
            where T : IAiSetupStep => Assert.IsType<T>(Wizard.CurrentStep);
    }

    [Fact]
    public Task BranchB_ServerToTryIt_EndToEnd() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        var finished = 0;
        rig.Wizard.Finished += (_, _) => finished++;
        await rig.PickAsync(AiSetupPath.CompanyServer);

        // 회사 AI 서버
        var server = rig.Current<ServerStep>();
        Assert.False(rig.Wizard.CanGoNext);
        server.ViewModel.LoadFileText(GoodFile);
        await server.ViewModel.ConnectAsync();
        Assert.True(rig.Wizard.CanGoNext);
        var profileId = rig.Context.CreatedProfileId;
        Assert.Equal(profileId, rig.Profiles.Settings.Current.Ai.ActiveProfileId);
        await rig.NextAsync();

        // 연결 시험: 이 레인 밖 단계 → 창 틀 자리 표시로 지나간다
        Assert.Equal(AiSetupStepId.Test, rig.Wizard.CurrentStep.StepId);
        await rig.NextAsync();

        // AI 를 쓸 접속: 들어오면 끝남, 운영을 끈다
        var access = rig.Current<AccessStep>();
        Assert.True(rig.Wizard.CanGoNext);
        access.ViewModel.Rows.Single(r => r.IsProduction).IsOn = false;
        await access.ViewModel.LastSave;
        Assert.Equal(AiAccessLevel.Off, rig.Connections.Profiles.Single(p => p.Name == "prd-erp").AiAccess);
        await rig.NextAsync();

        // 해 보기: 켠 개발 접속 하나로 설명 → 끝내기
        var tryIt = rig.Current<TryItStep>();
        Assert.Equal(["dev-erp"], tryIt.ViewModel.Connections.Select(c => c.Name));
        Assert.True(rig.Wizard.IsLastStep);
        Assert.False(rig.Wizard.CanGoNext);
        await tryIt.ViewModel.ExplainAsync();
        Assert.True(rig.Wizard.CanGoNext);
        tryIt.ViewModel.AlsoExternalAgent = true;
        await rig.NextAsync();

        Assert.Equal(1, finished);
        Assert.Equal(1, rig.TryIt.AgentOpens);
        Assert.Equal(profileId, rig.Profiles.Settings.Current.Ai.ActiveProfileId);
    });

    [Fact]
    public Task BranchB_AllConnectionsOff_SkipsTryIt() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        await rig.PickAsync(AiSetupPath.CompanyServer);
        var server = rig.Current<ServerStep>();
        server.ViewModel.LoadFileText(GoodFile);
        await server.ViewModel.ConnectAsync();
        await rig.NextAsync();
        await rig.NextAsync();

        var access = rig.Current<AccessStep>();
        foreach (var row in access.ViewModel.Rows)
        {
            row.IsOn = false;
            await access.ViewModel.LastSave;
        }

        Assert.True(access.ViewModel.ShowSkipNotice);
        await rig.NextAsync();

        var tryIt = rig.Current<TryItStep>();
        Assert.True(tryIt.ViewModel.IsSkipped);
        Assert.True(rig.Wizard.CanGoNext); // 건너뜀 = 끝남 → [끝내기]
        Assert.Equal(0, rig.TryIt.Explains);
    });

    [Fact]
    public Task BranchC_AskAdmin_EndToEnd() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync("en");
        var finished = 0;
        rig.Wizard.Finished += (_, _) => finished++;
        await rig.PickAsync(AiSetupPath.NotSure);

        var ask = rig.Current<AskAdminStep>();
        Assert.True(rig.Wizard.IsLastStep);
        Assert.False(rig.Wizard.CanGoNext);
        Assert.Contains("Hello", ask.ViewModel.Message);
        await ask.ViewModel.CopyAsync();
        Assert.Contains("0.1.5", Assert.Single(rig.Copied));
        await rig.NextAsync();

        Assert.Equal(1, finished);
    });

    [Fact]
    public Task FailurePaths_FileRefused_ThenSaveFailure_KeepNextOff() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        await rig.PickAsync(AiSetupPath.CompanyServer);
        var server = rig.Current<ServerStep>();

        server.ViewModel.LoadFileText(GoodFile.Replace("\"model\"", "\"apiKey\": \"sk-x\", \"model\"", StringComparison.Ordinal));
        Assert.NotEmpty(server.ViewModel.ErrorText);
        Assert.False(rig.Wizard.CanGoNext);

        rig.Profiles.Store.FailSave = true;
        server.ViewModel.LoadFileText(GoodFile);
        await server.ViewModel.ConnectAsync();
        Assert.Contains("저장하지 못했", server.ViewModel.ErrorText);
        Assert.False(rig.Wizard.CanGoNext);

        rig.Profiles.Store.FailSave = false;
        await server.ViewModel.ConnectAsync();
        Assert.True(rig.Wizard.CanGoNext, server.ViewModel.ErrorText);
    });

    [Fact]
    public Task FailurePath_AccessSaveFailure_KeepsNextOff() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        await rig.PickAsync(AiSetupPath.CompanyServer);
        var server = rig.Current<ServerStep>();
        server.ViewModel.LoadFileText(GoodFile);
        await server.ViewModel.ConnectAsync();
        await rig.NextAsync();
        await rig.NextAsync();
        var access = rig.Current<AccessStep>();

        rig.Connections.FailSave = true;
        access.ViewModel.Rows[0].IsOn = false;
        await access.ViewModel.LastSave;
        Assert.Contains("저장하지 못했어요", access.ViewModel.ErrorText);
        Assert.False(rig.Wizard.CanGoNext);

        rig.Connections.FailSave = false;
        access.ViewModel.Rows[0].IsOn = true;
        access.ViewModel.Rows[0].IsOn = false;
        await access.ViewModel.LastSave;
        Assert.True(rig.Wizard.CanGoNext);
    });

    [Theory]
    [InlineData("ko", "Light")]
    [InlineData("en", "Dark")]
    [InlineData("en", "Light")]
    [InlineData("ko", "Dark")]
    public Task Window_RendersEachStepAtMinimumWidth(string language, string theme) => HeadlessHost.Run(async () =>
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        app.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var rig = await Rig.CreateAsync(language);
        var window = new AiSetupWindow(rig.Wizard) { Width = AiSetupWindow.MinimumWidth, Height = 720 };
        window.Show();
        try
        {
            await rig.PickAsync(AiSetupPath.CompanyServer);
            Check(window, AiSetupStepId.Server);
            var server = rig.Current<ServerStep>();
            server.ViewModel.LoadFileText(GoodFile);
            await server.ViewModel.ConnectAsync();
            Check(window, AiSetupStepId.Server);
            await rig.NextAsync();
            await rig.NextAsync();
            Check(window, AiSetupStepId.Access);
            await rig.NextAsync();
            await rig.Current<TryItStep>().ViewModel.ExplainAsync();
            Check(window, AiSetupStepId.TryIt);
        }
        finally
        {
            window.Close();
            app.RequestedThemeVariant = before;
        }

        var c = await Rig.CreateAsync(language);
        var second = new AiSetupWindow(c.Wizard) { Width = AiSetupWindow.MinimumWidth, Height = 720 };
        second.Show();
        try
        {
            await c.PickAsync(AiSetupPath.NotSure);
            Check(second, AiSetupStepId.AskAdmin);
        }
        finally
        {
            second.Close();
        }
    });

    // 단계 화면 안의 보이는 글이 단계 자리 폭을 넘지 않는다(말줄임 글은 예외).
    private static void Check(AiSetupWindow window, AiSetupStepId id)
    {
        HeadlessHost.Settle(window);
        Assert.Equal(id, window.ViewModel.CurrentStep.StepId);
        var host = window.StepHost;
        var right = host.Bounds.Width + 0.5;
        foreach (var text in host.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            if (text.TranslatePoint(new Point(text.Bounds.Width, 0), host) is { } edge && text.TextTrimming == Avalonia.Media.TextTrimming.None)
            {
                Assert.True(edge.X <= right, $"{id}: '{text.Text}' 가 단계 자리 밖으로 나감({edge.X:0} > {right:0})");
            }
        }
    }
}
