using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.External.Connect;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>E1 「외부 에이전트 연결」 창 모델·창·띠 어댑터, 「스킬 설치」 창 모델(설계서 §3.1·§6.3·§6.4·§7.1). 허가 저장소는 실제 Core 구현을 쓴다.</summary>
public sealed class ExternalAgentConnectTests
{
    private static ConnectionProfile Profile(string name, ConnectionEnvironment environment, AiAccessLevel? level = null) =>
        new(name, "app", "db.example:1521/SAMPLE", Id: Guid.NewGuid(), Environment: environment) { AiAccess = level };

    private sealed class Harness
    {
        public List<ConnectionProfile> Profiles { get; } =
        [
            Profile("dev-a", ConnectionEnvironment.Development),
            Profile("test-a", ConnectionEnvironment.Test),
            Profile("prd-a", ConnectionEnvironment.Production),
            Profile("prd-off", ConnectionEnvironment.Production, AiAccessLevel.Off),
        ];

        public List<ConnectionProfile> Saved { get; } = [];

        public AgentGrantStore Grants { get; } = Store();

        public int BridgeStarts { get; private set; }

        public int Binds { get; private set; }

        public Exception? SaveFailure { get; set; }

        public ExternalAgentConnectViewModel Create()
        {
            var services = new ExternalAgentConnectServices(
                _ => Task.FromResult<IReadOnlyList<ConnectionProfile>>(Profiles),
                (p, _) =>
                {
                    if (SaveFailure is { } failure)
                    {
                        throw failure;
                    }

                    Saved.Add(p);
                    return Task.FromResult(p);
                },
                Grants,
                StartBridge: () => BridgeStarts++,
                Bind: () => Binds++,
                Label: () => "기본 작업 공간");
            return new ExternalAgentConnectViewModel(services);
        }
    }

    /// <summary>운영 접속(기본 수준 Query)의 허가 수준: 운영 실행 스위치(D5)가 켜져 있으면 Query, 끄면 Metadata.</summary>
    private static AiAccessLevel ProductionLevel => AgentGrantPolicy.ProductionRuns ? AiAccessLevel.Query : AiAccessLevel.Metadata;

    private static ExternalAgentConnectionRow Row(ExternalAgentConnectViewModel vm, string alias) => vm.Rows.Single(r => r.Alias == alias);

    [Fact]
    public async Task Development_is_checked_by_default_production_gives_metadata_only_and_off_cannot_be_chosen()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();

