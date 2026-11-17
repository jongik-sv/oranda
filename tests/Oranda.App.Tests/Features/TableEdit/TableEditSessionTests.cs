using Oranda.App.Features.TableEdit;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 편집 세션·편집 표 줄(설계 7.2~7.4, T6): 줄 상태 색, 바뀐 칸, 지우기 토글·되살리기, 되돌리기, 새 열만 옮기기, 이름 규칙, 칸 입력 오류, PK 하나, 제약 인덱스 흐림.
/// </summary>
public class TableEditSessionTests
{
    [Fact]
    public void Changed_cells_are_yellow_new_rows_green_and_dropped_rows_red()
    {
        var edit = CreateSession();
        var status = edit.Columns.Single(c => c.Name == "STATUS");
        var flag = edit.Columns.Single(c => c.Name == "OLD_FLAG");

        status.LengthText = "20";
        edit.ToggleDrop(flag.Key);
        var added = (ColumnRowViewModel)edit.AddRow(TableEditPage.Columns)!;
        added.Name = "TOTAL_VAT";

        Assert.True(status.IsModified);
        Assert.True(status.IsTypeChanged);
        Assert.False(status.IsNameChanged);
        Assert.Equal("✎", status.StateMark);
        Assert.True(flag.IsDropped);
        Assert.False(flag.CanEditName); // 지울 줄은 칸을 고치지 않는다
        Assert.Equal("−", flag.StateMark);
        Assert.True(added.IsAdded);
        Assert.Equal("+", added.StateMark);
        Assert.Same(added, edit.SelectedColumn); // 새 줄을 고른다
        Assert.Equal(3, edit.CountFor(TableEditPage.Columns));
        Assert.Equal("변경 3건", edit.SummaryText);
    }

    [Fact]
    public void Drop_toggles_and_new_rows_are_removed_instead()
    {
        var edit = CreateSession();
        var flag = edit.Columns.Single(c => c.Name == "OLD_FLAG");

        edit.ToggleDrop(flag.Key);
        Assert.True(flag.IsDropped);
        edit.ToggleDrop(flag.Key); // 다시 누르면 되살린다
        Assert.False(flag.IsDropped);
        Assert.False(edit.IsDirty);

        var added = edit.AddColumn()!;
        Assert.Equal(5, edit.Columns.Count);
        edit.ToggleDrop(added.Key);
        Assert.Equal(4, edit.Columns.Count); // 아직 없는 줄은 뺀다
        Assert.False(edit.IsDirty);
    }

    [Fact]
    public void Revert_restores_one_row_and_revert_all_returns_to_zero_statements()
    {
        var edit = CreateSession();
        var id = edit.Columns[0];
        id.Comment = "다른 주석";
        id.Name = "ORDER_NO";
        edit.Columns[1].IsNullable = true;
        Assert.Equal(2, edit.Script.Statements.Count);

        edit.Revert(id.Key);
        Assert.Equal("ORDER_ID", id.Name);
        Assert.Equal("주문 번호", id.Comment);
        Assert.Single(edit.Script.Statements);

        edit.RevertAll();
        Assert.True(edit.Script.IsEmpty);
        Assert.False(edit.IsDirty);
    }

    [Fact]
    public void Only_new_columns_move_and_never_above_existing_ones()
    {
        var edit = CreateSession();
        var a = edit.AddColumn()!;
        a.Name = "A";
        var b = edit.AddColumn()!;
        b.Name = "B";

        Assert.False(edit.CanMoveColumn(edit.Columns[0].Key, 1)); // 기존 열(TE-1: 기존 열 순서는 「열 순서 바꾸기…」)
        Assert.False(edit.CanMoveColumn(a.Key, -1)); // 기존 열 위로 못 감
        Assert.True(edit.CanMoveColumn(b.Key, -1));

        edit.SelectedColumn = b;
        Assert.True(edit.CanMoveUp);
        Assert.False(edit.CanMoveDown);
        edit.MoveSelected(-1);

        Assert.Equal(["ORDER_ID", "CUSTOMER_ID", "STATUS", "OLD_FLAG", "B", "A"], edit.Columns.Select(c => c.Column.Name));
        Assert.Same(b, edit.SelectedColumn);
    }

