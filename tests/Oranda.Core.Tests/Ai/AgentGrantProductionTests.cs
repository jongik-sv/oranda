using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Testing;

namespace Oranda.Core.Tests.Ai;

/// <summary>
/// D5 운영 갈래·D4 로컬 모델(계획서 §2.2·§3.2, G1·G3): 허가 정책 함수와 허가 저장소 하위 세션의 실행·가림 표.
/// 클라우드 모델 = 운영 민감 열·리터럴 가림·운영 탭 글 거부 유지, 로컬 모델 = 모두 보임.
/// 기대값은 스위치에서 계산한다 — 스위치 둘을 false 로 되돌려도 이 시험은 그대로 통한다(§3.2 되돌리기).
/// </summary>
public class AgentGrantProductionTests
{
    private static readonly Guid DevId = Guid.NewGuid();
    private static readonly Guid TestId = Guid.NewGuid();
    private static readonly Guid PrdId = Guid.NewGuid();

    private static readonly bool Runs = AgentGrantPolicy.ProductionRuns;
    private static readonly bool Covers = AgentGrantPolicy.LocalModelCoversProduction;

    private static ConnectionProfile Dev() =>
        new("dev-erp", "u", "h/s", Id: DevId, AiAccess: AiAccessLevel.QueryRows);

    private static ConnectionProfile Test() =>
        new("test-erp", "u", "h/s", Id: TestId, Environment: ConnectionEnvironment.Test, AiAccess: AiAccessLevel.QueryRows);

    private static ConnectionProfile Prd(AiAccessLevel? level = null) =>
        new("prod-erp", "u", "h/s", Id: PrdId, Environment: ConnectionEnvironment.Production, AiAccess: level);

    private static AgentGrantStore Store()
    {
        var store = new AgentGrantStore(new ManualTimeProvider());
        store.AttachWriter(new NoFiles());
        return store;
    }

    private static AgentGrantView Grant(AgentGrantStore store, bool localModel) =>
        store.Create(new AgentGrantRequest(
            [.. new[] { Dev(), Test(), Prd() }.Select(p => new AgentGrantTarget(p, AgentGrantPolicy.DefaultCap(p.Environment)))],
            AgentGrantPolicy.DefaultLifetime,
            LocalModel: localModel)).Grant;

    [Fact]
    public void Switches_are_on_for_phase_two()
    {
        // D5 확정(사용자 2026-11-22 22:58). 끄면 아래 표는 스위치 값을 따라 그대로 통한다.
        Assert.True(Runs);
        Assert.True(Covers);
    }

    [Theory]
    [InlineData(ConnectionEnvironment.Development, AiAccessLevel.QueryRows)]
    [InlineData(ConnectionEnvironment.Test, AiAccessLevel.QueryRows)]
    [InlineData(ConnectionEnvironment.Production, AiAccessLevel.QueryRows)]
    public void Environment_cap_lets_production_reach_its_own_maximum_when_runs_are_on(ConnectionEnvironment environment, AiAccessLevel onCap)
    {
        var expected = environment == ConnectionEnvironment.Production && !Runs ? AiAccessLevel.Metadata : onCap;
        Assert.Equal(expected, AgentGrantPolicy.EnvironmentCap(environment));
        Assert.Equal(expected, AgentGrantPolicy.DefaultCap(environment));
    }

    [Fact]
    public void Production_grant_level_is_query_at_most_because_the_connection_maximum_is_query()
    {
        // 운영 외부 상한이 QueryRows 여도 접속 최대가 Query 라 행 값 수준(QueryRows)은 되지 않는다.
        Assert.Equal(Runs ? AiAccessLevel.Query : AiAccessLevel.Metadata, AgentGrantPolicy.LevelFor(Prd(), AgentGrantPolicy.DefaultCap(ConnectionEnvironment.Production)));
        Assert.Equal(AiAccessLevel.Plan, AgentGrantPolicy.LevelFor(Prd(AiAccessLevel.Plan), AiAccessLevel.QueryRows));
        Assert.Equal(AiAccessLevel.Off, AgentGrantPolicy.LevelFor(Prd(AiAccessLevel.Off), AiAccessLevel.QueryRows));
    }

