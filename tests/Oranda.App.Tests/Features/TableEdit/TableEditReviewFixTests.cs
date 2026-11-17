using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// T6 리뷰에서 나온 경우: 칸에 쓰고 벗어나기 전에 닫기, 칸 포커스로 줄 고르기, 빌더만 실패할 때 줄 상태 유지, PK 둘 막기,
/// 새 이름이 손 이름과 겹치지 않기, 칸을 지나가기만 하면 거짓 차이 없음, 제한 테이블 주석.
/// </summary>
public class TableEditReviewFixTests
{
    [Fact]
    public Task Typing_in_a_cell_then_closing_still_asks() => HeadlessHost.Run(async () =>
    {
        var (vm, _, probe) = CreateDetail();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1200, Height = 700 };
        w.Show();
        try
        {
            await vm.SetTableEditingAsync(true);
            HeadlessHost.Settle(w);
            var edit = view.FindControl<TableEditView>("TableEditView")!;
            var rows = edit.FindControl<ListBox>("ColumnRows")!;
            var comment = rows.GetVisualDescendants().OfType<TextBox>().Where(t => Avalonia.Automation.AutomationProperties.GetName(t) == "주석").ElementAt(1);

            comment.Focus();
            HeadlessHost.Settle(w);
            Assert.Same(vm.TableEdit!.Columns[1], vm.TableEdit.SelectedColumn); // 칸 포커스 = 그 줄 고름
            comment.Text = "아직 칸 안"; // 칸을 벗어나지 않았다 → 모델에는 아직 없다
            Assert.False(vm.TableEdit.IsDirty);

            var confirm = vm.CloseConfirmation();

            Assert.NotNull(confirm); // 닫기 전에 칸 글을 넣고 본다
            Assert.Equal("아직 칸 안", vm.TableEdit.Columns[1].Comment);
            probe.Answer = false;
            Assert.False(await confirm(CancellationToken.None));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public void Builder_failure_keeps_row_states_and_counts()
    {
        var edit = new TableEditSession(CreateSession().Original, new FakeDialect(),
            new TableEditContext(128, Oranda.Core.Connection.ConnectionEnvironment.Development), new BuildFails());
        var status = edit.Columns.Single(c => c.Name == "STATUS");

        status.LengthText = "20";

        Assert.NotNull(edit.ScriptError);
        Assert.True(status.IsModified); // 차이는 그대로(노랑)
        Assert.Equal(1, edit.CountFor(TableEditPage.Columns));
        Assert.True(edit.Script.IsEmpty);
        Assert.True(edit.IsDirty);
    }

    [Fact]
    public void A_dropped_primary_key_cannot_come_back_while_another_one_exists()
    {
        var edit = CreateSession();
        var old = edit.Constraints.Single(c => c.Name == "PK_ORDERS");
        edit.ToggleDrop(old.Key);
        var fresh = edit.AddConstraint(ConstraintKind.PrimaryKey)!;

        edit.ToggleDrop(old.Key); // 되살리기
        Assert.True(old.IsDropped);
        edit.Revert(old.Key); // 되돌리기
        Assert.True(old.IsDropped);
        Assert.Single(edit.Draft.Constraints, c => c.Kind == ConstraintKind.PrimaryKey && !c.IsDropped);

        edit.ToggleDrop(fresh.Key); // 새 PK 를 빼면 옛 PK 를 되살릴 수 있다
        edit.ToggleDrop(old.Key);
        Assert.False(old.IsDropped);
    }

    [Fact]
    public void Rule_names_do_not_collide_with_hand_written_names_after_a_table_rename()
    {
        var edit = new TableEditSession(new TableDraft("SAMPLE", "T") { IsNewTable = true }, new FakeDialect(),
            new TableEditContext(128, Oranda.Core.Connection.ConnectionEnvironment.Development), new FakeTableEditEngine());
        var auto = edit.AddConstraint(ConstraintKind.Check)!; // CK_T_01
        var hand = edit.AddConstraint(ConstraintKind.Check)!;
        hand.Name = "CK_ITEMS_01"; // 다음 테이블 이름의 규칙 이름을 손으로 먼저 씀

        edit.SetTableName("ITEMS");

        Assert.Equal("CK_ITEMS_02", auto.Constraint.Name);
        Assert.Equal("CK_ITEMS_01", hand.Constraint.Name);
    }

    [Fact]
    public void Passing_through_cells_with_server_whitespace_makes_no_change()
    {
        var table = OrdersTable() with
        {
            Columns = [.. OrdersTable().Columns, new ColumnInfo("V", 5, "NUMBER") { IsVirtual = true, DefaultValue = " \"ORDER_ID\"*2\n" }],
            Constraints = [.. OrdersTable().Constraints.Select(k => k.Kind == ConstraintKind.Check ? k with { Condition = "STATUS IN ('A','B') " } : k)],
        };
        var edit = new TableEditSession(new FakeTableEditEngine().FromTable(table, new FakeDialect()), new FakeDialect(),
            new TableEditContext(128, Oranda.Core.Connection.ConnectionEnvironment.Development), new StrictEngine());
        var v = edit.Columns.Single(c => c.Column.Name == "V");
        var ck = edit.Constraints.Single(c => c.IsCheck);

        v.VirtualExpression = v.VirtualExpression; // 칸을 지나가기만 함(LostFocus 가 같은 글을 다시 씀)
        ck.Condition = ck.Condition;
        v.Comment = v.Comment;
        v.Name = v.Name;

        Assert.False(edit.IsDirty);
        Assert.True(edit.Script.IsEmpty);
    }

    [Fact]
    public void Restricted_tables_do_not_change_the_table_comment()
    {
        var edit = CreateSession(TableKind.Iot);
        Assert.False(edit.CanEditTableComment);

        edit.TableComment = "바꿈";
        edit.Columns[0].Name = "X";
        edit.Columns[0].LengthText = "5";

        Assert.False(edit.IsDirty);
    }

    private sealed class BuildFails : ITableEditEngine
    {
        private readonly FakeTableEditEngine _inner = new();

        public TableDraft FromTable(TableInfo table, ITableEditDialect dialect) => _inner.FromTable(table, dialect);

        public TableDraft Empty(string owner) => _inner.Empty(owner);

        public TableDraftChanges Diff(TableDraft original, TableDraft edited, ITableEditDialect dialect) => _inner.Diff(original, edited, dialect);

        public TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect) => throw new FormatException("빌더가 받지 않는 꼴");

        public TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options) => TableEditCheckPlan.Empty;

        public Task<IReadOnlyList<CheckFinding>> RunChecksAsync(
            TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, Oranda.Core.Sessions.ISession session, TableEditCheckOptions options, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CheckFinding>>([]);
    }

    /// <summary>정규화 없이 글자 그대로 비교하는 엔진(공백 하나만 달라도 차이) — 칸이 값을 몰래 바꾸지 않는지 본다.</summary>
    private sealed class StrictEngine : ITableEditEngine
    {
        private readonly FakeTableEditEngine _inner = new();

        public TableDraft FromTable(TableInfo table, ITableEditDialect dialect) => _inner.FromTable(table, dialect);

        public TableDraft Empty(string owner) => _inner.Empty(owner);

        public TableDraftChanges Diff(TableDraft original, TableDraft edited, ITableEditDialect dialect) =>
            original.Columns.SequenceEqual(edited.Columns) && original.Constraints.Count == edited.Constraints.Count
            && original.Constraints.Zip(edited.Constraints).All(p => p.First.Condition == p.Second.Condition && p.First.Name == p.Second.Name)
                ? new TableDraftChanges()
                : new TableDraftChanges { CommentChanged = true };

        public TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect) => _inner.Build(original, edited, dialect);

        public TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options) => TableEditCheckPlan.Empty;

        public Task<IReadOnlyList<CheckFinding>> RunChecksAsync(
            TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, Oranda.Core.Sessions.ISession session, TableEditCheckOptions options, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CheckFinding>>([]);
    }
}
