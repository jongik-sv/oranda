using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;

namespace Oranda.Core.Tests.Execution.Providers;

// Oracle·Tibero 대량 실행이 같이 쓰는 글 규칙(BulkStatements). 두 서비스에 따로 있던 같은 코드를 모은 것.
public class BulkStatementsTests
{
    [Theory]
    [InlineData("COMMIT", true)]
    [InlineData("rollback to savepoint x", true)]
    [InlineData("/* c */ SET TRANSACTION READ ONLY", true)]
    [InlineData("-- x\nset transaction isolation level serializable", true)]
    [InlineData("SET ROLE NONE", false)]
    [InlineData("INSERT INTO T VALUES (1)", false)]
    [InlineData("", false)]
    public void Transaction_control_text_is_set_transaction_commit_or_rollback(string text, bool expected) =>
        Assert.Equal(expected, BulkStatements.IsTransactionControlText(text));

    [Fact]
    public void Leading_words_skip_comments_and_open_parens()
    {
        Assert.Equal(("INSERT", "INTO"), BulkStatements.LeadingWords("-- x\n (insert into t"));
        Assert.Equal(("SELECT", null), BulkStatements.LeadingWords("/* a */select"));
        Assert.Equal(("MERGE_X", "INTO"), BulkStatements.LeadingWords("merge_x into"));
        Assert.Equal((null, null), BulkStatements.LeadingWords("  "));
        Assert.Equal((null, null), BulkStatements.LeadingWords("/* 닫히지 않음"));
        Assert.Throws<ArgumentNullException>(() => BulkStatements.LeadingWords(null!));
    }

    [Fact]
    public void Validate_rejects_ddl_column_less_rows_and_wrong_row_width()
    {
        var cols = new[] { new BulkColumn("C1", "NUMBER") };
        var dml = new SqlStatement("INSERT INTO T VALUES (:C1)", default, StatementKind.Dml);

        Assert.Throws<ArgumentException>(() => BulkStatements.Validate(new SqlStatement("CREATE TABLE T (X NUMBER)", default, StatementKind.Ddl), cols, []));
        Assert.Throws<ArgumentException>(() => BulkStatements.Validate(dml, [], [[], []]));
        Assert.Throws<ArgumentException>(() => BulkStatements.Validate(dml, cols, [[1, 2]]));
        BulkStatements.Validate(dml, cols, [[1], [null]]);
    }
}
