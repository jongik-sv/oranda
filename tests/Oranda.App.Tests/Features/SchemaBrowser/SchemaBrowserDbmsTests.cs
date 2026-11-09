using System.Reflection;
using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.SchemaBrowser;

// 소유: Tibero U2. 스키마 트리·메타데이터 구현이 없는 DBMS 의 접속은 자동으로 읽지 않고, 패널 안내줄에 이유를 보인다.
public class SchemaBrowserDbmsTests
{
    private sealed class Rig
    {
        public Rig(Dbms dbms, DbmsServiceTable table)
        {
            Session = new DbmsSession(dbms);
            Meta.Schemas.Add("SCOTT");
            Vm = new SchemaBrowserViewModel(new Provider(this, table)) { PostToUi = a => a(), DebounceMilliseconds = 3_600_000 };
        }

        public DbmsSession Session { get; }

        public EventBus Bus { get; } = new();

        public SchemaBrowserTests.TestManager Manager { get; } = new();

        public SchemaBrowserTests.TestMeta Meta { get; } = new();

        public SchemaBrowserViewModel Vm { get; }

        public void Open()
        {
            Manager.Sessions.Add(Session);
            Bus.Publish(new SessionOpened(Session.Id, Session.Profile, Session.Server, SessionPurpose.Work));
        }
    }

    private sealed class Provider(Rig rig, IDbmsServiceSelector selector) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMetadataProvider) ? rig.Meta
            : serviceType == typeof(IEventBus) ? rig.Bus
            : serviceType == typeof(ISessionManager) ? rig.Manager
            : serviceType == typeof(IDbmsServiceSelector) ? selector
            : null;
    }

    private sealed class DbmsSession(Dbms dbms) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "SCOTT", "localhost/FREEPDB1", Id: Guid.NewGuid(), Dbms: dbms);

        public ServerInfo Server { get; } = new("23.26.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false));

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    private static DbmsServiceTable TiberoMetadata() => new DbmsServiceTable()
        .Add(typeof(IMetadataProvider), Dbms.Tibero, () => DispatchProxy.Create<IMetadataProvider, NullProxy>())
        .Add(typeof(ISchemaTreeProvider), Dbms.Tibero, () => DispatchProxy.Create<ISchemaTreeProvider, NullProxy>());

    [Fact]
    public async Task Session_of_a_dbms_without_metadata_service_is_not_read_and_the_panel_shows_the_reason()
    {
        var rig = new Rig(Dbms.Tibero, new DbmsServiceTable());

        await rig.Vm.SetSessionAsync(rig.Session);

        Assert.Empty(rig.Vm.Roots);
        Assert.Equal(0, rig.Meta.ListObjectsCalls);
        Assert.Equal(0, rig.Meta.CommentCalls);
        Assert.Contains("Tibero", rig.Vm.StatusText);
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact]
    public async Task Opening_a_session_of_such_a_dbms_does_not_read_either()
    {
        var rig = new Rig(Dbms.Tibero, new DbmsServiceTable());

        rig.Open();
        await WaitForAsync(() => rig.Vm.StatusText.Contains("Tibero", StringComparison.Ordinal));

        Assert.Same(rig.Session, rig.Vm.CurrentSession);
        Assert.Empty(rig.Vm.Roots);
        Assert.Equal(0, rig.Meta.ListObjectsCalls);
    }

    [Fact]
    public async Task Dbms_with_metadata_services_loads_the_tree_as_before()
    {
        var rig = new Rig(Dbms.Tibero, TiberoMetadata());

        await rig.Vm.SetSessionAsync(rig.Session);

        Assert.Equal(["즐겨찾기", "SCOTT"], rig.Vm.Roots.Select(r => r.Title));
        Assert.True(rig.Meta.ListObjectsCalls > 0);
    }

    [Fact]
    public async Task Oracle_loads_even_when_the_selector_has_no_entries()
    {
        var rig = new Rig(Dbms.Oracle, new DbmsServiceTable());

        await rig.Vm.SetSessionAsync(rig.Session);

        Assert.Equal(["즐겨찾기", "SCOTT"], rig.Vm.Roots.Select(r => r.Title));
        Assert.True(rig.Meta.ListObjectsCalls > 0);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