    [Fact]
    public void New_constraints_and_indexes_get_rule_names_and_pk_is_single()
    {
        var edit = CreateSession();
        Assert.False(edit.CanAddPrimaryKey); // PK_ORDERS 가 있다
        Assert.Null(edit.AddConstraint(ConstraintKind.PrimaryKey));

        var uk = edit.AddConstraint(ConstraintKind.Unique)!;
        var fk = edit.AddConstraint(ConstraintKind.ForeignKey)!;
        var fk2 = edit.AddConstraint(ConstraintKind.ForeignKey)!;
        var ix = edit.AddIndex()!;

        Assert.Equal("UK_ORDERS_01", uk.Name);
        Assert.Equal("FK_ORDERS_01", fk.Name);
        Assert.Equal("FK_ORDERS_02", fk2.Name);
        Assert.Equal("IX_ORDERS_02", ix.Name); // IX_ORDERS_01 이 이미 있다
        Assert.True(uk.Constraint.IsAutoName);

        uk.Name = "uk_orders_status";
        Assert.False(uk.Constraint.IsAutoName); // 사용자가 고친 이름은 따라 바뀌지 않는다
        Assert.Equal("UK_ORDERS_STATUS", uk.Constraint.Name);

        edit.ToggleDrop(edit.Constraints.Single(c => c.Name == "PK_ORDERS").Key);
        Assert.True(edit.CanAddPrimaryKey);
    }

    [Fact]
    public void Table_name_change_renames_rule_names_only()
    {
        var edit = new TableEditSession(new TableDraft("SAMPLE", string.Empty) { IsNewTable = true }, new FakeDialect(),
            new TableEditContext(30, Oranda.Core.Connection.ConnectionEnvironment.Development), new FakeTableEditEngine());
        var pk = edit.AddConstraint(ConstraintKind.PrimaryKey)!;
        var ck = edit.AddConstraint(ConstraintKind.Check)!;
        ck.Name = "MY_CHECK";
        var ix = edit.AddIndex()!;
        Assert.Equal("PK_NEW_TABLE", pk.Name);

        edit.SetTableName("ORDER_ITEMS");

        Assert.Equal(["PK_ORDER_ITEMS", "MY_CHECK"], edit.Constraints.Select(c => c.Constraint.Name));
        Assert.Equal("IX_ORDER_ITEMS_01", Assert.Single(edit.Indexes).Name);
        Assert.Same(ix, edit.Indexes[0]);
        Assert.True(edit.IsNewTable);
        Assert.False(edit.IsRestricted);
    }