    [Fact]
    public void May_execute_needs_query_level_and_production_runs()
    {
        var prd = new AgentGrantConnection(PrdId, "prod-erp", ConnectionEnvironment.Production, Dbms.Oracle, AiAccessLevel.Query);
        var dev = prd with { Environment = ConnectionEnvironment.Development, Alias = "dev-erp" };

        Assert.Equal(Runs, AgentGrantPolicy.MayExecute(prd));
        Assert.True(AgentGrantPolicy.MayExecute(dev));
        Assert.False(AgentGrantPolicy.MayExecute(prd with { Level = AiAccessLevel.Plan }));
        Assert.False(AgentGrantPolicy.MayExecute(dev with { Level = AiAccessLevel.Plan }));
        Assert.Equal(Runs, AgentGrantPolicy.ExecutesIn(ConnectionEnvironment.Production));
    }

    /// <summary>가림 표: (로컬 모델, 환경) → 로컬 AI·민감 열·리터럴·탭 글.</summary>
    [Theory]
    [InlineData(false, ConnectionEnvironment.Development)]
    [InlineData(false, ConnectionEnvironment.Test)]
    [InlineData(false, ConnectionEnvironment.Production)]
    [InlineData(true, ConnectionEnvironment.Development)]
    [InlineData(true, ConnectionEnvironment.Test)]
    [InlineData(true, ConnectionEnvironment.Production)]
    public void Masking_table_cloud_model_masks_production_and_local_model_shows_everything(bool localModel, ConnectionEnvironment environment)
    {
        var grant = Grant(Store(), localModel);
        var production = environment == ConnectionEnvironment.Production;
        var id = environment switch
        {
            ConnectionEnvironment.Development => DevId,
            ConnectionEnvironment.Test => TestId,
            _ => PrdId,
        };
        var local = localModel && (!production || Covers);

        Assert.Equal(local, AgentGrantPolicy.UsesLocalModel(grant, environment));
        Assert.Equal(!local, AgentGrantPolicy.MasksSensitive(grant, environment));
        Assert.Equal(!local, grant.MasksSensitive(id));
        Assert.Equal(production && !local, AgentGrantPolicy.MasksLiterals(grant, environment));
        Assert.Equal(production && !local, grant.MasksLiterals(id));

        // E-18·E-32: 클라우드 모델은 운영 탭 글을 받지 않는다. 개발·시험 탭 글은 언제나 읽는다.
        Assert.Equal(!production || local, grant.CanReadTabText(id));
    }

    [Fact]
    public void Cloud_model_production_sub_session_is_not_local_and_masks_literals()
    {
        var store = Store();
        var grant = Grant(store, localModel: false);

        var prd = store.SessionFor(grant.GrantId, PrdId, "codex")!;
        Assert.False(prd.Context.LocalAi);
        Assert.NotNull(prd.Run.Literals);
        Assert.True(prd.Run.MaskLiteralsRequired);
        Assert.Equal(prd.Level, prd.DataLevel); // 행 값 수준이 늘지 않는다(운영 Query = 행 값 없음)

        var dev = store.SessionFor(grant.GrantId, DevId, "codex")!;
        Assert.False(dev.Context.LocalAi);
        Assert.Null(dev.Run.Literals);
        Assert.False(dev.Run.MaskLiteralsRequired);
    }

    [Fact]
    public void Local_model_sub_sessions_are_local_and_production_follows_the_cover_switch()
    {
        var store = Store();
        var grant = Grant(store, localModel: true);

        var dev = store.SessionFor(grant.GrantId, DevId, "opencode")!;
        Assert.True(dev.Context.LocalAi);
        Assert.Null(dev.Run.Literals);

        var prd = store.SessionFor(grant.GrantId, PrdId, "opencode")!;
        Assert.Equal(Covers, prd.Context.LocalAi);
        Assert.Equal(!Covers, prd.Run.MaskLiteralsRequired);
        Assert.Equal(!Covers, prd.Run.Literals is not null);
        if (prd.Level != AiAccessLevel.Off)
        {
            // 로컬 모델이 운영을 덮으면 값 수준은 행 값까지(AiAccessPolicy.DataLevel), 도구 수준은 그대로.
            Assert.Equal(Covers ? AiAccessLevel.QueryRows : prd.Level, prd.DataLevel);
        }
    }

    [Fact]
    public void Local_model_is_carried_from_the_request_to_the_view_and_through_extend()
    {
        var store = Store();
        var grant = Grant(store, localModel: true);

        Assert.True(grant.LocalModel);
        Assert.True(store.Extend(grant.GrantId, TimeSpan.FromHours(1))!.LocalModel);
        Assert.True(store.AddClient(grant.GrantId, "opencode")!.LocalModel);
        Assert.False(Grant(store, localModel: false).LocalModel);
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
