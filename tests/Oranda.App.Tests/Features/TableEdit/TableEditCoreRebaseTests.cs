using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 중간 실패 뒤 남은 편집 다시 계산(TE-5)을 실제 Core 빌더·<see cref="TableDraftRebase"/> 로: 이름 바꾸기만 됨, 아무것도 안 됨,
/// 같은 이름 지우고 넣기(지우기만 됨·둘 다 됨), 제약 정의 변경의 DROP 만 됨. 화면(App)이 쓰는 경로와 같다.
/// </summary>
public class TableEditCoreRebaseTests
{
    private static readonly CoreTableEditEngine Engine = CoreTableEditEngine.Instance;
    private static readonly OracleRules Dialect = new();

    private static TableDraft Read(TableInfo table) => Engine.FromTable(table, Dialect);

    /// <summary>실행 → 앞 <paramref name="applied"/> 문장만 성공 → 다시 읽은 <paramref name="after"/> 위의 남은 문장.</summary>
    private static (TableEditScript Ran, TableEditScript Remaining) Run(TableDraft original, TableDraft edited, int applied, TableInfo after)
    {
        var ran = Engine.Build(original, edited, Dialect);
        var newOriginal = Read(after);
        var rebased = TableDraftRebase.Rebase(original, edited, ran, applied, newOriginal);
        return (ran, Engine.Build(newOriginal, rebased, Dialect));
    }

    private static TableDraft WithColumns(TableDraft d, Func<ColumnDraft, ColumnDraft> change, params ColumnDraft[] add) =>
        d with { Columns = [.. d.Columns.Select(change), .. add] };

    private static string Texts(TableEditScript s) => string.Join(" | ", s.Statements.Select(x => x.Statement.Text));

    [Fact]
    public void Applied_rename_and_failed_comment_leave_only_the_comment()
    {
        var original = Read(OrdersTable());
        var edited = WithColumns(original, c => c.Name == "STATUS" ? c with { Name = "STATE", Comment = "새 상태" } : c);
        var after = OrdersTable() with
        {
            Columns = [.. OrdersTable().Columns.Select(c => c.Name == "STATUS" ? c with { Name = "STATE" } : c)],
            Constraints = [.. OrdersTable().Constraints.Select(k => k with { Columns = [.. k.Columns.Select(c => c == "STATUS" ? "STATE" : c)] })],
        };

        var (ran, remaining) = Run(original, edited, 1, after);

        Assert.Equal(2, ran.Statements.Count);
        var only = Assert.Single(remaining.Statements);
        Assert.StartsWith("COMMENT ON COLUMN", only.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("STATE", only.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_applied_keeps_every_statement()
    {
        var original = Read(OrdersTable());
        var edited = WithColumns(original, c => c.Name == "OLD_FLAG" ? c with { IsDropped = true } : c,
            new ColumnDraft(DraftKeys.New(), "TOTAL_VAT") { DataType = "NUMBER" });

        var (ran, remaining) = Run(original, edited, 0, OrdersTable());

        Assert.Equal(Texts(ran), Texts(remaining));
    }

    [Fact]
    public void Drop_and_readd_same_name_after_the_drop_leaves_only_the_add()
    {
        var original = Read(OrdersTable());
        var edited = WithColumns(original, c => c.Name == "OLD_FLAG" ? c with { IsDropped = true } : c,
            new ColumnDraft(DraftKeys.New(), "OLD_FLAG") { DataType = "NUMBER", Precision = 1 });
        var after = OrdersTable() with { Columns = [.. OrdersTable().Columns.Where(c => c.Name != "OLD_FLAG")] };

        var (ran, remaining) = Run(original, edited, 1, after);

        Assert.Contains("DROP COLUMN", ran.Statements[0].Statement.Text, StringComparison.Ordinal); // 같은 이름은 지우기가 먼저(설계 8.3)
        var only = Assert.Single(remaining.Statements);
        Assert.Contains("ADD", only.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("OLD_FLAG", only.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Drop_and_readd_both_applied_leave_only_the_failed_table_comment()
    {
        var original = Read(OrdersTable());
        var edited = WithColumns(original, c => c.Name == "OLD_FLAG" ? c with { IsDropped = true } : c,
            new ColumnDraft(DraftKeys.New(), "OLD_FLAG") { DataType = "NUMBER", Precision = 1 }) with { Comment = "다음 주석" };
        var after = OrdersTable() with
        {
            Columns = [.. OrdersTable().Columns.Where(c => c.Name != "OLD_FLAG"), new ColumnInfo("OLD_FLAG", 4, "NUMBER") { Precision = 1 }],
        };

        var (ran, remaining) = Run(original, edited, 2, after);

        Assert.Equal(3, ran.Statements.Count);
        var only = Assert.Single(remaining.Statements); // 다시 넣은 열이 지울 줄로 엇갈리지 않는다
        Assert.StartsWith("COMMENT ON TABLE", only.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Constraint_drop_applied_but_add_failed_leaves_the_add()
    {
        var original = Read(OrdersTable());
        var ck = original.Constraints.Single(c => c.Name == "SYS_C0012345");
        var edited = original with
        {
            Constraints = [.. original.Constraints.Select(c => c.RowKey == ck.RowKey ? c with { Condition = "STATUS IN ('A','B','C')" } : c)],
        };
        var after = OrdersTable() with { Constraints = [.. OrdersTable().Constraints.Where(c => c.Name != "SYS_C0012345")] };

        var (ran, remaining) = Run(original, edited, 1, after);

        Assert.Contains("DROP CONSTRAINT", ran.Statements[0].Statement.Text, StringComparison.Ordinal);
        var only = Assert.Single(remaining.Statements);
        Assert.Contains("ADD CONSTRAINT", only.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("'C'", only.Statement.Text, StringComparison.Ordinal);
    }

    /// <summary>Core 기본 규칙(Oracle)만 쓰는 방언.</summary>
    private sealed class OracleRules : ITableEditDialect
    {
        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "CHAR"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;
    }
}
