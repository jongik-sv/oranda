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
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Connection;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 설정 「AI」 탭(ai-settings-one-place 2026-11-24, 확인된 시안): 연결 전·막힘의 큰 칸과 접힌 세 묶음, 연결됨의 ① 공유 표 · ② Oranda 안의 AI · ③ 밖의 AI 에이전트,
/// ②·③의 접속별 실효 수준 줄(③은 min(접속, 외부 상한)), 탭에서 연 도우미 뒤 값(저장 안 한 고급 변경은 묻고 버림), 연결 시험(Claude Code·CLI), 막혔을 때의 단추, 묶음 보여 주기.
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

    private static readonly ConnectionProfile Dev = new("dev-erp", "scott", "db.example.com:1521/DEV", Id: Guid.NewGuid());
    private static readonly ConnectionProfile Prd = new("prd-erp", "scott", "db.example.com:1521/PRD", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production);

    private static AccessStepServices Access(params ConnectionProfile[] profiles)
    {
        var list = profiles.ToList();
        return new AccessStepServices(_ => Task.FromResult<IReadOnlyList<ConnectionProfile>>([.. list]), (p, _) =>
        {
            list[list.FindIndex(x => x.Id == p.Id)] = p;
            return Task.FromResult(p);
        });
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
    public void States_pick_the_big_box_or_the_three_open_groups()
    {
        var none = Hub(Settings(new AiSettings { Driver = AiDriverKind.None }));
        Assert.Equal(AiHubState.NotConnected, none.State);
        Assert.True(none.ShowsStartBox);
        Assert.Equal("AI 가 아직 연결되지 않았습니다", none.Headline);
        Assert.Contains("\n", none.Detail, StringComparison.Ordinal); // 두 줄 안내
        Assert.False(none.AccessExpanded || none.InsideExpanded || none.OutsideExpanded); // 연결 전에는 한 줄로 접힘
        Assert.Equal("도우미 마지막 단계에서 정합니다. 지금 바로 정해도 됩니다.", none.AccessCollapsedHint);
        Assert.Equal("필요한 사람만. 터미널에서 AI 를 쓰는 개발자용", none.OutsideCollapsedHint);

        var claude = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet" }));
        Assert.Equal(AiHubState.Connected, claude.State);
        Assert.False(claude.ShowsStartBox);
        Assert.True(claude.AccessExpanded && claude.InsideExpanded && claude.OutsideExpanded);
        Assert.Equal("연결됨 · Claude Code · sonnet", claude.InsideStatusText);
        Assert.Contains("아직 연결 시험을 하지 않았어요", claude.Detail, StringComparison.Ordinal);

        var profile = ProfileSamples.Example();
        var server = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, ActiveProfileId = profile.Id, Profiles = [profile] }));
        Assert.StartsWith("연결됨 · " + profile.Name, server.InsideStatusText, StringComparison.Ordinal);

        var missing = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, ActiveProfileId = Guid.NewGuid() }));
        Assert.Equal(AiHubState.Problem, missing.State);
        Assert.True(missing.ShowsInsideStatus);
        Assert.Contains("지워졌습니다", missing.Detail, StringComparison.Ordinal);

        var off = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode }), aiOn: false);
        Assert.Equal(AiHubState.Blocked, off.State);
        Assert.True(off.ShowsStartBox);
        Assert.Equal("AI 기능이 꺼져 있어요", off.Headline);
        Assert.Equal("기능 탭에서 켜기", off.BlockedActionText);
        Assert.False(off.CanOpenAgentConnect);

        var viewer = Hub(Settings(features: new FeaturesSettings { Roles = [RoleIds.Viewer] }));
        Assert.Equal(AiHubState.Blocked, viewer.State);
        Assert.Equal("주 업무 바꾸기", viewer.BlockedActionText);
    }

    [Fact]
    public async Task Inside_and_outside_lines_follow_table_one_with_the_external_cap()
    {
        var hub = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode }), new AiSettingsHubServices(Access: Access(Dev, Prd with { AiAccess = AiAccessLevel.Plan })));
        await hub.LoadAccessAsync();

        Assert.Equal("아래 두 연결(2 · 3)이 모두 이 표를 따릅니다.", hub.AccessNote);
        Assert.Equal("위 1 의 표를 그대로 따릅니다: dev-erp 계획 · prd-erp 계획", hub.InsideSummary);
        var prdOut = AiConnectionAccess.Of(Prd with { AiAccess = AiAccessLevel.Plan });
        var prdText = hub.Access!.Text.LevelName(prdOut.ExternalLevel) + (prdOut.ExternalCapped ? " (외부 상한)" : string.Empty);
        Assert.Equal("위 1 의 표를 그대로 따릅니다: dev-erp 계획 · prd-erp " + prdText + "\n운영 접속은 연결할 때 접속 이름을 한 번 더 입력해야 고를 수 있습니다.", hub.OutsideSummary);
        Assert.Equal("외부 에이전트는 자기 프로그램과 자기 로그인을 씁니다. Oranda 는 어느 접속을 어디까지 볼지만 정합니다.", hub.OutsideNote);

        hub.Access.Rows[0].Level = AiAccessLevel.QueryRows; // ① 에서 바꾸면 두 줄이 같이 바뀐다
        await hub.Access.LastSave;
        Assert.StartsWith("위 1 의 표를 그대로 따릅니다: dev-erp 조회+행 값", hub.InsideSummary, StringComparison.Ordinal);
        Assert.Contains("dev-erp 조회+행 값", hub.OutsideSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_external_cap_marker_appears_only_when_the_cap_lowers_the_level()
    {
        // 시안의 「prod-erp 메타데이터 (외부 상한)」 은 운영 실행 스위치를 끈 빌드의 모습이다. 지금 스위치 값에서 그대로 계산한다.
        var prd = AiConnectionAccess.Of(Prd);
        Assert.Equal(!AgentGrantPolicy.ProductionRuns, prd.ExternalCapped);
    }

    [Fact]
    public void Last_test_line_uses_plain_words_for_each_target()
    {
        var at = new DateTimeOffset(2026, 11, 24, 10, 12, 0, TimeSpan.Zero);
        var ok = Hub(Settings(new AiSettings
        {
            Driver = AiDriverKind.ClaudeCode,
            Setup = new AiSetupSettings { LastTest = new AiConnectionTestRecord(at, true, "Claude Code", 1200) },
        }));
        Assert.Contains("Claude Code · 성공 (1.2초)", ok.Detail, StringComparison.Ordinal);
        Assert.False(ok.LastTestFailed);

        var cli = Hub(Settings(new AiSettings
        {
            Driver = AiDriverKind.ClaudeCode,
            CustomClis = [new AiCustomCli { Id = "c1", Name = "Example CLI", Command = "examplecli {prompt}" }],
            SystemCliId = "custom:c1",
            Setup = new AiSetupSettings { LastTest = new AiConnectionTestRecord(at, false, "Example CLI", 0, AiSetupErrorKind.NotLoggedIn) },
        }));
        Assert.Contains("Example CLI 에 로그인되어 있지 않습니다", cli.Detail, StringComparison.Ordinal); // 「Claude」 로 적지 않는다
        Assert.True(cli.LastTestFailed);
    }

    [Fact]
    public async Task Wizard_from_the_tab_returns_to_the_values_it_just_chose_and_save_keeps_them()
    {
        var settings = Settings(new AiSettings { Driver = AiDriverKind.None });
        var opened = new List<AiSetupEntry>();
        var services = new AiSettingsHubServices(OpenWizard: async (_, entry, _) =>
        {
            opened.Add(entry);
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
        Assert.False(vm.AiHub.InsideExpanded);

        await vm.AiHub.StartWizardCommand.ExecuteAsync(null);

        Assert.Equal([AiSetupEntry.Menu], opened);
        Assert.NotSame(before, vm.AiModel);
        Assert.True(vm.AiModel.IsClaudeCode);
        Assert.Equal("opus", vm.AiModel.Model);
        Assert.Equal(AiHubState.Connected, vm.AiHub.State);
        Assert.True(vm.AiHub.InsideExpanded && vm.AiHub.AccessExpanded && vm.AiHub.OutsideExpanded); // 연결되면 세 묶음을 편다

        await vm.SaveAsync();
        Assert.Equal((AiDriverKind.ClaudeCode, "opus"), (settings.Current.Ai.Driver, settings.Current.Ai.Model));
    }

    [Fact]
    public async Task Unsaved_advanced_changes_are_discarded_only_after_asking()
    {
        var settings = Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet", MaskLiterals = false });
        var hub = Hub(settings, new AiSettingsHubServices(OpenWizard: (_, _, _) => settings.UpdateAsync(s => s with { Ai = s.Ai with { Model = "opus" } })));
        var asked = new List<string>();
        var answer = false;
        hub.ConfirmDiscard = q =>
        {
            asked.Add(q);
            return Task.FromResult(answer);
        };

        await hub.StartWizardCommand.ExecuteAsync(null); // 변경 없음 = 묻지 않고 새로 읽는다
        Assert.Empty(asked);
        Assert.Equal("opus", hub.Model.Model);

        hub.Model.MaskLiterals = true; // 저장 안 한 고급 변경
        var kept = hub.Model;
        await hub.StartWizardCommand.ExecuteAsync(null);
        Assert.Equal("저장 안 한 고급 설정 변경이 있습니다. 버리고 도우미가 고른 값을 쓸까요?", Assert.Single(asked));
        Assert.Same(kept, hub.Model); // 「그대로 두기」
        Assert.True(hub.Model.MaskLiterals);

        answer = true;
        await hub.StartWizardCommand.ExecuteAsync(null);
        Assert.Equal(2, asked.Count);
        Assert.NotSame(kept, hub.Model); // 「버리기」
        Assert.False(hub.Model.MaskLiterals);
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
        hub.Dispose();
        Assert.True(never.Task.IsCompleted);
    }

    [Fact]
    public async Task Test_button_runs_the_assistant_test_for_claude_code_and_records_it()
    {
        var settings = Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet" });
        var claude = new FakeClaude();
        var hub = Hub(settings, new AiSettingsHubServices(Test: () => new AiSetupContext(settings) { Claude = claude, Text = Ko }));
        Assert.True(hub.CanTest);

        await hub.TestCommand.ExecuteAsync(null);

        Assert.Equal(Oranda.App.Features.AiSetup.Test.AiSetupTestState.Succeeded, hub.TestState);
        Assert.True(settings.Current.Ai.Setup.LastTest!.Succeeded);
        Assert.Contains("Claude Code · 성공 (1.0초)", hub.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_button_sends_one_short_question_to_a_system_or_custom_cli()
    {
        var settings = Settings(new AiSettings
        {
            Driver = AiDriverKind.ClaudeCode,
            CustomClis = [new AiCustomCli { Id = "c1", Name = "Example CLI", Command = "examplecli {prompt}" }],
            SystemCliId = "custom:c1",
        });
        var calls = 0;
        var hub = Hub(settings, new AiSettingsHubServices(CliTest: (saved, _) =>
        {
            calls++;
            Assert.Equal("custom:c1", saved.SystemCliId);
            return Task.FromResult(new AiCliTestOutcome(false, TimeSpan.FromSeconds(90), AiSetupErrorKind.Timeout, "x"));
        }));
        Assert.True(hub.CanTest);

        await hub.TestCommand.ExecuteAsync(null);

        Assert.Equal(1, calls);
        var record = settings.Current.Ai.Setup.LastTest!;
        Assert.Equal(("Example CLI", false, AiSetupErrorKind.Timeout), (record.Target, record.Succeeded, record.Error));
        Assert.Contains("Example CLI 이(가) 제시간에 답하지 않았습니다", hub.Detail, StringComparison.Ordinal);
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
            ai.IsChecked = false;
        }

        vm.AiHub.ResolveBlockedCommand.Execute(null);

        Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);
        Assert.True(ai.IsChecked);
    }

    [Fact]
    public async Task Skill_status_reads_the_installer_states()
    {
        var states = new List<AgentSkillState> { AgentSkillState.NotInstalled };
        var hub = Hub(Settings(), new AiSettingsHubServices(SkillStates: () => [.. states]));
        await hub.LoadAccessAsync();
        Assert.Equal("스킬: 아직 설치하지 않았습니다", hub.SkillStatusText);

        states = [AgentSkillState.Current, AgentSkillState.Current];
        await hub.LoadAccessAsync();
        Assert.Equal("스킬: 설치됨 (2곳)", hub.SkillStatusText);

        states = [AgentSkillState.Current, AgentSkillState.Outdated];
        await hub.LoadAccessAsync();
        Assert.StartsWith("스킬: 다시 설치가 필요합니다", hub.SkillStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public Task Tab_view_shows_the_big_box_and_folded_groups_before_connecting() => HeadlessHost.Run(async () =>
    {
        Window? seenOwner = null;
        var hub = Hub(Settings(new AiSettings { Driver = AiDriverKind.None }), new AiSettingsHubServices(
            OpenWizard: (owner, _, _) =>
            {
                seenOwner = owner;
                return Task.CompletedTask;
            },
            Access: Access(Dev, Prd)));
        var view = new AiSettingsHubView { DataContext = hub };
        var window = new Window { Width = 700, Height = 900, Content = new ScrollViewer { Content = view } };
        window.Show();
        try
        {
            HeadlessHost.Pump(4);
            await HeadlessHost.WaitUntil(() => hub.Access!.Rows.Count == 2);
            HeadlessHost.Pump(2);

            Assert.True(view.StartBox.IsEffectivelyVisible);
            Assert.True(view.StartWizardButton.IsEffectivelyVisible);
            Assert.False(view.BlockedButton.IsVisible);
            Assert.False(view.AccessGroup.IsExpanded || view.InsideGroup.IsExpanded || view.OutsideGroup.IsExpanded);
            Assert.False(view.Advanced.IsExpanded);

            view.StartWizardButton.Command!.Execute(null);
            await HeadlessHost.WaitUntil(() => seenOwner is not null);
            Assert.Same(window, seenOwner);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Connected_view_has_numbered_groups_and_the_shared_table_with_five_level_buttons() => HeadlessHost.Run(async () =>
    {
        var hub = Hub(Settings(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet" }), new AiSettingsHubServices(Access: Access(Dev, Prd)));
        var view = new AiSettingsHubView { DataContext = hub };
        var window = new Window { Width = 760, Height = 1100, Content = new ScrollViewer { Content = view } };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => hub.Access!.Rows.Count == 2);
            HeadlessHost.Pump(4);

            Assert.False(view.StartBox.IsVisible);
            Assert.True(view.AccessGroup.IsExpanded && view.InsideGroup.IsExpanded && view.OutsideGroup.IsExpanded);
            Assert.Equal(["1", "2", "3"], new[] { view.AccessGroup, view.InsideGroup, view.OutsideGroup }.Select(Avalonia.Automation.AutomationProperties.GetName));
            var buttons = view.AccessGroup.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is AccessLevelButton).ToList();
            Assert.Equal(10, buttons.Count); // 접속 둘 × 다섯
            var prdRows = buttons.Where(b => ((AccessLevelButton)b.DataContext!).Row.IsProduction).ToList();
            Assert.False(prdRows.Single(b => ((AccessLevelButton)b.DataContext!).Level == AiAccessLevel.QueryRows).IsEnabled); // 운영은 조회 실행까지
            Assert.Contains(AccessTableView.SelectedClass, prdRows.Single(b => ((AccessLevelButton)b.DataContext!).Level == AiAccessLevel.Query).Classes);
            Assert.Contains(view.AccessGroup.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "운영은 「조회 실행」까지");
            Assert.StartsWith("위 1 의 표를 그대로 따릅니다", view.InsideSummary.Text, StringComparison.Ordinal);
            Assert.StartsWith("위 1 의 표를 그대로 따릅니다", view.OutsideSummary.Text, StringComparison.Ordinal);
            Assert.EndsWith("운영 접속은 연결할 때 접속 이름을 한 번 더 입력해야 고를 수 있습니다.", view.OutsideSummary.Text, StringComparison.Ordinal);
            Assert.True(view.TestButton.IsVisible);
            Assert.True(view.ReconnectButton.IsVisible);

            hub.Reveal(AiSettingsSection.Outside); // 상태 표시줄 띠 → ③
            Assert.Equal(AiSettingsSection.Outside, hub.RevealedSection);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Settings_window_opened_for_the_assistant_shows_it_over_itself_after_sizing() => HeadlessHost.Run(async () =>
    {
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
        Assert.Equal("Connections AI can see", en.AccessTitle);
        Assert.Equal("AI inside Oranda", en.InsideTitle);
        Assert.Equal("Outside AI agents", en.OutsideTitle);
        var hangul = new System.Text.RegularExpressions.Regex("[가-힣]");
        Assert.DoesNotContain(new[]
        {
            en.Detail, en.AccessNote, en.AccessCollapsedHint, en.InsideSubtitle, en.InsideCollapsedHint, en.InsideStatusText, en.InsideSummary,
            en.AdvancedTitle, en.OutsideSubtitle, en.OutsideCollapsedHint, en.OutsideSummary, en.OutsideNote, en.AgentStatusText, en.DiscardQuestion,
            en.ReconnectText, en.TestText, en.EnableFeatureText, en.ChangeRoleText, en.AgentConnectText, en.SkillInstallText,
        }, t => hangul.IsMatch(t));
    }
}
