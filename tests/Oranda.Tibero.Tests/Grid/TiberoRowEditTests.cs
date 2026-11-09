using System.Data;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Grid;

/// <summary>
/// 결과 편집(R4): 편집 계획·DML 생성은 DBMS 무관(문장 해석 기반)이다. 여기서는 Tibero 가 주는 모양의 결과 열
/// (ROWID 열 <see cref="ColumnDataKind.RowId"/>, 출처 <c>BaseTable</c>·<c>BaseColumn</c> 없음)로 계획이 맞게 나오는지 확인한다.
/// 드라이버 스키마 표의 출처(<c>BaseTableName</c> = FROM 첫 표, <c>BaseColumnName</c> = 별칭)는 믿을 수 없어 열 목록에 옮기지 않는다
/// (docs/design/tibero-driver-values.md 5장) — 계획이 그 값에 속지 않는다는 것이 이 시험의 핵심이다.
/// </summary>
public class TiberoRowEditTests
{
    private static readonly DbObjectRef Table = new("TIBERO", "T", "TABLE");

    private static readonly TableKeyInfo NoKey = new(Table, TableKeyKind.None, []);

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query);

    /// <summary>드라이버 스키마 표 모양. <c>BaseTableName</c> 은 모든 열에 FROM 첫 표, <c>BaseColumnName</c> 은 열 이름(별칭).</summary>
    private static IReadOnlyList<ResultColumn> Columns(string firstTable, params (string Name, int ProviderType, bool RowId)[] cols)
    {
        var t = new DataTable();
        t.Columns.Add("ProviderType", typeof(int));
        t.Columns.Add("IsRowID", typeof(bool));
        t.Columns.Add("BaseTableName", typeof(string));
        t.Columns.Add("BaseColumnName", typeof(string));
        foreach (var c in cols)
        {
            t.Rows.Add(c.ProviderType, c.RowId, firstTable, c.Name);
        }

        return TiberoColumns.Read(t, cols.Length, i => cols[i].Name);
    }

    [Fact]
    public void Result_columns_never_carry_the_untrusted_driver_source()
    {
        var cols = Columns("A", ("ID", 107, false), ("X", 126, false));

        Assert.All(cols, c =>
        {
            Assert.Null(c.BaseOwner);
            Assert.Null(c.BaseTable);
            Assert.Null(c.BaseColumn);
        });
    }

    [Fact]
    public void Rowid_column_in_the_select_list_is_the_row_key()
    {
        var cols = Columns("T", ("ROWID", 126, true), ("ID", 107, false), ("NAME", 126, false));

        var plan = RowEditPlanner.Plan(Query("SELECT t.ROWID, t.ID, t.NAME FROM TIBERO.T t"), cols, NoKey);

        Assert.Equal(ColumnDataKind.RowId, cols[0].Kind);
        Assert.True(plan.Capability.CanEdit);
        Assert.True(plan.UsesRowId);
        Assert.Equal([0], plan.Capability.KeyColumnOrdinals);
        Assert.Equal([1, 2], plan.Capability.EditableColumnOrdinals);
        Assert.Equal("ROWID", plan.ColumnNames[0]);
    }

    [Fact]
    public void Primary_key_in_the_result_is_preferred_over_no_rowid()
    {
        var cols = Columns("T", ("ID", 107, false), ("NAME", 126, false));
        var keys = new TableKeyInfo(Table, TableKeyKind.PrimaryKey, ["ID"]);

        var plan = RowEditPlanner.Plan(Query("SELECT ID, NAME FROM TIBERO.T"), cols, keys);

        Assert.True(plan.Capability.CanEdit);
        Assert.False(plan.UsesRowId);
        Assert.Equal([0], plan.Capability.KeyColumnOrdinals);
    }

    [Fact]
    public void Without_a_key_the_user_is_offered_to_add_rowid()
    {
        var cols = Columns("T", ("ID", 107, false), ("NAME", 126, false));

        var plan = RowEditPlanner.Plan(Query("SELECT * FROM TIBERO.T"), cols, NoKey);

        Assert.False(plan.Capability.CanEdit);
        Assert.Equal(EditBlockReason.NoKeyColumns, plan.Capability.Reason);
        Assert.True(plan.Capability.CanAddRowId);
        Assert.Contains("ROWID", plan.Capability.QueryWithRowId!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Qualified_join_columns_edit_only_the_target_table()
    {
        var cols = Columns("A", ("ROWID", 126, true), ("X", 126, false), ("Y", 126, false));

        var plan = RowEditPlanner.Plan(
            Query("SELECT a.ROWID, a.X, b.Y FROM TIBERO.T a JOIN TIBERO.U b ON a.ID = b.ID"), cols, NoKey);

        Assert.True(plan.Capability.CanEdit);
        Assert.True(plan.IsJoin);
        Assert.Equal([1], plan.Capability.EditableColumnOrdinals);
        Assert.Null(plan.ColumnNames[2]);
    }

    [Fact]
    public void Unqualified_star_over_a_join_is_not_guessed_from_the_driver_source()
    {
        // 드라이버는 두 번째 표 열(Y)에도 BaseTableName = 첫 표(T)를 준다. 그 값을 믿으면 Y 를 T 의 열로 잘못 편집한다.
        var cols = Columns("T", ("ID", 107, false), ("X", 126, false), ("Y", 126, false));
        var keys = new TableKeyInfo(Table, TableKeyKind.PrimaryKey, ["ID"]);

        var plan = RowEditPlanner.Plan(Query("SELECT * FROM TIBERO.T JOIN TIBERO.U ON T.ID = U.ID"), cols, keys);

        Assert.False(plan.Capability.CanEdit);
        Assert.All(plan.ColumnNames, Assert.Null);
    }

    [Fact]
    public void Rowid_update_and_delete_use_the_string_rowid_as_a_varchar2_bind()
    {
        const string rowid = "AAAAtYAAEAAAAfQAAA";
        var update = RowDmlBuilder.Build(new RowChange(RowChangeKind.Update, 0, Table)
        {
            Key = new Dictionary<string, object?> { ["ROWID"] = rowid },
            NewValues = new Dictionary<string, object?> { ["NAME"] = "x" },
        })!;
        var delete = RowDmlBuilder.Build(new RowChange(RowChangeKind.Delete, 1, Table)
        {
            Key = new Dictionary<string, object?> { ["ROWID"] = rowid },
        })!;

        Assert.Contains("WHERE ROWID = :rid", update.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("WHERE ROWID = :rid", delete.Statement.Text, StringComparison.Ordinal);
        var plan = TiberoBindPlan.For(update.Binds["rid"]);
        Assert.Equal(TiberoColumnTypes.PtVarchar2, plan.ProviderType);
        Assert.Equal(rowid, plan.Value);
        Assert.Equal(ParameterDirection.Input, plan.Direction);
    }
}
