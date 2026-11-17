using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>화면이 실제 Core 엔진(T1b)과 붙었을 때: 안 고치면 0문장, 고치면 그 문장이 미리보기 = 실행 문장, 중간 실패 뒤 Core 다시 계산.</summary>
public class TableEditCoreEngineTests
{
    private static TableEditSession Create()
    {
        var dialect = new CoreDefaultsDialect();
        var engine = CoreTableEditEngine.Instance;
        return new TableEditSession(engine.FromTable(OrdersTable(), dialect), dialect, new TableEditContext(128, ConnectionEnvironment.Development), engine);
    }

    [Fact]
    public void Untouched_edit_produces_no_statements_and_passing_through_cells_changes_nothing()
    {
        var edit = Create();
        Assert.Equal(4, edit.Columns.Count);
        foreach (var row in edit.Columns)
        {
            row.Name = row.Name;
            row.DataType = row.DataType;
            row.LengthText = row.LengthText;
            row.DefaultValue = row.DefaultValue;
            row.Comment = row.Comment;
        }

        Assert.False(edit.IsDirty);
        Assert.True(edit.Script.IsEmpty);
        Assert.Null(edit.ScriptError);
    }

    [Fact]
    public void A_shorter_length_becomes_one_modify_shown_and_run_as_the_same_statement()
    {
        var edit = Create();
        edit.Columns.Single(c => c.Name == "STATUS").LengthText = "20";

        var statement = Assert.Single(edit.Script.Statements);
        Assert.Null(edit.ScriptError);
        Assert.StartsWith("ALTER TABLE SAMPLE.ORDERS MODIFY", statement.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("STATUS", statement.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(StatementKind.Ddl, statement.Statement.Kind);
        Assert.Equal(statement.Statement.Text + ";", edit.PreviewText);
        Assert.Same(statement.Statement, edit.Script.SqlStatements[0]);
    }

    [Fact]
    public void Typed_size_in_the_type_cell_goes_to_the_length_cell()
    {
        var edit = Create();
        var row = edit.AddColumn()!;
        row.Name = "NOTE";
        row.DataType = "varchar2(30 char)";

        Assert.Equal("VARCHAR2", row.Column.DataType);
        Assert.Equal(30, row.Column.Length);
        Assert.Equal(CharSemantics.Characters, row.Column.CharSemantics);
        row.DataType = "number(10,2)";
        Assert.Equal(("NUMBER", 10, 2), (row.Column.DataType, row.Column.Precision, row.Column.Scale));
        row.DataType = "timestamp(6)";
        Assert.Equal("TIMESTAMP(6)", row.Column.DataType); // 사전이 이름에 붙이는 괄호는 그대로
    }

    /// <summary>Core 기본 규칙(Oracle)만 쓰는 방언.</summary>
    private sealed class CoreDefaultsDialect : ITableEditDialect
    {
        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "DATE"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;
    }
}
