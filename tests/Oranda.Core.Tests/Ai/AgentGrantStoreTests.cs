using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Testing;

namespace Oranda.Core.Tests.Ai;

/// <summary>외부 허가 저장소(A2): 발급·만료·연장·폐기·하위 세션·접속 설정 변경·연결 파일 쓰기 호출.</summary>
public class AgentGrantStoreTests
{
    private static readonly Guid DevId = Guid.NewGuid();
    private static readonly Guid PrdId = Guid.NewGuid();

    private static ConnectionProfile Dev(AiAccessLevel? level = AiAccessLevel.QueryRows) =>
        new("dev-erp", "u", "h/s", Id: DevId, AiAccess: level);

    private static ConnectionProfile Prd() =>
        new("prd-erp", "u", "h/s", Id: PrdId, Environment: ConnectionEnvironment.Production);

    private static AgentGrantRequest Request(TimeSpan? lifetime = null, params ConnectionProfile[] profiles) =>
        new([.. profiles.Select(p => new AgentGrantTarget(p, AgentGrantPolicy.DefaultCap(p.Environment)))], lifetime ?? AgentGrantPolicy.DefaultLifetime, Label: "기본");

    /// <summary>운영 접속(기본 수준 Query)의 외부 수준: 운영 실행 스위치가 켜져 있으면 Query, 끄면 Metadata 상한.</summary>
    private static AiAccessLevel ProductionLevel => AgentGrantPolicy.ProductionRuns ? AiAccessLevel.Query : AiAccessLevel.Metadata;

    private static (AgentGrantStore Store, FakeWriter Writer, ManualTimeProvider Time) NewStore(IEventBus? events = null)
    {
        var time = new ManualTimeProvider();
        var store = new AgentGrantStore(time, events);
        var writer = new FakeWriter();
        store.AttachWriter(writer);
        return (store, writer, time);
    }

    [Fact]
    public void Create_issues_a_token_publishes_the_file_and_resolves_only_that_token()
    {
        var (store, writer, _) = NewStore();
        var changes = new List<AgentGrantChanged>();
        store.Changed += (_, e) => changes.Add(e);

        var issue = store.Create(Request(null, Dev(), Prd()));

        Assert.Equal(43, issue.Token.Length);
        Assert.Same(issue, Assert.Single(writer.Published));
        Assert.Equal("기본", issue.Grant.Label);
        Assert.Equal(AiAccessLevel.QueryRows, issue.Grant.FindConnection(DevId)!.Level);
        Assert.Equal(ProductionLevel, issue.Grant.FindConnection(PrdId)!.Level); // 운영 상한(D5 스위치)
        Assert.Equal(issue.Grant, store.Resolve(issue.Token));
        Assert.Null(store.Resolve("other"));
        Assert.Null(store.Resolve(string.Empty));
        Assert.Single(store.Active);
        Assert.False(Assert.Single(changes).Ended);
    }

    [Fact]
    public void Create_without_an_open_bridge_fails_and_a_failed_file_write_leaves_no_grant()
    {
        var store = new AgentGrantStore(new ManualTimeProvider());
        Assert.Throws<InvalidOperationException>(() => store.Create(Request(null, Dev())));

        store.AttachWriter(new FakeWriter { FailPublish = true });
        Assert.Throws<IOException>(() => store.Create(Request(null, Dev())));
        Assert.Empty(store.Active);
    }

    [Fact]
    public void Creating_again_revokes_the_old_grant_and_its_file()
    {
        var (store, writer, _) = NewStore();
        var first = store.Create(Request(null, Dev()));
        var oldSession = store.SessionFor(first.Grant.GrantId, DevId, "claude-code")!;

        var second = store.Create(Request(null, Dev()));

        Assert.Null(store.Resolve(first.Token));
        Assert.Equal(second.Grant.GrantId, store.Resolve(second.Token)!.GrantId);
        Assert.True(oldSession.IsRevoked);
        Assert.Equal(first.Grant.GrantId, Assert.Single(writer.Removed).GrantId);
    }

    [Fact]
    public void Expiry_ends_the_grant_and_reports_expired_for_the_old_token()
    {
        var (store, writer, time) = NewStore();
        var changes = new List<AgentGrantChanged>();
        store.Changed += (_, e) => changes.Add(e);
        var issue = store.Create(Request(TimeSpan.FromHours(8), Dev()));
        var session = store.SessionFor(issue.Grant.GrantId, DevId, "codex")!;

        time.Advance(TimeSpan.FromHours(8));

        Assert.True(session.IsRevoked);
        Assert.Single(writer.Removed);
        Assert.True(changes[^1].Ended);
        var lookup = store.Lookup(issue.Token);
        Assert.Null(lookup.Grant);
        Assert.True(lookup.Expired);
        Assert.False(store.Lookup("unknown").Expired);
        Assert.Null(store.Find(issue.Grant.GrantId));
        Assert.Null(store.SessionFor(issue.Grant.GrantId, DevId, "codex"));
    }

    [Fact]
    public void Extend_moves_the_expiry_and_rewrites_the_file()
    {
        var (store, writer, time) = NewStore();
        var issue = store.Create(Request(TimeSpan.FromHours(8), Dev()));
        time.Advance(TimeSpan.FromHours(7));

        var extended = store.Extend(issue.Grant.GrantId, AgentGrantPolicy.DefaultLifetime)!;

        Assert.Equal(time.GetUtcNow() + TimeSpan.FromHours(8), extended.ExpiresAt);
        Assert.Equal(extended, Assert.Single(writer.Updated));
        time.Advance(TimeSpan.FromHours(2)); // 옛 기한(8시간)을 지나도 살아 있다
        Assert.NotNull(store.Resolve(issue.Token));
        Assert.Null(store.Extend("ext-none", TimeSpan.FromHours(1)));
    }

