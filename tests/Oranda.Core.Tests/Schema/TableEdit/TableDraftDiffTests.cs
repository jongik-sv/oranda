using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>차이 계산(설계 8.1 줄 키, 8.4 정규화, 화면 표시용 상태·칸).</summary>
public sealed class TableDraftDiffTests
{
    [Fact]
    public void Untouched_draft_has_no_changes()
    {
        var (original, edited) = Start();

        var changes = TableDraftDiff.Compute(original, edited, Dialect);

        Assert.True(changes.IsEmpty);
        Assert.Equal(DraftRowState.Unchanged, changes.StateOf(original.KeyOf("NOTE")));
    }

    [Fact]
    public void Row_states_and_changed_cells()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { Name = "MEMO", Length = 300, Comment = "x" })
            .WithColumn("STATUS", c => c with { IsDropped = true })
            .AddColumn(new ColumnDraft("n1", "CODE") { DataType = "DATE" });

        var changes = TableDraftDiff.Compute(original, edited, Dialect);

        Assert.Equal(DraftRowState.Modified, changes.StateOf(original.KeyOf("NOTE")));
        Assert.Equal(ColumnChange.Name | ColumnChange.Type | ColumnChange.Comment, changes.Columns.Single(d => d.RowKey == original.KeyOf("NOTE")).Changes);
        Assert.Equal(DraftRowState.Dropped, changes.StateOf(original.KeyOf("STATUS")));
        Assert.Equal(DraftRowState.Added, changes.StateOf("n1"));
        Assert.Equal(3, changes.CountFor(TableEditPage.Columns));
    }

    [Fact]
    public void A_new_row_marked_dropped_is_no_change()
    {
        var (original, edited) = Start();
        edited = edited.AddColumn(new ColumnDraft("n1", "CODE") { DataType = "DATE", IsDropped = true });

        Assert.True(TableDraftDiff.Compute(original, edited, Dialect).IsEmpty);
    }

    [Fact]
    public void Constraints_and_indexes_on_a_dropped_column_are_dropped_too()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("CREATED", c => c with { IsDropped = true });

        var changes = TableDraftDiff.Compute(original, edited, Dialect);

        Assert.Equal(DraftRowState.Dropped, changes.StateOf(original.Constraints.Single(c => c.Name == "SYS_C0013").RowKey));
        Assert.Equal(DraftRowState.Dropped, changes.StateOf(original.Indexes.Single(i => i.Name == "IX_ORDERS_01").RowKey));
        Assert.Equal(1, changes.CountFor(TableEditPage.Constraints));
        Assert.Equal(1, changes.CountFor(TableEditPage.Indexes));
    }

    [Fact]
    public void Virtual_column_without_a_type_does_not_compare_the_type()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("TOTAL", c => c with { DataType = null });

        Assert.True(TableDraftDiff.Compute(original, edited, Dialect).IsEmpty);
    }

    [Fact]
    public void Missing_length_semantics_on_an_existing_column_keeps_the_original()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("CUSTOMER_NAME", c => c with { CharSemantics = null });

        Assert.True(TableDraftDiff.Compute(original, edited, Dialect).IsEmpty);
    }

    [Fact]
    public void Changing_length_semantics_is_a_type_change()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { CharSemantics = CharSemantics.Characters });

        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE VARCHAR2(200 CHAR))"], Build(original, edited));
    }

    [Fact]
    public void Constraint_and_index_changes_are_classified()
    {
        var (original, edited) = Start();
        edited = edited
            .WithConstraint("CK_ORDERS_01", c => c with { IsEnabled = false, Name = "CK_X" })
            .WithConstraint("FK_ORDERS_01", c => c with { DeleteRule = DeleteRule.Cascade })
            .WithIndex("IX_ORDERS_01", i => i with { Name = "IX_X" })
            .WithIndex("IX_ORDERS_02", i => i with { IsUnique = true });

        var changes = TableDraftDiff.Compute(original, edited, Dialect);

        Assert.Equal(ConstraintChange.Name | ConstraintChange.Enabled, changes.Constraints.Single(d => d.Original!.Name == "CK_ORDERS_01").Changes);
        Assert.Equal(ConstraintChange.Definition, changes.Constraints.Single(d => d.Original!.Name == "FK_ORDERS_01").Changes);
        Assert.Equal(IndexChange.Name, changes.Indexes.Single(d => d.Original!.Name == "IX_ORDERS_01").Changes);
        Assert.Equal(IndexChange.Definition, changes.Indexes.Single(d => d.Original!.Name == "IX_ORDERS_02").Changes);
    }

    [Fact]
    public void Index_tablespace_left_empty_is_not_a_change()
    {
        var (original, edited) = Start();
        edited = edited.WithIndex("IX_ORDERS_01", i => i with { Tablespace = null });

        Assert.True(TableDraftDiff.Compute(original, edited, Dialect).IsEmpty);
    }

    [Fact]
    public void Comment_change_counts_on_the_description_page()
    {
        var (original, edited) = Start();
        edited = edited with { Comment = "다른 주석" };

        var changes = TableDraftDiff.Compute(original, edited, Dialect);

        Assert.True(changes.CommentChanged);
        Assert.Equal(1, changes.CountFor(TableEditPage.Description));
    }
}
