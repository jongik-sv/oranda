using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Connection;
using Oranda.Core.Features;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 설정 「AI」 탭(ai-settings-one-place 2026-11-24): 연결 상태 카드의 네 모양, 탭에서 연 도우미가 끝나면 방금 고른 값이 보이는지(설정 창 저장이 옛 값으로 덮지 않는지),
/// 연결 시험 기록, 막혔을 때의 「기능 켜기」·「주 업무 바꾸기」, 「AI 를 쓸 접속」 이 도우미와 같은 값인지, 화면 묶음(고급은 접힘).
/// </summary>
[Collection("UiLanguage")]
public sealed class AiSettingsHubTests
{
    private static readonly Oranda.App.Shell.ShellText Ko = new(language: "ko");

    private sealed class Gate(bool aiTuning) : IFeatureGate
    {
        public bool IsEnabled(string featureId) => featureId != FeatureIds.AiTuning || aiTuning;

        public bool IsCommandEnabled(string commandId, Type handlerType) => true;

        public bool IsPanelEnabled(string panelId, Type viewModelType) => true;

        public bool IsTypeEnabled(Type registeredType) => true;

        public bool ReadOnlyRole => false;

        public IReadOnlyCollection<string> PendingRestart => [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private static SettingsService Settings(AiSettings? ai = null, FeaturesSettings? features = null)
    {
        var settings = new SettingsService(new MemorySettingsStore());
        settings.UpdateAsync(s => s with { Ai = ai ?? s.Ai, Features = features ?? s.Features }).GetAwaiter().GetResult();
        return settings;
    }

    private static AiSettingsHubPage Hub(SettingsService settings, AiSettingsHubServices? services = null, bool aiOn = true) =>
        new(settings, Ko, ai => new AiModelSettingsPage(ai, new AiProfileServices(settings, Text: Ko)), services, () => aiOn);

    [Fact]
    public void Card_shows_not_connected_connected_problem_and_blocked()
    {
        var none = Hub(Settings(new AiSettings { Driver = AiDriverKind.None }));
        Assert.Equal(AiHubState.NotConnected, none.State);
        Assert.Equal("아직 AI 를 연결하지 않았어요", none.Headline);
        Assert.Equal("연결 도우미 시작", none.StartWizardText);

        var claude = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet" }));
        Assert.Equal(AiHubState.Connected, claude.State);
        Assert.Equal("사용 중: Claude Code · sonnet", claude.Headline);
        Assert.Contains("아직 연결 시험을 하지 않았어요", claude.Detail, StringComparison.Ordinal);

        var profile = ProfileSamples.Example();
        var server = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, ActiveProfileId = profile.Id, Profiles = [profile] }));
        Assert.StartsWith("사용 중: " + profile.Name, server.Headline, StringComparison.Ordinal);