    [Fact]
    public void Constraint_columns_and_reference_parse_from_text_and_report_unknown_names()
    {
        var edit = CreateSession();
        var fk = edit.AddConstraint(ConstraintKind.ForeignKey)!;

        fk.ColumnsText = "customer_id, NOPE";
        Assert.True(fk.HasError);
        Assert.Contains("NOPE", fk.Error, StringComparison.Ordinal);
        Assert.Equal("customer_id, NOPE", fk.ColumnsText); // 입력은 남긴다
        Assert.Empty(fk.Constraint.ColumnKeys); // 모델은 그대로

        fk.ColumnsText = "customer_id";
        Assert.False(fk.HasError);
        Assert.Equal(["c2"], fk.Constraint.ColumnKeys);

        fk.ReferenceText = "customers (customer_id)";
        Assert.Equal(new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"), fk.Constraint.ReferencedTable); // 소유자 생략 = 이 테이블 소유자
        Assert.Equal(["CUSTOMER_ID"], fk.Constraint.ReferencedColumns);
        Assert.Equal("SAMPLE.CUSTOMERS (CUSTOMER_ID)", fk.ReferenceText);

        fk.DeleteRuleIndex = 2;
        Assert.Equal(DeleteRule.SetNull, fk.Constraint.DeleteRule);

        fk.ReferenceText = "(";
        Assert.True(fk.HasError);
    }

    [Fact]
    public void Index_columns_parse_desc_and_constraint_indexes_are_locked()
    {
        var edit = CreateSession();
        var pkIndex = edit.Indexes.Single(i => i.Name == "PK_ORDERS");
        var ix = edit.Indexes.Single(i => i.Name == "IX_ORDERS_01");

        Assert.True(pkIndex.IsConstraintIndex);
        Assert.False(pkIndex.CanEditCells);
        Assert.Contains("제약 페이지", pkIndex.LockedReason, StringComparison.Ordinal);
        edit.ToggleDrop(pkIndex.Key);
        Assert.False(pkIndex.IsDropped); // 제약 인덱스는 지우지 않는다

        ix.ColumnsText = "customer_id, status desc";
        Assert.Equal("CUSTOMER_ID, STATUS DESC", ix.ColumnsText);
        Assert.True(ix.IsDefinitionChanged);
        Assert.Equal([false, true], ix.Index.Columns.Select(c => c.Descending));
    }

    [Fact]
    public void Server_named_constraints_are_flagged()
    {
        var edit = CreateSession();
        Assert.True(edit.Constraints.Single(c => c.Name == "SYS_C0012345").IsServerName); // TE-8
        Assert.False(edit.Constraints.Single(c => c.Name == "PK_ORDERS").IsServerName);
    }

    [Fact]
    public void Bad_numbers_keep_the_model_and_show_an_error()
    {
        var edit = CreateSession();
        var status = edit.Columns.Single(c => c.Name == "STATUS");

        status.LengthText = "abc";

        Assert.True(status.HasError);
        Assert.False(edit.IsDirty);
        Assert.Equal("50", status.LengthText);
        Assert.Equal(string.Empty, status.ScaleText); // 문자 형식은 소수 칸이 없다
        Assert.False(status.CanEditScale);

        var id = edit.Columns[0];
        Assert.True(id.IsNumeric);
        Assert.Equal("10", id.LengthText); // 숫자는 정밀도
        id.ScaleText = "2";
        Assert.Equal(2, id.Column.Scale);
    }

    [Fact]
    public void Virtual_expression_cell_is_for_new_or_virtual_columns_only()
    {
        var edit = CreateSession();
        Assert.False(edit.Columns[0].CanEditVirtual); // 일반 열을 가상 열로 바꾸는 문장은 없다

        var v = edit.AddColumn()!;
        Assert.True(v.CanEditVirtual);
        v.VirtualExpression = "ORDER_ID * 2";
        Assert.True(v.IsVirtual);
        Assert.False(v.CanEditLength); // 가상 열은 길이·NULL·기본값 흐림
        Assert.False(v.CanEditDefault);
        Assert.True(v.CanEditType);
    }

    [Fact]
    public void Builder_failure_blocks_the_script_and_keeps_dirty_state()
    {
        var edit = new TableEditSession(TableEditTestSupport.CreateSession().Original, new FakeDialect(),
            new TableEditContext(128, Oranda.Core.Connection.ConnectionEnvironment.Development), new ThrowingEngine());
        Assert.False(edit.IsDirty);

        edit.TableComment = "x";

        Assert.NotNull(edit.ScriptError);
        Assert.True(edit.IsDirty); // 버림 확인은 그대로 묻는다
        Assert.True(edit.Script.IsEmpty);
        Assert.StartsWith("-- ", edit.PreviewText, StringComparison.Ordinal);
    }

    private sealed class ThrowingEngine : ITableEditEngine
    {
        private readonly FakeTableEditEngine _inner = new();

        public TableDraft FromTable(TableInfo table, ITableEditDialect dialect) => _inner.FromTable(table, dialect);

        public TableDraft Empty(string owner) => _inner.Empty(owner);

        public TableDraftChanges Diff(TableDraft original, TableDraft edited, ITableEditDialect dialect) =>
            original == edited ? new TableDraftChanges() : throw new NotImplementedException("T1b");

        public TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect) => throw new NotImplementedException("T1b");

        public TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options) => TableEditCheckPlan.Empty;

        public Task<IReadOnlyList<CheckFinding>> RunChecksAsync(
            TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, Oranda.Core.Sessions.ISession session, TableEditCheckOptions options, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CheckFinding>>([]);
    }
}
