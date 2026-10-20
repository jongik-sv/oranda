using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.TableSpec;
using Oranda.App.Tests.Features.SchemaBrowser;
using Oranda.App.Tests.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;
using Oranda.Core.Transfer;

namespace Oranda.App.Tests.Features.TableSpec;

/// <summary>TSK-13-06 테이블 정의서: 문서 모델 만들기·화면 모델·명령 등록.</summary>
public class TableSpecTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private static readonly DbObjectRef Dept = new("SCOTT", "DEPT", "TABLE");

    private static SchemaBrowserTests.TestMeta NewMeta()
    {
        var meta = new SchemaBrowserTests.TestMeta();
        meta.Objects.AddRange([Emp, Dept, new DbObjectRef("SCOTT", "V_EMP", "VIEW"), new DbObjectRef("HR", "JOBS", "TABLE")]);
        meta.Tables[Dept] = new TableInfo(Dept, [new ColumnInfo("DEPTNO", 1, "NUMBER") { Precision = 2, IsNullable = false }])
        {
            Constraints = [new ConstraintInfo("PK_DEPT", ConstraintKind.PrimaryKey, ["DEPTNO"])],
            Comment = "부서",
        };
        meta.Tables[Emp] = new TableInfo(Emp,
        [
            new ColumnInfo("ENAME", 2, "VARCHAR2") { Length = 10, DefaultValue = " 'X' ", Comment = "이름" },
            new ColumnInfo("EMPNO", 1, "NUMBER") { Precision = 4, IsNullable = false },
            new ColumnInfo("SAL", 3, "NUMBER") { Precision = 7, Scale = 2 },
            new ColumnInfo("DEPTNO", 4, "NUMBER") { Precision = 2 },
        ])
        {
            Constraints =
            [
                new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["EMPNO"]),
                new ConstraintInfo("FK_DEPT", ConstraintKind.ForeignKey, ["DEPTNO"]) { ReferencedTable = Dept, ReferencedConstraint = "PK_DEPT" },
            ],
            Indexes = [new IndexInfo("Z_IDX", false, ["ENAME"]), new IndexInfo("PK_EMP", true, ["EMPNO"])],
            Comment = "사원",
        };
        return meta;
    }

    [Fact]
    public async Task Builder_maps_columns_keys_references_and_indexes()
    {
        var doc = await TableSpecBuilder.BuildAsync(NewMeta(), new SchemaBrowserTests.TestSession(), "T", [Emp], null, default);

        var t = Assert.Single(doc.Tables);
        Assert.Equal("사원", t.Comment);
        Assert.Equal(["EMPNO", "ENAME", "SAL", "DEPTNO"], t.Columns.Select(c => c.Name)); // 위치 순서
        Assert.Equal([1, 2, 3, 4], t.Columns.Select(c => c.No));
        Assert.Equal(["NUMBER(4)", "VARCHAR2(10)", "NUMBER(7,2)", "NUMBER(2)"], t.Columns.Select(c => c.DataType));
        Assert.Equal([false, true, true, true], t.Columns.Select(c => c.IsNullable));
        Assert.Equal([true, false, false, false], t.Columns.Select(c => c.IsPrimaryKey));
        Assert.Equal("DEPT.DEPTNO", t.Columns[3].References);
        Assert.Null(t.Columns[0].References);
        Assert.Equal("'X'", t.Columns[1].DefaultValue);
        Assert.Equal("이름", t.Columns[1].Comment);
        Assert.Equal(["PK_EMP", "Z_IDX"], t.Indexes.Select(i => i.Name));
        Assert.True(t.Indexes[0].IsUnique);
    }

    [Fact]
    public async Task Builder_falls_back_to_the_table_name_when_the_parent_cannot_be_read()
    {
        var meta = NewMeta();
        meta.Tables.Remove(Dept); // 부모 표 조회가 실패한다

        var doc = await TableSpecBuilder.BuildAsync(meta, new SchemaBrowserTests.TestSession(), "T", [Emp], null, default);

        Assert.Equal("DEPT", doc.Tables[0].Columns.Single(c => c.Name == "DEPTNO").References);
    }

    [Fact]
    public async Task View_model_with_a_table_targets_only_that_table_and_writes_the_chosen_format()
    {
        var writer = new FakeWriter();
        var vm = new TableSpecViewModel(NewMeta(), writer);
        vm.Attach(new SchemaBrowserTests.TestSession(), Emp);
        vm.Format = TableSpecViewModel.Formats[1];
        var path = Path.Combine(Path.GetTempPath(), "oranda-tablespec-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            Assert.Equal("SCOTT_EMP_테이블정의서.md", vm.SuggestedFileName);
            Assert.True(await vm.GenerateAsync(path));

            Assert.Equal(TableSpecFormat.Markdown, writer.Format);
            Assert.Equal(["EMP"], writer.Document!.Tables.Select(t => t.Name));
            Assert.Equal("SCOTT.EMP 테이블 정의서", writer.Document.Title);
            Assert.Equal("written", File.ReadAllText(path));
            Assert.Contains(path, vm.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task View_model_with_a_pattern_picks_tables_of_the_owner_and_ignores_views()
    {
        var vm = new TableSpecViewModel(NewMeta(), new FakeWriter());
        vm.Attach(new SchemaBrowserTests.TestSession());

        Assert.Equal("SCOTT", vm.Owner);
        Assert.Equal(["DEPT", "EMP"], (await vm.ResolveTargetsAsync()).Select(t => t.Name));
        vm.NameText = "emp";
        Assert.Equal(["EMP"], (await vm.ResolveTargetsAsync()).Select(t => t.Name));
        vm.Owner = "HR";
        vm.NameText = string.Empty;
        Assert.Equal(["JOBS"], (await vm.ResolveTargetsAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task Editing_the_name_releases_the_single_table_lock()
    {
        var vm = new TableSpecViewModel(NewMeta(), new FakeWriter());
        vm.Attach(new SchemaBrowserTests.TestSession(), Emp);

        vm.NameText = "D";

        Assert.Equal(["DEPT"], (await vm.ResolveTargetsAsync()).Select(t => t.Name));
    }

    [Theory]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", null, null, null, "INTERVAL DAY(2) TO SECOND(6)")]
    [InlineData("RAW", 16, null, null, "RAW(16)")]
    [InlineData("NUMBER", null, 10, 0, "NUMBER(10)")]
    [InlineData("DATE", 7, null, null, "DATE")]
    public void Type_text_adds_length_or_precision_only_where_it_belongs(string type, int? length, int? precision, int? scale, string expected) =>
        Assert.Equal(expected, TableSpecBuilder.FormatType(new ColumnInfo("C", 1, type) { Length = length, Precision = precision, Scale = scale }));

    [Fact]
    public async Task Changing_the_owner_releases_the_single_table_lock_and_a_failed_write_keeps_the_old_file()
    {
        var vm = new TableSpecViewModel(NewMeta(), new FakeWriter { Fail = true });
        vm.Attach(new SchemaBrowserTests.TestSession(), Emp);
        vm.Owner = "HR";
        Assert.Empty(await vm.ResolveTargetsAsync()); // 옛 SCOTT.EMP 고정이 아니라 HR 에서 EMP 패턴을 찾는다

        vm.Attach(new SchemaBrowserTests.TestSession(), Emp);
        var path = Path.Combine(Path.GetTempPath(), "oranda-tablespec-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, "old");
        try
        {
            Assert.False(await vm.GenerateAsync(path));
            Assert.Equal("old", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Nothing_to_write_reports_it_and_does_not_create_a_file()
    {
        var vm = new TableSpecViewModel(NewMeta(), new FakeWriter());
        vm.Attach(new SchemaBrowserTests.TestSession());
        vm.NameText = "NOPE";
        var path = Path.Combine(Path.GetTempPath(), "oranda-tablespec-" + Guid.NewGuid().ToString("N") + ".xlsx");

        Assert.False(await vm.GenerateAsync(path));

        Assert.Equal("조건에 맞는 표가 없다", vm.StatusText);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Table_context_command_opens_the_panel_for_that_table_and_tools_command_for_the_owner()
    {
        var h = new SchemaBrowserTests.Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var scott = h.Vm.Roots[1];
        var tables = scott.Children.First(c => c.CategoryType == "TABLE");
        await h.Vm.ExpandAsync(tables);
        var emp = tables.Children.First(c => c.Title == "EMP");
        await h.Vm.ExpandAsync(emp);
        var views = scott.Children.First(c => c.CategoryType == "VIEW");
        await h.Vm.ExpandAsync(views);

        var opened = new List<(TableSpecViewModel Vm, string Title)>();
        var handler = new TableSpecCommandHandler
        {
            SessionProvider = () => h.Session,
            OpenDocument = (vm, title) =>
            {
                opened.Add((vm, title));
                return true;
            },
        };
        await handler.ExecuteAsync(new CommandContext("oranda.schema.tableSpec", FakeContextKeys.AllTrue, emp.Children[0]), default);
        await handler.ExecuteAsync(new CommandContext("oranda.schema.tableSpec", FakeContextKeys.AllTrue, views.Children[0]), default);
        await handler.ExecuteAsync(new CommandContext("oranda.tools.tableSpec", FakeContextKeys.AllTrue), default);

        Assert.Equal(3, opened.Count); // 뷰 노드도 정의서 대상(tableNodeSelected 와 같은 범위)
        Assert.Equal("EMP", opened[0].Vm.NameText);
        Assert.Equal("SCOTT", opened[0].Vm.Owner);
        Assert.Equal("테이블 정의서 EMP", opened[0].Title);
        Assert.Equal("V_EMP", opened[1].Vm.NameText);
        Assert.Equal(string.Empty, opened[2].Vm.NameText);
        Assert.Equal("SCOTT", opened[2].Vm.Owner);
    }

    [Fact]
    public void Panel_and_commands_are_registered()
    {
        Assert.Equal(DockLocation.Document, ShellTestHelpers.RequirePanel("oranda.panel.tableSpec").DefaultLocation);
        var reg = ShellTestHelpers.CreateCommandRegistry();
        Assert.NotNull(reg.Find("oranda.schema.tableSpec"));
        Assert.NotNull(reg.Find("oranda.tools.tableSpec"));
    }

    private sealed class FakeWriter : ITableSpecWriter
    {
        public TableSpecDocument? Document { get; private set; }

        public TableSpecFormat Format { get; private set; }

        public bool Fail { get; init; }

        public string Extension(TableSpecFormat format) => ".x";

        public async Task WriteAsync(TableSpecDocument document, TableSpecFormat format, Stream destination, CancellationToken ct)
        {
            Document = document;
            Format = format;
            if (Fail)
            {
                await destination.WriteAsync("half"u8.ToArray(), ct);
                throw new IOException("disk full");
            }

            await destination.WriteAsync("written"u8.ToArray(), ct);
        }
    }
}
