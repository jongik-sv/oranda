using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class CopyCommonStatementsTests
{
    [Fact]
    public void Drop_created_table_purges_and_asks_for_the_target_name_to_be_retyped()
    {
        var drop = CopyCommonStatements.DropCreatedTable("DST", "ORDERS");

        Assert.Equal("DROP TABLE \"DST\".\"ORDERS\" PURGE", drop.Text);
        Assert.DoesNotContain("CASCADE", drop.Text, StringComparison.Ordinal); // 작업 안 FK 는 적재 뒤에 붙이므로 참조하는 FK 가 없다
        Assert.Equal(StatementKind.Ddl, drop.Kind);
        Assert.Equal("DROP", drop.Verb);
        Assert.Equal("ORDERS", drop.DangerousTarget);
        Assert.Equal(new ChangeTarget("DST", "ORDERS", "TABLE", ChangeOperation.Drop), drop.ChangeTarget);
    }

    [Fact]
    public void Drop_created_table_quotes_unusual_names()
    {
        Assert.Equal("DROP TABLE \"my owner\".\"a\"\"b\" PURGE", CopyCommonStatements.DropCreatedTable("my owner", "a\"b").Text);
    }
}