        var missing = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, ActiveProfileId = Guid.NewGuid() }));
        Assert.Equal(AiHubState.Problem, missing.State);
        Assert.Contains("지워졌습니다", missing.Detail, StringComparison.Ordinal);

        var off = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode }), aiOn: false);
        Assert.Equal(AiHubState.Blocked, off.State);
        Assert.Equal("AI 기능이 꺼져 있어요", off.Headline);
        Assert.Equal("기능 켜기", off.BlockedActionText);
        Assert.False(off.CanOpenAgentConnect);

        var viewer = Hub(Settings(features: new FeaturesSettings { Roles = [RoleIds.Viewer] }));
        Assert.Equal(AiHubState.Blocked, viewer.State);
        Assert.Equal("주 업무 바꾸기", viewer.BlockedActionText);
    }

    [Fact]
    public void Last_test_line_shows_the_saved_result()
    {
        var at = new DateTimeOffset(2026, 11, 24, 10, 12, 0, TimeSpan.Zero);
        var ok = Hub(Settings(new AiSettings
        {
            Driver = AiDriverKind.ClaudeCode,
            Setup = new AiSetupSettings { LastTest = new AiConnectionTestRecord(at, true, "Claude Code", 1200) },
        }));
        Assert.Contains("Claude Code · 성공 (1.2초)", ok.Detail, StringComparison.Ordinal);
        Assert.False(ok.LastTestFailed);

        var failed = Hub(Settings(new AiSettings
        {
            Driver = AiDriverKind.ClaudeCode,
            Setup = new AiSetupSettings { LastTest = new AiConnectionTestRecord(at, false, "Claude Code", 0, AiSetupErrorKind.NotLoggedIn) },
        }));
        Assert.Contains("실패 — ", failed.Detail, StringComparison.Ordinal);
        Assert.True(failed.LastTestFailed);
    }

    [Fact]
    public async Task Wizard_from_the_tab_returns_to_the_values_it_just_chose_and_save_keeps_them()
    {
        var settings = Settings(new AiSettings { Driver = AiDriverKind.None });
        var opened = new List<(Window? Owner, AiSetupEntry Entry)>();
        var services = new AiSettingsHubServices(OpenWizard: async (owner, entry, _) =>
        {
            opened.Add((owner, entry));
            // 도우미가 Claude Code 를 고르고 시험한 흉내: 설정에 곧바로 쓴다.
            await settings.UpdateAsync(s => s with
            {
                Ai = s.Ai with
                {
                    Driver = AiDriverKind.ClaudeCode,
                    Model = "opus",
                    Setup = s.Ai.Setup with { LastTest = new AiConnectionTestRecord(DateTimeOffset.Now, true, "Claude Code", 900) },
                },
            });
        });
        var vm = new SettingsViewModel(settings, Ko, featureGate: new Gate(aiTuning: true), aiHub: services);
        var before = vm.AiModel;
        Assert.Equal(AiHubState.NotConnected, vm.AiHub.State);
        var replaced = 0;
        vm.AiHub.ModelReplaced += (_, _) => replaced++;

        await vm.AiHub.StartWizardCommand.ExecuteAsync(null);

        Assert.Equal([(null, AiSetupEntry.Menu)], opened.Select(o => (o.Owner, o.Entry)));
        Assert.Equal(1, replaced);
        Assert.NotSame(before, vm.AiModel);
        Assert.True(vm.AiModel.IsClaudeCode); // 「고급」 에 방금 고른 값
        Assert.Equal("opus", vm.AiModel.Model);
        Assert.Equal(AiHubState.Connected, vm.AiHub.State);
        Assert.Contains("성공", vm.AiHub.Detail, StringComparison.Ordinal);

        await vm.SaveAsync(); // 설정 창 「저장」 이 옛 사본(선택 안 함)으로 덮지 않는다
        Assert.Equal((AiDriverKind.ClaudeCode, "opus"), (settings.Current.Ai.Driver, settings.Current.Ai.Model));
    }

    [Fact]
    public async Task Queued_wizard_runs_once_after_the_window_opens_and_releases_the_caller()
    {
        var runs = 0;
        var hub = Hub(Settings(), new AiSettingsHubServices(OpenWizard: (_, entry, reason) =>
        {
            runs++;
            Assert.Equal((AiSetupEntry.FailureBand, (AiSetupErrorKind?)AiSetupErrorKind.NotLoggedIn), (entry, reason));
            return Task.CompletedTask;
        }));
        var done = new TaskCompletionSource();
        hub.QueueWizard(AiSetupEntry.FailureBand, AiSetupErrorKind.NotLoggedIn, done);

        await hub.RunQueuedWizardAsync();
        await hub.RunQueuedWizardAsync();

        Assert.Equal(1, runs);
        Assert.True(done.Task.IsCompleted);

        var never = new TaskCompletionSource();
        hub.QueueWizard(AiSetupEntry.FirstRun, null, never);
        hub.Dispose(); // 띄우지 못하고 닫혀도 기다리는 쪽이 풀린다
        Assert.True(never.Task.IsCompleted);
    }

    [Fact]
    public async Task Test_button_runs_the_assistant_test_for_the_saved_target_and_records_it()
    {
        var settings = Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet" });
        var claude = new FakeClaude();
        var hub = Hub(settings, new AiSettingsHubServices(Test: () => new AiSetupContext(settings) { Claude = claude, Text = Ko }));
        Assert.True(hub.CanTest);

        await hub.TestCommand.ExecuteAsync(null);

        Assert.Equal(Oranda.App.Features.AiSetup.Test.AiSetupTestState.Succeeded, hub.TestState);
        var record = settings.Current.Ai.Setup.LastTest!;
        Assert.True(record.Succeeded);
        Assert.Equal("Claude Code", record.Target);
        Assert.Contains("Claude Code · 성공 (1.0초)", hub.Detail, StringComparison.Ordinal);

        var cli = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, CustomClis = [new AiCustomCli { Id = "c1", Name = "Example CLI", Command = "examplecli {prompt}" }], SystemCliId = "custom:c1" }),
            new AiSettingsHubServices(Test: () => new AiSetupContext(settings)));
        Assert.Equal(AiHubState.Connected, cli.State);
        Assert.False(cli.CanTest); // 기타 CLI 는 쓸 때 확인한다
        Assert.True(cli.ShowTestUnavailable);
    }

    [Fact]
    public void Blocked_buttons_open_the_features_tab_and_turn_ai_tuning_on_when_allowed()
    {
        var settings = Settings();
        var vm = new SettingsViewModel(settings, Ko, featureGate: new Gate(aiTuning: false), aiHub: new AiSettingsHubServices());
        vm.SelectedTab = SettingsViewModel.AiTabIndex;
        var ai = vm.Features.Items.Single(i => i.Id == FeatureIds.AiTuning);
        if (ai.IsChecked)
        {
            ai.IsChecked = false; // 이 Role 의 기본이 켬이면 먼저 끈 상태를 만든다
        }

        vm.AiHub.ResolveBlockedCommand.Execute(null);

        Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);
        Assert.True(ai.IsChecked); // 「저장」 해야 적용(재시작 안내는 「기능」 탭)
    }

    [Fact]
    public async Task Access_group_uses_the_assistant_values_and_saves_at_once()
    {
        var dev = new ConnectionProfile("dev-erp", "scott", "db.example.com:1521/DEV", Id: Guid.NewGuid());
        var prd = new ConnectionProfile("prd-erp", "scott", "db.example.com:1521/PRD", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production, AiAccess: AiAccessLevel.Off);
        var saved = new List<ConnectionProfile>();
        var access = new AccessStepServices(_ => Task.FromResult<IReadOnlyList<ConnectionProfile>>([dev, prd]), (p, _) =>
        {
            saved.Add(p);
            return Task.FromResult(p);
        });
        var hub = Hub(Settings(), new AiSettingsHubServices(Access: access));

        await hub.LoadAccessAsync();
        Assert.Equal([true, false], hub.Access!.Rows.Select(r => r.IsOn));
        hub.Access.Rows[1].IsOn = true;
        await hub.Access.LastSave;

        Assert.Equal(prd.Id, Assert.Single(saved).Id);
        Assert.Null(saved[0].AiAccess); // 켬 = 환경 기본(도우미와 같은 값)
    }

    [Fact]
    public Task Tab_view_starts_collapsed_and_shows_the_wizard_button_when_not_connected() => HeadlessHost.Run(async () =>
    {
        var dev = new ConnectionProfile("dev-erp", "scott", "db.example.com:1521/DEV", Id: Guid.NewGuid());
        var access = new AccessStepServices(_ => Task.FromResult<IReadOnlyList<ConnectionProfile>>([dev]), (p, _) => Task.FromResult(p));
        Window? seenOwner = null;
        var hub = Hub(Settings(new AiSettings { Driver = AiDriverKind.None }), new AiSettingsHubServices(
            OpenWizard: (owner, _, _) =>
            {
                seenOwner = owner;
                return Task.CompletedTask;
            },
            Access: access));
        var view = new AiSettingsHubView { DataContext = hub };
        var window = new Window { Width = 640, Height = 900, Content = new ScrollViewer { Content = view } };
        window.Show();
        try
        {
            HeadlessHost.Pump(4);
            await HeadlessHost.WaitUntil(() => hub.Access!.Rows.Count == 1);
            HeadlessHost.Pump(2);

            Assert.False(view.Advanced.IsExpanded);
            Assert.True(view.StartWizardButton.IsEffectivelyVisible);
            Assert.False(view.TestButton.IsVisible);
            Assert.False(view.BlockedButton.IsVisible);
            Assert.False(view.AgentConnectButton.IsEffectivelyEnabled); // 연결 창 부품이 없는 조립
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "dev-erp");

            view.StartWizardButton.Command!.Execute(null);
            await HeadlessHost.WaitUntil(() => seenOwner is not null);
            Assert.Same(window, seenOwner); // 도우미는 이 탭이 있는 창 위에

            view.Advanced.IsExpanded = true;
            HeadlessHost.Pump(2);
            Assert.True(hub.AdvancedExpanded);
            Assert.Contains(view.GetVisualDescendants().OfType<AiModelSettingsView>(), v => v.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Settings_window_opened_for_the_assistant_shows_it_over_itself_after_sizing() => HeadlessHost.Run(async () =>
    {
        // 첫 실행 묻기·실패 띠·옛 명령: 설정 창을 「AI」 탭으로 열고, 창 크기를 정한 뒤 그 창 위에 도우미를 띄운다. 닫히면 기다리던 쪽이 풀린다.
        var settings = Settings(new AiSettings { Driver = AiDriverKind.None });
        Window? seenOwner = null;
        var release = new TaskCompletionSource();
        var vm = new SettingsViewModel(settings, Ko, featureGate: new Gate(aiTuning: true), aiHub: new AiSettingsHubServices(OpenWizard: async (owner, entry, _) =>
        {
            seenOwner = owner;
            Assert.Equal(AiSetupEntry.FirstRun, entry);
            await release.Task;
        }));
        var done = new TaskCompletionSource();
        vm.SelectedTab = SettingsViewModel.AiTabIndex;
        vm.AiHub.QueueWizard(AiSetupEntry.FirstRun, null, done);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => seenOwner is not null);
            Assert.Same(window, seenOwner);
            Assert.Same(window, SettingsWindow.Current);
            Assert.Equal(SettingsViewModel.AiTabIndex, vm.SelectedTab);
            Assert.False(done.Task.IsCompleted);

            release.SetResult();
            await HeadlessHost.WaitUntil(() => done.Task.IsCompleted);
        }
        finally
        {
            window.Close();
        }

        Assert.Null(SettingsWindow.Current);
    });

    [Fact]
    public void Tab_texts_have_english()
    {
        var en = new AiSettingsHubPage(Settings(new AiSettings { Driver = AiDriverKind.None }), new Oranda.App.Shell.ShellText(language: "en"),
            ai => new AiModelSettingsPage(ai), aiFeatureOn: () => true);
        Assert.Equal("AI is not connected yet", en.Headline);
        Assert.Equal("Start setup assistant", en.StartWizardText);
        Assert.Equal("Connections that use AI", en.AccessTitle);
        Assert.Equal("External agents", en.AgentTitle);
        Assert.Equal("Advanced", en.AdvancedTitle);
        Assert.DoesNotContain(new[] { en.Detail, en.AccessHint, en.AgentHint, en.AdvancedHint, en.AgentStatusText, en.TestUnavailableText },
            t => t.Any(c => c is >= '가' and <= '힣'));
    }
}
