using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>TSK-13-08 표·뷰 하위 폴더(Columns · Indexes · Constraints · Triggers · Snapshot Log · Recycle Bin) 모델 시험.</summary>
public class SchemaBrowserFolderTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private static readonly DbObjectRef EmpView = new("SCOTT", "V_EMP", "VIEW");
    private static readonly int[] LastThreeFolders = [3, 4, 5];

    private static SchemaBrowserTests.Harness NewHarness()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Tables[EmpView] = new TableInfo(EmpView, [new ColumnInfo("EMPNO", 1, "NUMBER")]);
        h.Meta.Tables[Emp] = h.Meta.Tables[Emp] with
        {
            Indexes = [new IndexInfo("EMP_PK", true, ["EMPNO"]), new IndexInfo("EMP_ENAME", false, ["ENAME", "EMPNO"])],
            Constraints =
            [
                new ConstraintInfo("EMP_PK", ConstraintKind.PrimaryKey, ["EMPNO"]),
                new ConstraintInfo("EMP_DEPT_FK", ConstraintKind.ForeignKey, ["DEPTNO"]) { ReferencedTable = new DbObjectRef("SCOTT", "DEPT", "TABLE") },
            ],
        };
        return h;
    }

    /// <summary>하네스로 트리를 짓고 <paramref name="owner"/> 스키마의 <paramref name="name"/> 노드를 펼쳐 돌려준다(폴더는 펼치지 않는다).</summary>
    private static async Task<SchemaNode> OpenAsync(SchemaBrowserTests.Harness h, string owner, string categoryTitle, string name)
    {
        await h.Vm.SetSessionAsync(h.Session);
        var schema = h.Vm.Roots.First(r => r.Title == owner);
        await h.Vm.ExpandAsync(schema);
        var category = schema.Children.First(c => c.Title.StartsWith(categoryTitle, StringComparison.Ordinal));
        await h.Vm.ExpandAsync(category);
        var node = category.Children.First(c => c.Title == name);
        await h.Vm.ExpandAsync(node);
        return node;
    }

    [Fact]
    public async Task Table_shows_six_folders_in_order_without_reading_anything()
    {
        var h = NewHarness();
        var emp = await OpenAsync(h, "SCOTT", "테이블", "EMP");

        Assert.Equal(["Columns", "Indexes", "Constraints", "Triggers", "Snapshot Log", "Recycle Bin"], emp.Children.Select(c => c.Title));
        Assert.All(emp.Children, c => Assert.Equal(SchemaNodeKind.Folder, c.Kind));
        Assert.Equal(["column", "objIndex", "key", "objTrigger", "objMview", "recycle"], emp.Children.Select(c => c.IconKey));
        Assert.Equal(0, h.Meta.TableCalls); // 폴더를 펼치기 전에는 읽지 않는다
        Assert.All(emp.Children, c => Assert.Equal(SchemaNodeKind.Placeholder, c.Children.Single().Kind));
    }

    [Fact]
    public async Task View_shows_only_columns_and_triggers()
    {
        var view = await OpenAsync(NewHarness(), "SCOTT", "뷰", "V_EMP");

        Assert.Equal(["Columns", "Triggers"], view.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Folder_reads_only_when_expanded_and_columns_indexes_constraints_share_one_table_read()
    {
        var h = NewHarness();
        var emp = await OpenAsync(h, "SCOTT", "테이블", "EMP");

        await h.Vm.ExpandAsync(emp.Children[0]);
        Assert.Equal(1, h.Meta.TableCalls);
        await h.Vm.ExpandAsync(emp.Children[1]);
        await h.Vm.ExpandAsync(emp.Children[2]);
        Assert.Equal(1, h.Meta.TableCalls);

        Assert.Equal(["EMPNO", "ENAME", "HIDDEN"], emp.Children[0].Children.Select(c => c.Title));
        Assert.Equal(["EMP_ENAME", "EMP_PK"], emp.Children[1].Children.Select(c => c.Title));
        Assert.Equal("UNIQUE (EMPNO)", emp.Children[1].Children[1].Detail);
        Assert.Equal(["EMP_DEPT_FK", "EMP_PK"], emp.Children[2].Children.Select(c => c.Title));
        Assert.Equal("FK (DEPTNO) → DEPT", emp.Children[2].Children[0].Detail);

        await h.Vm.ExpandAsync(emp.Children[0]); // 다시 펼쳐도 다시 읽지 않는다
        Assert.Equal(1, h.Meta.TableCalls);
    }

    [Fact]
    public async Task Empty_folders_say_none_and_denied_folders_say_no_permission()
    {
        var h = NewHarness();
        h.Tree = new FakeTree { Triggers = new SchemaFolderResult<TreeTrigger>([], Denied: true) };
        h.Admin = new FakeAdmin([new RecycleBinItem("BIN$1", "OTHER", "TABLE", null, null, true, true, null)]);
        var emp = await OpenAsync(h, "SCOTT", "테이블", "EMP");
        foreach (var i in LastThreeFolders)
        {
            await h.Vm.ExpandAsync(emp.Children[i]);
        }

        Assert.Equal("권한 없음", emp.Children[3].Children.Single().Title);
        Assert.Equal("없음", emp.Children[4].Children.Single().Title);
        Assert.Equal("없음", emp.Children[5].Children.Single().Title); // 휴지통에 다른 표만 있다
        Assert.All(LastThreeFolders, i => Assert.Equal(SchemaNodeKind.Message, emp.Children[i].Children.Single().Kind));
    }

    [Fact]
    public async Task Triggers_snapshot_logs_and_recycle_bin_list_their_items()
    {
        var h = NewHarness();
        h.Tree = new FakeTree
        {
            Triggers = new SchemaFolderResult<TreeTrigger>([new TreeTrigger("SCOTT", "TRG_EMP", "ENABLED"), new TreeTrigger("SCOTT", "TRG_OFF", "DISABLED")]),
            Logs = new SchemaFolderResult<SnapshotLogRef>([new SnapshotLogRef("SCOTT", "MLOG$_EMP")]),
        };
        h.Admin = new FakeAdmin(
        [
            new RecycleBinItem("BIN$A", "EMP", "TABLE", null, new DateTime(2026, 10, 20, 9, 30, 0), true, true, null),
            new RecycleBinItem("BIN$B", "DEPT", "TABLE", null, null, true, true, null),
            new RecycleBinItem("BIN$C", "EMP", "INDEX", null, null, true, true, null), // 같은 이름의 지운 인덱스는 섞이지 않는다
        ]);
        var emp = await OpenAsync(h, "SCOTT", "테이블", "EMP");
        foreach (var i in LastThreeFolders)
        {
            await h.Vm.ExpandAsync(emp.Children[i]);
        }

        var triggers = emp.Children[3].Children;
        Assert.Equal(["TRG_EMP", "TRG_OFF"], triggers.Select(c => c.Title));
        Assert.All(triggers, t => Assert.Equal("TRIGGER", t.Ref?.ObjectType)); // 트리거는 객체로 열린다
        Assert.False(triggers[0].IsInvalid);
        Assert.False(triggers[1].IsInvalid); // 꺼진 트리거는 INVALID 가 아니다
        Assert.Equal("DISABLED", triggers[1].Detail);

        Assert.Equal("MLOG$_EMP", emp.Children[4].Children.Single().Title);
        var bin = emp.Children[5].Children.Single();
        Assert.Equal("BIN$A", bin.Title);
        Assert.Equal("2026-10-20 09:30", bin.Detail);
    }

    [Fact]
    public async Task Recycle_bin_of_another_schema_is_not_readable()
    {
        var h = NewHarness();
        h.Admin = new FakeAdmin([]);
        var jobs = new DbObjectRef("HR", "JOBS", "TABLE");
        h.Meta.Tables[jobs] = new TableInfo(jobs, []);
        var node = await OpenAsync(h, "HR", "테이블", "JOBS");
        await h.Vm.ExpandAsync(node.Children[5]);

        Assert.Equal("권한 없음", node.Children[5].Children.Single().Title);
    }

    [Fact]
    public async Task Index_and_constraint_nodes_open_the_table_and_do_not_carry_a_table_ref()
    {
        var h = NewHarness();
        var emp = await OpenAsync(h, "SCOTT", "테이블", "EMP");
        await h.Vm.ExpandAsync(emp.Children[1]);
        await h.Vm.ExpandAsync(emp.Children[2]);

        var index = emp.Children[1].Children[0];
        var constraint = emp.Children[2].Children[0];
        Assert.Equal(Emp, index.OpenTarget);
        Assert.Equal(Emp, constraint.OpenTarget);
        Assert.Null(index.Ref);
        Assert.Null(constraint.Ref);
        Assert.Null(emp.Children[0].OpenTarget); // 폴더는 열지 않는다
        Assert.True(SchemaBrowserViewModel.IsInsideTable(index));
        Assert.True(SchemaBrowserViewModel.IsInsideTable(emp.Children[3]));
        Assert.False(SchemaBrowserViewModel.IsInsideTable(emp.Parent));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Column_drag_text_is_the_column_name_with_or_without_ctrl_and_shift(bool ctrl, bool shift)
    {
        var h = NewHarness();
        var emp = await OpenAsync(h, "SCOTT", "테이블", "EMP");
        await h.Vm.ExpandAsync(emp.Children[0]);
        var column = emp.Children[0].Children.First(c => c.Title == "EMPNO");

        Assert.Equal("EMPNO", h.Vm.BuildDragText(column, ctrl, shift).Text);
        Assert.Equal("EMPNO", await h.Vm.PrepareDragTextAsync(column, ctrl, shift));
        Assert.Equal("EMPNO", await h.Vm.BuildTextAsync(column, SchemaInsertMode.Columns));
    }

    [Fact]
    public async Task Table_drag_text_is_unchanged_by_the_folders()
    {
        var h = NewHarness();
        var emp = await OpenAsync(h, "SCOTT", "테이블", "EMP");
        await h.Vm.PreloadDragMaterialAsync(emp);

        Assert.Equal("SCOTT.EMP", h.Vm.BuildDragText(emp, ctrl: true, shift: false).Text);
        Assert.Equal("EMPNO, ENAME", h.Vm.BuildDragText(emp, ctrl: false, shift: true).Text);
        Assert.StartsWith("SELECT EMPNO,", h.Vm.BuildDragText(emp, ctrl: false, shift: false).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Constraint_detail_names_the_kind_columns_and_state()
    {
        Assert.Equal("CHECK SAL > 0", SchemaBrowserViewModel.DescribeConstraint(
            new ConstraintInfo("C1", ConstraintKind.Check, ["SAL"]) { Condition = "SAL > 0" }));
        Assert.Equal("UNIQUE (A, B) DISABLED", SchemaBrowserViewModel.DescribeConstraint(
            new ConstraintInfo("U1", ConstraintKind.Unique, ["A", "B"]) { IsEnabled = false }));
    }

    private sealed class FakeTree : ISchemaTreeProvider
    {
        public SchemaFolderResult<TreeTrigger> Triggers { get; init; } = new([]);

        public SchemaFolderResult<SnapshotLogRef> Logs { get; init; } = new([]);

        public Task<SchemaFolderResult<TreeTrigger>> ListTriggersAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(Triggers);

        public Task<SchemaFolderResult<SnapshotLogRef>> ListSnapshotLogsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(Logs);
    }

    private sealed class FakeAdmin(IReadOnlyList<RecycleBinItem> bin) : ISchemaAdminProvider
    {
        public Task<IReadOnlyList<RecycleBinItem>> ListRecycleBinAsync(ISession s, CancellationToken ct) => Task.FromResult(bin);

        public Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<SearchHits<SourceSearchHit>> SearchSourceAsync(ISession s, SourceSearchQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<SearchHits<ColumnSearchHit>> SearchColumnsAsync(ISession s, ColumnSearchQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ObjectReference>> GetReferencesAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