        Assert.True(Row(vm, "dev-a").IsChecked);
        Assert.False(Row(vm, "test-a").IsChecked);
        Assert.False(Row(vm, "prd-a").IsChecked);
        var prd = Row(vm, "prd-a");
        Assert.Equal(ExternalAgentConnectionRow.LevelName(prd.ExternalLevel), prd.LevelText);
        Assert.Equal(AgentGrantPolicy.LevelFor(prd.Profile, AgentGrantPolicy.DefaultCap(ConnectionEnvironment.Production)), prd.ExternalLevel);
        Assert.True(prd.IsProductionMarked);
        Assert.False(Row(vm, "dev-a").IsProductionMarked);
        Assert.False(Row(vm, "prd-off").Usable);
        Assert.False(vm.IsConnected);
        Assert.True(vm.CanConnect);
    }

    [Fact]
    public async Task Choosing_production_needs_the_connection_name_typed_again()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        var prd = Row(vm, "prd-a");

        prd.IsChecked = true;
        Assert.True(prd.NeedsConfirmation);
        Assert.False(vm.CanConnect);

        prd.ConfirmName = "PRD-A";
        Assert.False(vm.CanConnect);
        prd.ConfirmName = "prd-a";
        Assert.True(vm.CanConnect);

        Assert.True(await vm.ConnectAsync());
        var grant = Assert.Single(h.Grants.Active);
        Assert.Equal(ProductionLevel, grant.FindConnection("prd-a")!.Level);
    }

    [Fact]
    public async Task Production_run_warning_follows_the_policy_cap()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        var prd = Row(vm, "prd-a");

        // 운영 기본 수준은 Query. 스위치가 켜져 있으면(상한 ≥ Query) 「LLM 이 SELECT 를 자동 실행합니다」, 끄면 Metadata 라 숨는다(§3.2 되돌리기).
        Assert.Equal(ExternalAgentConnectPolicy.ProductionRuns, prd.ExternalLevel >= AiAccessLevel.Query);
        Assert.Equal(ExternalAgentConnectPolicy.ProductionRuns, prd.ShowsRunWarning);
        Assert.False(Row(vm, "dev-a").ShowsRunWarning);
        Assert.False(Row(vm, "prd-off").ShowsRunWarning);
        Assert.Equal("운영 접속 — LLM 이 SELECT 를 자동 실행합니다", ExternalAgentConnectionRow.RunWarningText);
        Assert.Equal(
            ExternalAgentConnectPolicy.ProductionRuns ? "개발은 기본으로 고릅니다. 운영은 기본으로 고르지 않고, 고르려면 접속 이름을 다시 입력합니다." : "개발은 기본으로 고릅니다. 운영은 메타데이터만 쓰고, 고르려면 접속 이름을 다시 입력합니다.",
            ExternalAgentConnectViewModel.ConnectionsCaption);
    }

    [Fact]
    public async Task Local_model_is_off_by_default_and_warns_and_adds_a_production_line_when_both_are_chosen()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        var prd = Row(vm, "prd-a");

        Assert.False(vm.LocalModel);
        Assert.False(vm.ShowsLocalModelWarning);

        vm.LocalModel = true;
        Assert.True(vm.ShowsLocalModelWarning);
        Assert.False(prd.ShowsUnmaskedWarning); // 운영을 고르지 않았다
        Assert.False(vm.ShowsProductionUnmasked);
        Assert.False(vm.IncludesProduction);

        prd.IsChecked = true;
        Assert.True(vm.IncludesProduction);
        Assert.Equal(ExternalAgentConnectPolicy.LocalModelCoversProduction, prd.ShowsUnmaskedWarning);
        Assert.Equal(ExternalAgentConnectPolicy.LocalModelCoversProduction, vm.ShowsProductionUnmasked);
        Assert.False(Row(vm, "dev-a").ShowsUnmaskedWarning);
        Assert.True(prd.NeedsConfirmation); // 로컬 모델이어도 이름 다시 입력은 그대로(SAFE-08)

        vm.LocalModel = false;
        Assert.False(prd.ShowsUnmaskedWarning);
        Assert.False(vm.ShowsLocalModelWarning);
        Assert.Equal("운영 값이 가리지 않고 나갑니다", ExternalAgentConnectionRow.UnmaskedWarningText);
    }

    [Fact]
    public async Task Local_model_goes_into_the_grant_and_is_off_unless_checked()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();

        await vm.ConnectAsync();
        Assert.False(Assert.Single(h.Grants.Active).LocalModel);

        vm.LocalModel = true;
        await vm.ConnectAsync();
        var grant = Assert.Single(h.Grants.Active);
        Assert.True(grant.LocalModel);
        Assert.False(grant.MasksSensitive(grant.FindConnection("dev-a")!.ProfileId)); // 개발 + 로컬 모델 = 가리지 않음
    }

    [Fact]
    public async Task Allow_queries_raises_production_to_query_not_query_rows_and_only_after_the_name_is_typed()
    {
        var h = new Harness();
        h.Profiles.Add(Profile("prd-plan", ConnectionEnvironment.Production, AiAccessLevel.Plan));
        using var vm = h.Create();
        await vm.LoadAsync();
        var prd = Row(vm, "prd-plan");
        prd.IsChecked = true;
        vm.AllowQueryRows = true;

        // 이름을 다시 입력하지 않으면 「연결」도 저장도 없다.
        Assert.False(vm.CanConnect);
        Assert.False(await vm.ConnectAsync());
        Assert.Empty(h.Saved);
        Assert.Empty(h.Grants.Active);

        prd.ConfirmName = "prd-plan";
        Assert.True(await vm.ConnectAsync());

        var savedProduction = h.Saved.Where(p => p.Environment == ConnectionEnvironment.Production).ToList();
        if (ExternalAgentConnectPolicy.ProductionRuns)
        {
            // 운영 최대는 Query. QueryRows 를 쓰면 Plan 으로 읽혀 오히려 내려간다.
            var saved = Assert.Single(savedProduction);
            Assert.Equal(AiAccessLevel.Query, saved.AiAccess);
            Assert.Equal(AiAccessLevel.Query, AiAccessPolicy.Effective(saved));
            Assert.Equal(AiAccessLevel.Query, Assert.Single(h.Grants.Active).FindConnection("prd-plan")!.Level);
        }
        else
        {
            Assert.Empty(savedProduction);
            Assert.Equal(AiAccessLevel.Metadata, Assert.Single(h.Grants.Active).FindConnection("prd-plan")!.Level);
        }
    }

    [Fact]
    public async Task Expected_level_shows_what_allow_queries_will_give_before_connecting()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        var dev = Row(vm, "dev-a");
        Assert.Equal(AiAccessLevel.Plan, dev.ExpectedLevel);

        vm.AllowQueryRows = true;
        Assert.Equal(AiAccessLevel.QueryRows, dev.ExpectedLevel);
        Assert.Equal("조회 + 행 값", dev.LevelText);

        vm.AllowQueryRows = false;
        Assert.Equal(AiAccessLevel.Plan, dev.ExpectedLevel);
    }

    [Fact]
    public void Link_state_says_when_the_grant_includes_production()
    {
        var grants = Store();
        var services = new ServiceContainer();
        services.AddInstance<IAgentGrants>(grants);
        using var link = new ExternalAgentLink(services);
        link.Bind();

        grants.Create(new AgentGrantRequest([new AgentGrantTarget(Profile("dev-a", ConnectionEnvironment.Development), AiAccessLevel.QueryRows)], null));
        Assert.False(link.Current!.Production);

        var prd = Profile("prd-a", ConnectionEnvironment.Production);
        grants.Create(new AgentGrantRequest([new AgentGrantTarget(prd, AgentGrantPolicy.DefaultCap(prd.Environment))], null));
        Assert.True(link.Current!.Production);
    }

    [Fact]
    public async Task Connect_creates_an_8_hour_grant_for_the_chosen_connections_and_starts_the_bridge()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();

        Assert.True(await vm.ConnectAsync());

        var grant = Assert.Single(h.Grants.Active);
        Assert.Equal(["dev-a"], grant.Connections.Select(c => c.Alias));
        Assert.Equal(AiAccessLevel.Plan, grant.Connections[0].Level); // 개발 기본 수준
        Assert.NotNull(grant.ExpiresAt);
        Assert.InRange(grant.ExpiresAt!.Value - grant.CreatedAt, TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1));
        Assert.True(grant.AllowUnboundTabText);
        Assert.Equal(1, h.BridgeStarts);
        Assert.Equal(1, h.Binds);
        Assert.True(vm.IsConnected);
        Assert.StartsWith("연결됨", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task Without_the_8_hour_limit_the_grant_lasts_until_the_app_closes_and_unbound_tabs_can_be_closed_off()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        vm.ExpireIn8Hours = false;
        vm.AllowUnboundTabText = false;

        await vm.ConnectAsync();

        var grant = Assert.Single(h.Grants.Active);
        Assert.Null(grant.ExpiresAt);
        Assert.False(grant.AllowUnboundTabText);
        Assert.False(vm.CanExtend);
        Assert.Contains("앱을 끌 때까지", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allow_queries_saves_query_rows_on_chosen_development_and_test_connections_only()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        Row(vm, "test-a").IsChecked = true;
        var prd = Row(vm, "prd-a");
        prd.IsChecked = true;
        prd.ConfirmName = "prd-a";
        vm.AllowQueryRows = true;

        await vm.ConnectAsync();

        Assert.Equal(["dev-a", "test-a"], h.Saved.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(h.Saved, p => Assert.Equal(AiAccessLevel.QueryRows, p.AiAccess));
        var grant = Assert.Single(h.Grants.Active);
        Assert.Equal(AiAccessLevel.QueryRows, grant.FindConnection("dev-a")!.Level);
        Assert.Equal(ProductionLevel, grant.FindConnection("prd-a")!.Level);
    }

    [Fact]
    public async Task Allow_queries_starts_checked_when_the_chosen_connections_already_allow_rows()
    {
        var h = new Harness();
        h.Profiles[0] = h.Profiles[0] with { AiAccess = AiAccessLevel.QueryRows };
        using var vm = h.Create();

        await vm.LoadAsync();

        Assert.True(vm.AllowQueryRows);
    }

    [Fact]
    public async Task A_save_failure_is_shown_and_no_grant_is_made()
    {
        var h = new Harness { SaveFailure = new InvalidOperationException("connections.json 이(가) 읽기 전용") };
        using var vm = h.Create();
        await vm.LoadAsync();
        vm.AllowQueryRows = true;

        Assert.False(await vm.ConnectAsync());

        Assert.Empty(h.Grants.Active);
        Assert.Contains("읽기 전용", vm.Error, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Disconnect_revokes_and_extend_moves_the_expiry_and_reconnect_replaces_the_grant()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        await vm.ConnectAsync();
        var first = Assert.Single(h.Grants.Active);

        vm.Extend();
        Assert.True(Assert.Single(h.Grants.Active).ExpiresAt >= first.ExpiresAt);

        await vm.ConnectAsync();
        var second = Assert.Single(h.Grants.Active);
        Assert.NotEqual(first.GrantId, second.GrantId);

        vm.Disconnect();
        Assert.Empty(h.Grants.Active);
        Assert.False(vm.IsConnected);
        Assert.Equal("연결 안 됨", vm.StatusText);
    }

    // ---- 띠 어댑터 ----

    [Fact]
    public void Link_is_silent_until_bound_then_follows_the_grant_and_its_calls()
    {
        var grants = Store();
        var gate = new FakeExternalGate();
        var services = new ServiceContainer();
        services.AddInstance<IAgentGrants>(grants);
        services.AddInstance<IAgentExternalGate>(gate);
        using var link = new ExternalAgentLink(services);
        var changes = 0;
        var calls = new List<ExternalAgentCall>();
        link.Changed += (_, _) => changes++;
        link.CallCompleted += (_, c) => calls.Add(c);

        Assert.Null(link.Current);
        link.Bind();
        link.Bind(); // 두 번 불러도 한 번만 잇는다

        var issue = grants.Create(new AgentGrantRequest([], AgentGrantPolicy.DefaultLifetime));
        grants.AddClient(issue.Grant.GrantId, "Codex");
        Assert.Equal(["Codex"], link.Current!.Clients);
        Assert.NotNull(link.Current.ExpiresAt);
        Assert.True(changes >= 2);

        gate.Raise(issue.Grant.GrantId, "list_tabs");
        gate.Raise("old-grant", "late_call");
        Assert.Equal(["list_tabs"], calls.Select(c => c.Tool));
        Assert.Equal("Codex", calls[0].Client);

        link.Disconnect();
        Assert.Null(link.Current);
        Assert.Empty(grants.Active);
    }

    [Fact]
    public async Task Connect_without_an_open_bridge_shows_the_error_and_keeps_the_window()
    {
        var store = new AgentGrantStore();
        var services = new ExternalAgentConnectServices(_ => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]), (p, _) => Task.FromResult(p), store);
        using var vm = new ExternalAgentConnectViewModel(services);
        await vm.LoadAsync();

        Assert.False(await vm.ConnectAsync());

        Assert.False(string.IsNullOrEmpty(vm.Error));
        Assert.False(vm.IsConnected);
    }

    [Fact]
    public void Link_without_a_grant_store_stays_empty()
    {
        using var link = new ExternalAgentLink(new ServiceContainer());

        link.Bind();
        link.Extend();
        link.Disconnect();

        Assert.Null(link.Current);
    }

    // ---- 스킬 설치 창 모델 ----

    [Fact]
    public void Skill_rows_are_checked_where_the_cli_exists_or_a_managed_skill_is()
    {
        var skills = new ExternalAgentStatusTests.FakeSkills(AgentSkillState.NotInstalled, AgentSkillState.Outdated, AgentSkillState.NotInstalled);
        skills.Present.Add(AgentSkillTarget.Claude);

        var vm = new AgentSkillInstallViewModel(skills);

        Assert.Equal([true, true, false], vm.Rows.Select(r => r.IsChecked));
        Assert.Equal("옛 경로 — 다시 설치 필요", vm.Rows[1].StateText);
        Assert.Equal("/opt/Oranda/orandactl", vm.OrandactlPath);
    }

    [Fact]
    public async Task Install_skips_a_foreign_file_until_overwrite_is_pressed_for_that_row()
    {
        var skills = new ExternalAgentStatusTests.FakeSkills(AgentSkillState.Foreign, AgentSkillState.NotInstalled, AgentSkillState.NotInstalled);
        var vm = new AgentSkillInstallViewModel(skills);
        foreach (var row in vm.Rows)
        {
            row.IsChecked = true;
        }

        await vm.InstallAsync();

        Assert.True(vm.Rows[0].SkippedForeign);
        Assert.Equal(AgentSkillState.Foreign, vm.Rows[0].State);
        Assert.Equal(AgentSkillState.Current, vm.Rows[1].State);
        Assert.Contains("건너뜀", vm.Message, StringComparison.Ordinal);

        await vm.OverwriteAsync(vm.Rows[0]);

        Assert.False(vm.Rows[0].SkippedForeign);
        Assert.Equal(AgentSkillState.Current, vm.Rows[0].State);
    }

    [Fact]
    public async Task Uninstall_removes_checked_managed_skills_and_nothing_selected_says_so()
    {
        var skills = new ExternalAgentStatusTests.FakeSkills(AgentSkillState.Current, AgentSkillState.Current, AgentSkillState.NotInstalled);
        var vm = new AgentSkillInstallViewModel(skills);
        vm.Rows[1].IsChecked = false;

        await vm.UninstallAsync();

        Assert.Equal([AgentSkillTarget.Claude], skills.Removed);
        Assert.Equal(AgentSkillState.NotInstalled, vm.Rows[0].State);

        foreach (var row in vm.Rows)
        {
            row.IsChecked = false;
        }

        await vm.InstallAsync();
        Assert.Equal("고른 곳이 없습니다.", vm.Message);
    }

    // ---- 창(Headless) ----

    [Fact]
    public Task Window_lists_connections_and_connect_follows_the_production_name_check() => HeadlessHost.Run(async () =>
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        var skills = new ExternalAgentStatusTests.FakeSkills(AgentSkillState.NotInstalled, AgentSkillState.NotInstalled, AgentSkillState.NotInstalled);
        var window = new ExternalAgentConnectWindow(vm, skills);
        try
        {
            window.Show();
            HeadlessHost.Settle(window);

            var checks = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Contains(checks, c => (c.Content as string) == "dev-a" && c.IsChecked == true);
            var prd = checks.Single(c => (c.Content as string) == "prd-a");
            Assert.False(checks.Single(c => (c.Content as string) == "prd-off").IsEnabled);
            var connect = window.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "연결");
            Assert.True(connect.IsEnabled);

            prd.IsChecked = true;
            HeadlessHost.Settle(window);
            Assert.False(connect.IsEnabled);
            var confirm = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible);
            confirm.Text = "prd-a";
            HeadlessHost.Settle(window);
            Assert.True(connect.IsEnabled);

            // 운영 행 빨간 표시, 「로컬 모델」 체크와 경고.
            Assert.Contains(ExternalAgentConnectWindow.ProductionClass, prd.Classes);
            Assert.DoesNotContain(ExternalAgentConnectWindow.ProductionClass, checks.Single(c => (c.Content as string) == "dev-a").Classes);
            var local = checks.Single(c => c.Content is TextBlock { Text: "로컬 모델(가리지 않음)" });
            Assert.False(local.IsChecked);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("민감 열·리터럴", StringComparison.Ordinal) == true);
            local.IsChecked = true;
            HeadlessHost.Settle(window);
            Assert.True(vm.LocalModel);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("민감 열·리터럴", StringComparison.Ordinal) == true);
            var runWarning = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == ExternalAgentConnectionRow.RunWarningText && t.DataContext == Row(vm, "prd-a"));
            Assert.Equal(ExternalAgentConnectPolicy.ProductionRuns, runWarning.IsEffectivelyVisible);
            Assert.Contains(ExternalAgentConnectWindow.ProductionClass, runWarning.Classes);
            local.IsChecked = false;
            HeadlessHost.Settle(window);

            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => vm.IsConnected);
            HeadlessHost.Settle(window);
            var disconnect = window.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "끊기");
            Assert.True(disconnect.IsEffectivelyVisible);

            disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(h.Grants.Active);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Connect_command_is_registered_in_the_tools_menu()
    {
        var all = Oranda.App.Shell.ShellContributions.Scan(typeof(ExternalAgentConnectCommandHandler).Assembly);

        var command = Assert.Single(all.Commands, c => c.Definition.Id == ExternalAgentConnectCommands.Connect).Definition;
        Assert.Contains(command.Placements, p => p.StartsWith("menu:tools/", StringComparison.Ordinal));
    }

    /// <summary>연결 파일 기록기를 붙인 허가 저장소(다리가 열린 상태와 같다).</summary>
    private static AgentGrantStore Store()
    {
        var store = new AgentGrantStore();
        store.AttachWriter(new NoFiles());
        return store;
    }

    private sealed class NoFiles : IAgentEndpointWriter
    {
        public void Publish(AgentGrantIssue issue)
        {
        }

        public void Update(AgentGrantView grant)
        {
        }

        public void Remove(AgentGrantView grant)
        {
        }
    }

    private sealed class FakeExternalGate : IAgentExternalGate
    {
        public event EventHandler<AgentExternalToolActivity>? ExternalToolCompleted;

        public IReadOnlyList<Oranda.Core.Ai.Protocol.AgentToolInfo> ListTools(AgentGrantView grant) => [];

        public bool RunsWithoutDatabase(string toolName) => true;

        public Task<AgentGateResponse> CallToolAsync(AgentGrantView grant, string? client, string toolName, JsonNode? arguments, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Raise(string grantId, string tool) =>
            ExternalToolCompleted?.Invoke(this, new AgentExternalToolActivity(grantId, "Codex", null, 1, tool, AiToolJudgement.Allowed, 10, null, DateTimeOffset.UtcNow));
    }
}
