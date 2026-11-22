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
        Assert.Equal("메타데이터만", Row(vm, "prd-a").LevelText);
        Assert.Equal(AiAccessLevel.Metadata, Row(vm, "prd-a").ExternalLevel);
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
        Assert.Equal(AiAccessLevel.Metadata, grant.FindConnection("prd-a")!.Level);
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
        Assert.Equal(AiAccessLevel.Metadata, grant.FindConnection("prd-a")!.Level);
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
