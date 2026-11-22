using System.Reflection;
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
using Oranda.App.Shell;
using Oranda.App.Tests.Features.Ai;
using Oranda.App.Tests.Features.AiSetup.Access;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.AiSetup.Flow;

/// <summary>B1~B4 단계를 계약(IAiSetupStep·AiSetupContext)으로 붙였는지: 등록, 단계 사이 값, 끝남, 화면(한/영·밝음/어둠·좁은 폭).</summary>
public sealed class StepWiringTests
{
    private static readonly AiSetupStepId[] Mine = [AiSetupStepId.Server, AiSetupStepId.Access, AiSetupStepId.TryIt, AiSetupStepId.AskAdmin];

    private static ConnectionProfile Profile(string name, ConnectionEnvironment environment, AiAccessLevel? level = null) =>
        new(name, "app", "db.example:1521/SAMPLE", Id: Guid.NewGuid(), Environment: environment, AiAccess: level);

    private sealed class Rig
    {
        public required ProfileHarness Profiles { get; init; }

        public FakeConnections Connections { get; } = new();

        public List<string> Copied { get; } = [];

        public ShellText Text { get; } = new(language: "ko");

        public AiSetupContext Context { get; private set; } = null!;

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig { Profiles = await ProfileHarness.CreateAsync() };
            rig.Context = new AiSetupContext(rig.Profiles.Settings)
            {
                Text = rig.Text,
                Connections = rig.Connections,
                Probe = rig.Profiles.Probe,
                Credentials = rig.Profiles.Credentials,
                Bus = rig.Profiles.Bus,
                AppVersion = "0.1.5",
                CopyText = t =>
                {
                    rig.Copied.Add(t);
                    return Task.CompletedTask;
                },
            };
            return rig;
        }
    }

    private static IReadOnlyList<IAiSetupStepFactory> Factories() =>
        [.. typeof(AccessStep).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<OrandaServiceAttribute>().Any(a => a.ServiceType == typeof(IAiSetupStepFactory)))
            .Select(t => (IAiSetupStepFactory)Activator.CreateInstance(t, nonPublic: true)!)];

    [Fact]
    public void FourStepFactories_AreRegistered_WithOwnIds()
    {
        var factories = Factories();

        foreach (var id in Mine)
        {
            Assert.Single(factories, f => f.StepId == id);
        }

        Assert.Equal(factories.Count, factories.Select(f => f.StepId).Distinct().Count());
    }

    [Fact]
    public Task EachStep_HasTitleAndView_InBothLanguages() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        foreach (var factory in Factories().Where(f => Mine.Contains(f.StepId)))
        {
            using var step = factory.Create(rig.Context);
            Assert.Equal(factory.StepId, step.StepId);
            Assert.Equal(AiSetupText.StepTitle(step.StepId, english: false), step.Title);
            Assert.Same(step.View, step.View);
        }

        rig.Text.SetLanguage("en");
        using var english = Factories().Single(f => f.StepId == AiSetupStepId.Access).Create(rig.Context);
        Assert.Equal(AiSetupText.StepTitle(AiSetupStepId.Access, english: true), english.Title);
    });

    [Fact]
    public Task Access_PassesEnabledIds_AndSwitchSavesAtOnce() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        var dev = Profile("dev-erp", ConnectionEnvironment.Development);
        var prd = Profile("prd-erp", ConnectionEnvironment.Production);
        rig.Connections.Profiles.AddRange([prd, dev]);
        using var step = new AccessStep(rig.Context);
        var changes = 0;
        step.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(IAiSetupStep.IsComplete) ? 1 : 0;

        await step.EnterAsync(default);

        Assert.True(step.IsComplete);
        Assert.Equal([dev.Id, prd.Id], rig.Context.EnabledConnectionIds);
        step.ViewModel.Rows.Single(r => r.Name == "dev-erp").IsOn = false;
        await step.LeaveAsync(default);

        Assert.Equal(AiAccessLevel.Off, rig.Connections.Profiles.Single(p => p.Id == dev.Id).AiAccess);
        Assert.Equal([prd.Id], rig.Context.EnabledConnectionIds);
        Assert.True(step.IsComplete);
        Assert.True(changes >= 1);
    });

    [Fact]
    public Task Access_WithoutConnectionService_CompletesAsSkipped() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        var context = new AiSetupContext(rig.Profiles.Settings) { Text = rig.Text };
        using var step = new AccessStep(context);

        await step.EnterAsync(default);

        Assert.True(step.IsComplete);
        Assert.Empty(context.EnabledConnectionIds);
    });

    [Fact]
    public Task TryIt_NoEnabledConnections_IsSkippedAndComplete() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        using var step = new TryItStep(rig.Context, actions: null);

        await step.EnterAsync(default);

        Assert.True(step.IsComplete);
        Assert.True(step.ViewModel.IsSkipped);
    });

    [Fact]
    public Task TryIt_UsesEnabledConnections_AndFinishOpensAgentOnlyWhenChecked() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        var dev = Profile("dev-erp", ConnectionEnvironment.Development);
        rig.Connections.Profiles.Add(dev);
        rig.Context.EnabledConnectionIds = [dev.Id];
        var actions = new RecordingActions();
        using var step = new TryItStep(rig.Context, actions);

        await step.EnterAsync(default);
        Assert.False(step.IsComplete);
        Assert.Equal("dev-erp", step.ViewModel.SelectedConnection?.Name);
        await step.ViewModel.ExplainAsync();
        Assert.True(step.IsComplete);

        await step.FinishAsync(default);
        Assert.Equal(0, actions.AgentOpens);
        step.ViewModel.AlsoExternalAgent = true;
        await step.FinishAsync(default);
        Assert.Equal(1, actions.AgentOpens);
    });

    [Fact]
    public Task Server_FileConnect_SetsCreatedProfileId() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        using var step = new ServerStep(rig.Context);
        await step.EnterAsync(default);
        Assert.False(step.IsComplete);

        step.ViewModel.LoadFileText("{ \"format\": \"oranda-ai\", \"version\": 1, \"baseUrl\": \"https://ai.example.com/v1\", \"apiFormat\": \"openai\", \"model\": \"big-model\" }");
        await step.ViewModel.ConnectAsync();

        Assert.True(step.IsComplete, step.ViewModel.ErrorText);
        Assert.NotNull(rig.Context.CreatedProfileId);
        Assert.Equal(rig.Context.CreatedProfileId, rig.Profiles.Settings.Current.Ai.ActiveProfileId);
    });

    [Fact]
    public Task Server_ResumeWithExistingProfile_IsComplete() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        var existing = ProfileSamples.Example();
        await rig.Profiles.Settings.UpdateAsync(s => s with { Ai = s.Ai with { Profiles = [existing] } });
        rig.Context.CreatedProfileId = existing.Id;
        using var step = new ServerStep(rig.Context);

        await step.EnterAsync(default);

        Assert.True(step.IsComplete);
    });

    [Fact]
    public Task AskAdmin_CopyUsesContextClipboard_AndCompletes() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        using var step = new AskAdminStep(rig.Context);
        await step.EnterAsync(default);

        await step.ViewModel.CopyAsync();

        Assert.True(step.IsComplete);
        Assert.Contains("0.1.5", Assert.Single(rig.Copied));
    });

    [Theory]
    [InlineData("ko", "Light", 360)]
    [InlineData("en", "Dark", 360)]
    [InlineData("ko", "Dark", 640)]
    [InlineData("en", "Light", 640)]
    public Task Views_FitWithoutClipping(string language, string theme, double width) => HeadlessHost.Run(async () =>
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        app.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            var rig = await Rig.CreateAsync();
            rig.Text.SetLanguage(language);
            rig.Connections.Profiles.AddRange([Profile("dev-erp", ConnectionEnvironment.Development), Profile("prd-erp-with-a-rather-long-name", ConnectionEnvironment.Production)]);
            rig.Context.EnabledConnectionIds = [.. rig.Connections.Profiles.Select(p => p.Id)];

            var access = new AccessStep(rig.Context);
            var tryIt = new TryItStep(rig.Context, new RecordingActions());
            var server = new ServerStep(rig.Context);
            var ask = new AskAdminStep(rig.Context);
            IAiSetupStep[] steps = [server, access, tryIt, ask];
            foreach (var step in steps)
            {
                await step.EnterAsync(default);
            }

            await tryIt.ViewModel.ExplainAsync(); // 답 뒤 화면(3기능·확인란)까지 그린다
            server.ViewModel.UseAddressOnly();
            server.ViewModel.Address = "http://ai.example.com/v1"; // 경고 줄까지

            foreach (var step in steps)
            {
                var window = new Window { Width = width, Height = 900, Content = new ScrollViewer { Content = step.View, Padding = new Thickness(16) } };
                window.Show();
                HeadlessHost.Settle(window);
                try
                {
                    AssertFits(window, step.View, step.StepId);
                }
                finally
                {
                    window.Close();
                }
            }

            var row = access.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("ai-setup-production"));
            Assert.True(app.TryFindResource("OrandaEnvProductionBrush", app.ActualThemeVariant, out var red));
            Assert.Equal(red, row.BorderBrush);

            foreach (var step in steps)
            {
                step.Dispose();
            }
        }
        finally
        {
            app.RequestedThemeVariant = before;
        }
    });

    // 보이는 글 상자는 단계 화면 폭 안에 있고, 줄바꿈이 없는 글은 잘리지 않는다(말줄임표로 줄인 이름 글은 예외).
    private static void AssertFits(Window window, Control view, AiSetupStepId id)
    {
        var right = view.Bounds.Width + 0.5;
        foreach (var text in view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            if (text.TranslatePoint(new Point(text.Bounds.Width, 0), view) is not { } edge)
            {
                continue;
            }

            Assert.True(edge.X <= right, $"{id}: '{text.Text}' 가 화면 밖으로 나감({edge.X:0} > {right:0}, 창 {window.Width})");
            if (text.TextWrapping == Avalonia.Media.TextWrapping.NoWrap && text.TextTrimming == Avalonia.Media.TextTrimming.None)
            {
                text.Measure(Size.Infinity);
                Assert.True(text.DesiredSize.Width <= text.Bounds.Width + 1, $"{id}: '{text.Text}' 가 잘림({text.DesiredSize.Width:0.0} > {text.Bounds.Width:0.0}, 창 {window.Width})");
            }
        }
    }

    private sealed class RecordingActions : ITryItActions
    {
        public int AgentOpens { get; private set; }

        public Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql) => Guid.NewGuid();

        public Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct) =>
            Task.FromResult(new TryItExplainResult(true, "DUAL 은 한 행만 돌려주는 시험용 표입니다. 이 문장은 그 한 행을 그대로 읽습니다."));

        public Task OpenExternalAgentAsync(CancellationToken ct)
        {
            AgentOpens++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public Task TryIt_IsNotCompleteBeforeEnter_AndKeepsStateOnReenter() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        var dev = Profile("dev-erp", ConnectionEnvironment.Development);
        rig.Connections.Profiles.Add(dev);
        using var step = new TryItStep(rig.Context, new RecordingActions());
        Assert.False(step.IsComplete); // 접속을 읽기 전에는 「건너뜀」 으로 보지 않는다

        rig.Context.EnabledConnectionIds = [dev.Id];
        await step.EnterAsync(default);
        var first = step.ViewModel;
        first.AlsoExternalAgent = true;
        await step.LeaveAsync(default);
        await step.EnterAsync(default);

        Assert.Same(first, step.ViewModel);
        Assert.True(step.ViewModel.AlsoExternalAgent);
    });

    [Fact]
    public Task Steps_NeverRequestNextOnTheirOwn() => HeadlessHost.Run(async () =>
    {
        var rig = await Rig.CreateAsync();
        var requested = 0;
        IAiSetupStep[] steps = [new ServerStep(rig.Context), new AccessStep(rig.Context), new TryItStep(rig.Context, null), new AskAdminStep(rig.Context)];
        foreach (var step in steps)
        {
            step.NextRequested += (_, _) => requested++;
            await step.EnterAsync(default);
            await step.LeaveAsync(default);
            step.Dispose();
        }

        Assert.Equal(0, requested);
    });
}
