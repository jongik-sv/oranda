using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Composition;

// 소유: tb-core C7(D-046). 디스패처가 세션·프로필의 DBMS 로 키 등록 구현을 골라 넘긴다.

public class DbmsDispatcherTests
{
    private static readonly SqlStatement Select = new("SELECT 1 FROM dual", default, StatementKind.Query);

    private static FakeSession OracleSession() => new(TestData.Profile());

    private static FakeSession TiberoSession() => new(TestData.Profile() with { Dbms = Dbms.Tibero });

    [Fact]
    public async Task Execution_dispatcher_forwards_to_the_implementation_of_the_session_dbms()
    {
        var oracle = new FakeExecutionService();
        var tibero = new FakeExecutionService();
        var table = new DbmsServiceTable()
            .Add<IExecutionService>(Dbms.Oracle, () => oracle)
            .Add<IExecutionService>(Dbms.Tibero, () => tibero);
        var dispatcher = new ExecutionServiceDispatcher(table);

        var onTibero = TiberoSession();
        await dispatcher.ExecuteAsync(onTibero, Select, new Dictionary<string, BindValue>(), null, default);
        await foreach (var _ in dispatcher.ExecuteScriptAsync(OracleSession(), [Select, Select], ScriptErrorMode.Stop, null, default))
        {
        }

        Assert.Same(onTibero, Assert.Single(tibero.Calls).Session);
        Assert.Equal(2, oracle.Calls.Count);
    }

    [Fact]
    public async Task Missing_dbms_implementation_throws_dbms_not_supported()
    {
        var table = new DbmsServiceTable().Add<ILobAccessor>(Dbms.Oracle, () => new MarkerLob(1));
        var dispatcher = new LobAccessorDispatcher(table);
        var lob = new LobValue(LobKind.Blob, 1);

        await using var stream = await dispatcher.OpenReadAsync(OracleSession(), lob, default);
        Assert.Equal(1, stream.ReadByte());

        var ex = await Assert.ThrowsAsync<DbmsNotSupportedException>(() => dispatcher.OpenReadAsync(TiberoSession(), lob, default));
        Assert.Equal(Dbms.Tibero, ex.Dbms);
        Assert.Equal(typeof(ILobAccessor), ex.ServiceType);
    }

    [Fact]
    public async Task Session_manager_opens_by_profile_dbms_and_lists_sessions_of_opened_providers_oracle_first()
    {
        var oracle = new FakeSessionManager();
        var tibero = new FakeSessionManager();
        var loads = 0;
        var table = new DbmsServiceTable(() => loads++)
            .Add<ISessionManager>(Dbms.Oracle, () => oracle)
            .Add<ISessionManager>(Dbms.Tibero, () => tibero);
        var dispatcher = new SessionManagerDispatcher(table);

        // 아무것도 열지 않았으면 고르기를 묻지 않는다(지연 어셈블리를 깨우지 않는다).
        Assert.Empty(dispatcher.Sessions);
        Assert.Equal(0, loads);

        using var credential = new SecureCredential("pw");
        var t = await dispatcher.OpenAsync(TestData.Profile() with { Dbms = Dbms.Tibero }, credential, SessionPurpose.Work, default);
        var o = await dispatcher.OpenAsync(TestData.Profile(), credential, SessionPurpose.Work, default);

        Assert.Same(t, Assert.Single(tibero.Sessions));
        Assert.Same(o, Assert.Single(oracle.Sessions));
        Assert.Equal([o, t], dispatcher.Sessions);
    }

    [Fact]
    public async Task Session_manager_rejects_a_dbms_without_provider_as_argument_exception_and_keeps_replaced_managers_sessions()
    {
        var first = new FakeSessionManager();
        var second = new FakeSessionManager();
        var table = new DbmsServiceTable().Add<ISessionManager>(Dbms.Oracle, () => first);
        var dispatcher = new SessionManagerDispatcher(table);
        using var credential = new SecureCredential("pw");

        // 예전 OracleSessionManager 가 Oracle 아닌 프로필을 거부하던 형식(접속 화면이 「접속 실패」로 받는다)
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => dispatcher.OpenAsync(TestData.Profile() with { Dbms = Dbms.Tibero }, credential, SessionPurpose.Work, default));
        Assert.IsType<DbmsNotSupportedException>(ex.InnerException);

        var a = await dispatcher.OpenAsync(TestData.Profile(), credential, SessionPurpose.Work, default);
        table.Add<ISessionManager>(Dbms.Oracle, () => second); // 같은 DBMS 의 관리자가 바뀜
        var b = await dispatcher.OpenAsync(TestData.Profile(), credential, SessionPurpose.Work, default);

        Assert.Equal([a, b], dispatcher.Sessions);
    }

    [Fact]
    public async Task Streaming_dispatch_selects_the_implementation_when_enumeration_starts()
    {
        var dispatcher = new ExecutionServiceDispatcher(new DbmsServiceTable().Add<IExecutionService>(Dbms.Oracle, () => new FakeExecutionService()));

        // Oracle 구현(async iterator)처럼 고르기 예외도 await foreach 안에서 난다
        var stream = dispatcher.ExecuteScriptAsync(TiberoSession(), [Select], ScriptErrorMode.Stop, null, default);
        await Assert.ThrowsAsync<DbmsNotSupportedException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });
    }

    [Fact]
    public async Task Copy_dispatch_uses_the_shared_dbms_and_rejects_mixed_dbms()
    {
        var precheck = new CountingPrecheck();
        var table = new DbmsServiceTable().Add<ICopyPrecheckService>(Dbms.Oracle, () => precheck);
        var dispatcher = new CopyPrecheckServiceDispatcher(table);

        await dispatcher.RunAsync(new CopySessions(OracleSession(), OracleSession()), null!, null!, null, default);
        Assert.Equal(1, precheck.Runs);

        var mixed = new CopySessions(OracleSession(), TiberoSession());
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => dispatcher.RunAsync(mixed, null!, null!, null, default));
        Assert.IsNotType<DbmsNotSupportedException>(ex);
        Assert.Equal(1, precheck.Runs);
    }

    private sealed class MarkerLob(byte marker) : ILobAccessor
    {
        public Task<Stream> OpenReadAsync(ISession s, LobValue lob, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream([marker]));
    }

    private sealed class CountingPrecheck : ICopyPrecheckService
    {
        public int Runs { get; private set; }

        public Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period, IProgress<string>? progress, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(new PrecheckReport([], [], [], new Dictionary<string, long>()));
        }
    }
}
