using Oranda.App.Features.DbTools;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.DbTools;

// 소유: Tibero U2. DB 정보 구현이 없는 DBMS 의 세션은 DB 정보 제공자를 부르지 않고 패널 상태줄에 이유를 보인다.
public class DbInfoDbmsTests
{
    private sealed class CountingInfo : IDatabaseInfoProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DetailSection>>([new DetailSection("인스턴스", ["이름"], [["X"]])]);
        }
    }

    private sealed class Provider(CountingInfo info, IDbmsServiceSelector selector) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IDatabaseInfoProvider) ? info
            : serviceType == typeof(IDbmsServiceSelector) ? selector
            : null;
    }

    private sealed class DbmsSession(Dbms dbms) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev", "scott", "h:1521/s", Dbms: dbms);

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Dbms_without_database_info_service_is_not_queried_and_the_panel_shows_the_reason()
    {
        var info = new CountingInfo();
        var vm = new DbInfoViewModel(new Provider(info, new DbmsServiceTable()));

        await vm.LoadAsync(new DbmsSession(Dbms.Tibero));

        Assert.Equal(0, info.Calls);
        Assert.Empty(vm.Sections);
        Assert.Contains("Tibero", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Oracle_is_queried_as_before_even_when_the_selector_has_no_entries()
    {
        var info = new CountingInfo();
        var vm = new DbInfoViewModel(new Provider(info, new DbmsServiceTable()));

        await vm.LoadAsync(new DbmsSession(Dbms.Oracle));

        Assert.Equal(1, info.Calls);
        Assert.Single(vm.Sections);
    }
}