    [Fact]
    public void No_lifetime_means_until_the_app_closes()
    {
        var (store, _, time) = NewStore();
        var issue = store.Create(Request(null, Dev()) with { Lifetime = null });
        time.Advance(TimeSpan.FromDays(30));
        Assert.NotNull(store.Resolve(issue.Token));
        Assert.Null(issue.Grant.ExpiresAt);
    }

    [Fact]
    public void Revoke_ends_sessions_removes_the_file_and_rejects_the_token()
    {
        var (store, writer, _) = NewStore();
        var issue = store.Create(Request(null, Dev()));
        var session = store.SessionFor(issue.Grant.GrantId, DevId, "codex")!;

        store.Revoke(issue.Grant.GrantId);

        Assert.True(session.IsRevoked);
        Assert.Null(store.Resolve(issue.Token));
        Assert.False(store.Lookup(issue.Token).Expired); // 끊긴 것은 만료가 아니다
        Assert.Single(writer.Removed);
        store.Revoke(issue.Grant.GrantId); // 두 번째는 아무 일도 없다
        Assert.Single(writer.Removed);
    }

    [Fact]
    public void Sub_sessions_are_per_connection_and_client_with_all_objects_and_no_statement()
    {
        var (store, _, _) = NewStore();
        var issue = store.Create(Request(null, Dev(), Prd()));
        var id = issue.Grant.GrantId;

        var claude = store.SessionFor(id, DevId, "claude-code")!;
        Assert.Same(claude, store.SessionFor(id, DevId, "claude-code"));
        var codex = store.SessionFor(id, DevId, "codex")!;
        Assert.NotSame(claude, codex); // 감사의 client 칸이 섞이지 않는다
        Assert.Equal("codex", codex.Client);
        Assert.Equal(id + ":dev-erp", claude.RunId);
        Assert.True(claude.Run.External);
        Assert.True(claude.Send.AllObjects);
        Assert.Null(claude.Context.StatementText);
        Assert.Equal(AiAccessLevel.QueryRows, claude.Level);
        Assert.Null(claude.Run.Literals);

        var prod = store.SessionFor(id, PrdId, "codex")!;
        Assert.Equal(ProductionLevel, prod.Level);
        Assert.NotNull(prod.Run.Literals); // 클라우드 모델 운영은 결과의 SQL 글을 가린다
        Assert.True(prod.Run.MaskLiteralsRequired);
        Assert.False(prod.Context.LocalAi);
        Assert.False(claude.Context.LocalAi);

        Assert.Null(store.SessionFor(id, Guid.NewGuid(), "codex"));
        Assert.Null(store.SessionFor("ext-other", DevId, "codex"));

        claude.Revoke();
        Assert.NotSame(claude, store.SessionFor(id, DevId, "claude-code")); // 폐기된 하위 세션은 다시 만든다
    }

    [Fact]
    public void Clients_are_added_once()
    {
        var (store, _, _) = NewStore();
        var issue = store.Create(Request(null, Dev()));
        store.AddClient(issue.Grant.GrantId, "claude-code");
        store.AddClient(issue.Grant.GrantId, "claude-code");
        var view = store.AddClient(issue.Grant.GrantId, "codex")!;
        Assert.Equal(["claude-code", "codex"], view.Clients);
        Assert.Null(store.AddClient("ext-none", "x"));
    }

    [Fact]
    public void Saving_a_connection_recomputes_its_level_and_drops_its_sub_sessions()
    {
        var bus = new EventBus();
        var (store, writer, _) = NewStore(bus);
        var issue = store.Create(Request(null, Dev()));
        var old = store.SessionFor(issue.Grant.GrantId, DevId, "codex")!;

        bus.Publish(new ConnectionProfileSaved(Dev(AiAccessLevel.Metadata)));

        Assert.True(old.IsRevoked);
        Assert.Equal(AiAccessLevel.Metadata, store.Find(issue.Grant.GrantId)!.FindConnection(DevId)!.Level);
        Assert.Equal(AiAccessLevel.Metadata, store.SessionFor(issue.Grant.GrantId, DevId, "codex")!.Level);
        Assert.Single(writer.Updated);
        Assert.NotNull(store.Resolve(issue.Token)); // 허가 자체는 살아 있다

        bus.Publish(new ConnectionProfileSaved(new ConnectionProfile("other", "u", "h/s", Id: Guid.NewGuid())));
        Assert.Single(writer.Updated);
        GC.KeepAlive(store);
    }

    [Fact]
    public void Revoke_all_and_dispose_end_the_grant()
    {
        var (store, writer, _) = NewStore();
        var issue = store.Create(Request(null, Dev()));
        store.Dispose();
        Assert.Null(store.Resolve(issue.Token));
        Assert.Single(writer.Removed);
    }

    private sealed class FakeWriter : IAgentEndpointWriter
    {
        public bool FailPublish { get; init; }

        public List<AgentGrantIssue> Published { get; } = [];

        public List<AgentGrantView> Updated { get; } = [];

        public List<AgentGrantView> Removed { get; } = [];

        public void Publish(AgentGrantIssue issue)
        {
            if (FailPublish)
            {
                throw new IOException("disk full");
            }

            Published.Add(issue);
        }

        public void Update(AgentGrantView grant) => Updated.Add(grant);

        public void Remove(AgentGrantView grant) => Removed.Add(grant);
    }
}
