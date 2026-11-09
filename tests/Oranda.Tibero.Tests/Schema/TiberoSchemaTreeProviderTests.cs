using Oranda.Core.Catalog;
using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoSchemaTreeProviderTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public async Task Triggers_map_owner_name_status_and_bind_the_table()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoSchemaTreeProvider(Recording(calls, _ => Table(
            ["TRIGGER_NAME", "TRIGGER_TYPE", "TRIGGERING_EVENT", "STATUS", "WHEN_CLAUSE", "OWNER"],
            ["TRG_A", "BEFORE EACH ROW", "INSERT", "ENABLED", null, "HR"],
            ["TRG_B", "AFTER STATEMENT", "UPDATE", "DISABLED", null, null])));
        await using var session = Session();

        var result = await provider.ListTriggersAsync(session, Emp, CancellationToken.None);

        Assert.False(result.Denied);
        Assert.Equal([new TreeTrigger("HR", "TRG_A", "ENABLED"), new TreeTrigger("SCOTT", "TRG_B", "DISABLED")], result.Items);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal("meta.detail_triggers", id);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "EMP" }, binds);
    }

    [Fact]
    public async Task Snapshot_logs_map_owner_and_table()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoSchemaTreeProvider(Recording(calls, _ => Table(["LOG_OWNER", "LOG_TABLE"], ["SCOTT", "MLOG$_EMP"])));
        await using var session = Session();

        var result = await provider.ListSnapshotLogsAsync(session, Emp, CancellationToken.None);

        Assert.Equal([new SnapshotLogRef("SCOTT", "MLOG$_EMP")], result.Items);
        Assert.Equal("meta.mview_log", Assert.Single(calls).Id);
    }

    [Fact]
    public async Task No_access_is_a_denied_folder_and_a_missing_catalog_variant_is_an_empty_one()
    {
        await using var session = Session();

        var denied = await new TiberoSchemaTreeProvider(Throwing(ServerError(-8033, "TBR-8033: Specified schema object was not found.")))
            .ListTriggersAsync(session, Emp, CancellationToken.None);
        var missing = await new TiberoSchemaTreeProvider(Throwing(new CatalogQueryNotFoundException("meta.mview_log", 6)))
            .ListSnapshotLogsAsync(session, Emp, CancellationToken.None);

        Assert.True(denied.Denied);
        Assert.Empty(denied.Items);
        Assert.False(missing.Denied);
        Assert.Empty(missing.Items);
    }

    [Fact]
    public async Task Other_server_errors_are_thrown()
    {
        await using var session = Session();
        var error = ServerError(-10007, "TBR-10007: syntax error");
        var provider = new TiberoSchemaTreeProvider(Throwing(error));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => provider.ListTriggersAsync(session, Emp, CancellationToken.None));
        Assert.Same(error, thrown);
    }
}
