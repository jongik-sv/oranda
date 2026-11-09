using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>스키마 폴더(트리거·스냅샷 로그·키 열)를 실제 서버로 읽는다. 서버·드라이버가 없으면 아무것도 하지 않는다.</summary>
[Trait("Category", "Integration")]
public class TiberoSchemaFolderIt
{
    [TiberoItFact]
    public async Task Folders_of_a_missing_table_are_empty_not_denied()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var table = new DbObjectRef(it.Env.User.ToUpperInvariant(), "ORT_NO_SUCH_TABLE_XYZ", "TABLE");

        var triggers = await new TiberoSchemaTreeProvider().ListTriggersAsync(session, table, CancellationToken.None);
        var logs = await new TiberoSchemaTreeProvider().ListSnapshotLogsAsync(session, table, CancellationToken.None);
        var keys = await new TiberoTableKeyDetailProvider().GetIndexColumnsAsync(session, table, "ORT_NO_SUCH_IX", CancellationToken.None);

        Assert.Equal((false, 0), (triggers.Denied, triggers.Items.Count));
        Assert.Equal((false, 0), (logs.Denied, logs.Items.Count));
        Assert.Equal((false, 0), (keys.Denied, keys.Items.Count));
    }

    [TiberoWriteFact]
    public async Task Primary_key_index_and_constraint_columns_of_a_test_table()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var name = TiberoItSessions.ObjectName("T3B_KEYS");
        var session = await it.OpenAsync();
        await DropIfLeftAsync(session, name); // 지난 실행이 CREATE 와 정리 사이에 끊겼으면 남은 표
        await ExecAsync(session, $"CREATE TABLE {name} (A NUMBER NOT NULL, B VARCHAR2(10) NOT NULL, C NUMBER, CONSTRAINT {name}_PK PRIMARY KEY (B, A))");
        try
        {
            var table = new DbObjectRef(it.Env.User.ToUpperInvariant(), name, "TABLE");
            var index = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, $"SELECT index_name FROM user_indexes WHERE table_name = '{name}'"));
            Assert.NotNull(index);

            var indexColumns = await new TiberoTableKeyDetailProvider().GetIndexColumnsAsync(session, table, index, CancellationToken.None);
            var constraintColumns = await new TiberoTableKeyDetailProvider().GetConstraintColumnsAsync(session, table, name + "_PK", CancellationToken.None);
            var triggers = await new TiberoSchemaTreeProvider().ListTriggersAsync(session, table, CancellationToken.None);

            Assert.Equal([new IndexColumnDetail(1, "B", null, false), new IndexColumnDetail(2, "A", null, false)], indexColumns.Items);
            Assert.Equal([(1, "B"), (2, "A")], constraintColumns.Items.Select(c => (c.Position, c.Column)));
            Assert.Null(constraintColumns.Items[0].ReferencedTable);
            Assert.Empty(triggers.Items);
        }
        finally
        {
            await ExecAsync(session, $"DROP TABLE {name}");
        }
    }

    private static async Task DropIfLeftAsync(TiberoSession session, string name)
    {
        var left = await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{name}'");
        if (TiberoSessionQuery.Long(left) > 0)
        {
            await ExecAsync(session, $"DROP TABLE {name}");
        }
    }

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
