using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoTableKeyDetailProviderTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public async Task Index_columns_bind_the_index_and_unquote_descending_plain_columns()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoTableKeyDetailProvider(Recording(calls, _ => Table(
            ["COLUMN_POSITION", "COLUMN_NAME", "DESCEND", "COLUMN_EXPRESSION"],
            [1m, "DEPTNO", "ASC", null],
            [2m, "SYS_NC00009$", "DESC", "\"SAL\""],
            [null, "SYS_NC00010$", "ASC", "UPPER(\"ENAME\")"])));
        await using var session = Session();

        var result = await provider.GetIndexColumnsAsync(session, Emp, "EMP_IX", CancellationToken.None);

        Assert.Equal(
            [
                new IndexColumnDetail(1, "DEPTNO", null, false),
                new IndexColumnDetail(2, "SAL", null, true),
                new IndexColumnDetail(3, "SYS_NC00010$", "UPPER(\"ENAME\")", false),
            ],
            result.Items);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal("meta.index_columns", id);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "EMP", ["index_name"] = "EMP_IX" }, binds);
    }

    [Fact]
    public async Task Constraint_columns_carry_the_referenced_key_column()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoTableKeyDetailProvider(Recording(calls, _ => Table(
            ["POSITION", "COLUMN_NAME", "R_OWNER", "R_TABLE_NAME", "R_COLUMN_NAME"],
            [1m, "DEPTNO", "SCOTT", "DEPT", "DEPTNO"])));
        await using var session = Session();

        var result = await provider.GetConstraintColumnsAsync(session, Emp, "FK_DEPTNO", CancellationToken.None);

        var column = Assert.Single(result.Items);
        Assert.Equal((1, "DEPTNO", "SCOTT", "DEPT", "DEPTNO"), (column.Position, column.Column, column.ReferencedOwner, column.ReferencedTable, column.ReferencedColumn));
        Assert.Equal("FK_DEPTNO", Assert.Single(calls).Binds!["constraint_name"]);
    }

    [Fact]
    public async Task No_privilege_is_a_denied_folder()
    {
        await using var session = Session();
        var provider = new TiberoTableKeyDetailProvider(Throwing(ServerError(-8053, "TBR-8053: Not enough privilege.")));

        var result = await provider.GetConstraintColumnsAsync(session, Emp, "PK_EMP", CancellationToken.None);

        Assert.True(result.Denied);
    }
}
