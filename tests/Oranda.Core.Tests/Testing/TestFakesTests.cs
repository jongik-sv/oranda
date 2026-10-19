using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Testing;

namespace Oranda.Core.Tests.Testing;

/// <summary>공용 시험 헬퍼(Oranda.Testing)가 계약을 구현하고 기본 동작이 맞는지.</summary>
public class TestFakesTests
{
    private static readonly SqlStatement Select = new("SELECT 1 FROM dual", default(TextRange), StatementKind.Query);

    [Theory]
    [InlineData(typeof(FakeSession), typeof(ISession))]
    [InlineData(typeof(FakeSessionManager), typeof(ISessionManager))]
    [InlineData(typeof(FakeResultSet), typeof(IResultSet))]
    [InlineData(typeof(FakeExecutionService), typeof(IExecutionService))]
    [InlineData(typeof(FakeExecutionInteraction), typeof(IExecutionInteraction))]
    [InlineData(typeof(FakeEventBus), typeof(IEventBus))]
    [InlineData(typeof(FakeMetadataProvider), typeof(IMetadataProvider))]
    [InlineData(typeof(InMemoryCredentialStore), typeof(ICredentialStore))]
    public void Fake_implements_contract(Type fake, Type contract)
    {
        Assert.True(contract.IsAssignableFrom(fake));
    }

    [Fact]
    public async Task Fake_result_set_pages_rows_and_completes()
    {
        await using var rs = new FakeResultSet(
            [new ResultColumn(0, "N", "NUMBER", ColumnDataKind.Number)],
            Enumerable.Range(1, 5).Select(i => (IReadOnlyList<object?>)[i]).ToList(),
            pageSize: 2);

        var first = await rs.GetPageAsync(0, CancellationToken.None);
        Assert.Equal(2, first.RowCount);
        Assert.False(rs.IsComplete);
        Assert.Null(rs.KnownRowCount);

        var last = await rs.GetPageAsync(2, CancellationToken.None);
        Assert.Equal(1, last.RowCount);
        Assert.True(last.IsLast);
        Assert.Equal(5, last.Columns[0][0]);
        Assert.Equal(5, rs.KnownRowCount);
    }

    [Fact]
    public async Task Fake_execution_service_records_calls()
    {
        var service = new FakeExecutionService();
        var session = new FakeSession();

        var result = await service.ExecuteAsync(session, Select, new Dictionary<string, BindValue>(), null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Same(session, Assert.Single(service.Calls).Session);
    }

    [Fact]
    public void Fake_event_bus_records_and_delivers()
    {
        var bus = new FakeEventBus();
        var received = new List<TransactionStateChanged>();
        using (bus.Subscribe<TransactionStateChanged>(this, received.Add))
        {
            bus.Publish(new TransactionStateChanged(Guid.Empty, true));
        }

        bus.Publish(new TransactionStateChanged(Guid.Empty, false));

        Assert.Single(received);
        Assert.Equal(2, bus.PublishedOf<TransactionStateChanged>().Count());
    }

    [Fact]
    public async Task Credential_store_saves_loads_and_deletes()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveAsync("oranda:conn:x", new byte[] { 1, 2 });

        Assert.Equal([1, 2], await store.LoadAsync("oranda:conn:x"));
        await store.DeleteAsync("oranda:conn:x");
        Assert.Null(await store.LoadAsync("oranda:conn:x"));
    }

    [Fact]
    public void Secure_credential_clears_on_dispose()
    {
        var credential = new SecureCredential("tiger");
        Assert.Equal("tiger", credential.Secret.ToString());

        credential.Dispose();

        Assert.Throws<ObjectDisposedException>(() => credential.Secret.Length);
    }
}
