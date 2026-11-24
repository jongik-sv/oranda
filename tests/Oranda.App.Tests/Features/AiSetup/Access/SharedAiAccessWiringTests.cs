using Oranda.App.Features.Ai.External.Connect;
using Oranda.App.Features.AiSetup.Access;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Events;

namespace Oranda.App.Tests.Features.AiSetup.Access;

/// <summary>
/// 공유 허용 수준 한 길 배선(ai-settings-one-place 2026-11-24, 화면 변화 없음): 도우미의 「AI 를 쓸 접속」 단계와 외부 에이전트 연결 창의 「조회 허용」 이
/// <see cref="SharedAiAccessService"/> 로 저장하고, 그 저장이 다른 화면(설정 「AI」 탭이 듣는 서비스)에 곧바로 알려진다. 저장 꼴은 예전과 같다.
/// </summary>
public sealed class SharedAiAccessWiringTests
{
    /// <summary>접속 서비스 흉내: 저장하면 버스에 접속 저장 사건을 낸다(실제 접속 서비스와 같다).</summary>
    private sealed class Connections(EventBus bus, params ConnectionProfile[] profiles)
    {
        public List<ConnectionProfile> Profiles { get; } = [.. profiles];

        public List<ConnectionProfile> Saved { get; } = [];

        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ConnectionProfile>>([.. Profiles]);

        public Task<ConnectionProfile> SaveAsync(ConnectionProfile profile, CancellationToken ct)
        {
            Saved.Add(profile);
            Profiles[Profiles.FindIndex(p => p.Id == profile.Id)] = profile;
            bus.Publish(new ConnectionProfileSaved(profile));
            return Task.FromResult(profile);
        }

        /// <summary>창·단계가 쓰는 쪽(버스를 듣지 않음).</summary>
        public SharedAiAccessService Writer() => new(LoadAsync, SaveAsync);
    }

    private static ConnectionProfile Dev(AiAccessLevel? level = null) => new("dev-erp", "scott", "db.example.com:1521/DEV", Id: Guid.NewGuid(), AiAccess: level);

    private static ConnectionProfile Prd(AiAccessLevel? level = null) =>
        new("prd-erp", "scott", "db.example.com:1521/PRD", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production, AiAccess: level);

    [Fact]
    public async Task Wizard_access_step_saves_through_the_shared_service_in_the_same_shape()
    {
        var bus = new EventBus();
        var dev = Dev(AiAccessLevel.Off);
        var prd = Prd(AiAccessLevel.Off);
        var connections = new Connections(bus, dev, prd);
        using var tab = new SharedAiAccessService(connections.LoadAsync, connections.SaveAsync, bus); // 설정 「AI」 탭이 듣는 쪽
        var seen = new List<AiConnectionAccess>();
        tab.Changed += (_, a) => seen.Add(a);
        using var writer = connections.Writer();
        using var vm = new AccessStepViewModel(AccessStepServices.FromShared(writer), text: null);
        await vm.LoadAsync(CancellationToken.None);

        vm.Rows[0].IsOn = true; // 개발 켬
        vm.Rows[1].IsOn = true; // 운영 켬(환경 기본 = 조회 실행, 이름 재입력 없음 — 예전과 같다)
        Assert.True(await vm.SaveAsync(CancellationToken.None));

        Assert.Equal([null, null], connections.Saved.Select(p => p.AiAccess)); // 켬 = 환경 기본값(null), AccessChoice 와 같은 꼴
        Assert.Equal([AiAccessLevel.Plan, AiAccessLevel.Query], seen.Select(a => a.Level)); // 탭이 곧바로 안다
        Assert.Equal(2, vm.EnabledProfiles.Count);

        vm.Rows[0].IsOn = false;
        Assert.True(await vm.SaveAsync(CancellationToken.None));
        Assert.Equal(AiAccessLevel.Off, connections.Saved[^1].AiAccess);
        Assert.Equal(AiAccessLevel.Off, seen[^1].ExternalLevel);
    }

    [Fact]
    public async Task Connect_window_query_allow_raises_the_shared_level_and_the_tab_sees_it()
    {
        var bus = new EventBus();
        var dev = Dev();
        var prd = Prd();
        var connections = new Connections(bus, dev, prd);
        using var tab = new SharedAiAccessService(connections.LoadAsync, connections.SaveAsync, bus);
        var seen = new List<AiConnectionAccess>();
        tab.Changed += (_, a) => seen.Add(a);
        var (load, save) = ExternalAgentConnectCommands.ProfileAccess(connections.Writer());
        var grants = new AgentGrantStore();
        grants.AttachWriter(new NoFiles());
        using var vm = new ExternalAgentConnectViewModel(new ExternalAgentConnectServices(load, save, grants));
        await vm.LoadAsync();
        vm.Rows.Single(r => r.Alias == "dev-erp").IsChecked = true;
        vm.AllowQueryRows = true;

        Assert.True(await vm.ConnectAsync());

        var raised = Assert.Single(connections.Saved);
        Assert.Equal(dev.Id, raised.Id);
        Assert.Equal(AiAccessLevel.QueryRows, raised.AiAccess); // 개발 최대 = 조회 + 행 값
        var shown = Assert.Single(seen);
        Assert.Equal((AiAccessLevel.QueryRows, AiAccessLevel.QueryRows), (shown.Level, shown.ExternalLevel)); // ① 목록이 바로 같은 값
        Assert.Equal(AiAccessLevel.QueryRows, Assert.Single(grants.Active).FindConnection("dev-erp")!.Level);
    }

    [Fact]
    public async Task Without_connections_the_window_reads_nothing_and_saves_nothing()
    {
        var (load, save) = ExternalAgentConnectCommands.ProfileAccess(null);
        Assert.Empty(await load(CancellationToken.None));
        var profile = Dev();
        Assert.Same(profile, await save(profile, CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_shared_save_surfaces_as_a_save_failure()
    {
        var connections = new Connections(new EventBus(), Dev());
        using var writer = connections.Writer();
        var ghost = Dev(AiAccessLevel.QueryRows); // 목록에 없는 접속(그사이 지워짐)

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SaveDesiredAsync(ghost, null, CancellationToken.None));
        Assert.Empty(connections.Saved);
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
}
