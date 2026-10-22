using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>TSK-04-06 정의로 이동·객체 정보·참조 찾기·뒤로/앞으로 시험.</summary>
public class NavigationTests
{
    private const string PackageBody = """
        create or replace package body emp_pkg as
          procedure hire(p_id number) is
          begin
            null;
          end;

          procedure raise_salary(p_id number) is
          begin
            null;
          end;
        end emp_pkg;
        """;

    [Fact]
    public void Commands_are_registered_with_default_keys_conditions()
    {
        var reg = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in new[] { "goToDefinition", "describeObject", "findReferences", "back", "forward" })
        {
            Assert.NotNull(reg.Find("oranda.nav." + id));
        }
    }

    private static (NavigationService Svc, SqlEditorHost Host, SqlEditorViewModel Tab, FakeMeta Meta, List<DbObjectRef> Ddl) Setup(string sql)
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument(sql);
        tab.ConnectionProfileId = FakeSession.ProfileId;
        var meta = new FakeMeta();
        var ddl = new List<DbObjectRef>();
        var svc = new NavigationService
        {
            Host = host,
            History = new NavigationHistory(),
            Metadata = meta,
            SessionProvider = _ => new FakeSession(),
            OpenDdl = (o, _, _) =>
            {
                ddl.Add(o);
                return Task.CompletedTask;
            },
        };
        return (svc, host, tab, meta, ddl);
    }

    [Fact]
    public async Task Table_name_opens_ddl()
    {
        var (svc, _, tab, _, ddl) = Setup("select * from emp where 1 = 1");
        var r = await svc.GoToDefinitionAsync(tab, 15);
        Assert.Equal(NavigationOutcome.OpenedDdl, r.Outcome);
        Assert.Equal(new DbObjectRef("SCOTT", "EMP", "TABLE"), Assert.Single(ddl));
        Assert.True(svc.History.CanGoBack);
    }

    [Fact]
    public async Task Owner_qualified_table_and_alias_column_open_ddl_of_the_table()
    {
        var (svc, _, tab, _, ddl) = Setup("select e.sal from hr.employees e");
        await svc.GoToDefinitionAsync(tab, tab.Text.IndexOf("sal", StringComparison.Ordinal) + 1);
        await svc.GoToDefinitionAsync(tab, tab.Text.IndexOf("employees", StringComparison.Ordinal) + 1);
        Assert.Equal(["EMPLOYEES", "EMPLOYEES"], ddl.Select(d => d.Name).ToArray());
        Assert.All(ddl, d => Assert.Equal("HR", d.Owner));
    }

    [Fact]
    public async Task Package_procedure_call_opens_body_source_at_the_procedure()
    {
        var (svc, host, tab, _, ddl) = Setup("begin\n  emp_pkg.raise_salary(1);\nend;");
        var r = await svc.GoToDefinitionAsync(tab, tab.Text.IndexOf("raise_salary", StringComparison.Ordinal) + 2);
        Assert.Equal(NavigationOutcome.OpenedSource, r.Outcome);
        Assert.Empty(ddl);
        var doc = host.Active!;
        Assert.NotSame(tab, doc);
        Assert.True(doc.IsReadOnly);
        Assert.Equal("SCOTT.EMP_PKG BODY", doc.Title);
        Assert.Equal("raise_salary", doc.Text.Substring(doc.SelectionStart, doc.SelectionLength));
        Assert.Equal(PackageBody.LastIndexOf("raise_salary", StringComparison.Ordinal), doc.SelectionStart);

        // 같은 곳을 다시 가면 탭을 새로 만들지 않는다.
        host.Active = tab;
        await svc.GoToDefinitionAsync(tab, tab.Text.IndexOf("raise_salary", StringComparison.Ordinal) + 2);
        Assert.Equal(2, host.Tabs.Count);
    }

    [Fact]
    public async Task Local_definitions_jump_inside_the_document_and_back()
    {
        var (svc, _, tab, _, ddl) = Setup("with big as (select 1 x from dual) select * from big b");
        tab.UpdateCaret(5, 5, 0);
        var r = await svc.GoToDefinitionAsync(tab, tab.Text.LastIndexOf("big", StringComparison.Ordinal) + 1);
        Assert.Equal(NavigationOutcome.Local, r.Outcome);
        Assert.Empty(ddl);
        Assert.Equal(5, tab.SelectionStart);
        Assert.Equal(3, tab.SelectionLength);

        Assert.True(svc.Back());
        Assert.Equal(5, tab.CaretOffset);
        Assert.True(svc.History.CanGoForward);
        Assert.True(svc.Forward());
        Assert.Equal(8, tab.CaretOffset);
    }

    [Fact]
    public async Task Not_found_no_symbol_and_no_session_are_reported()
    {
        var (svc, _, tab, _, _) = Setup("select 1 from nothing_here");
        Assert.Equal(NavigationOutcome.NotFound, (await svc.GoToDefinitionAsync(tab, tab.Text.IndexOf("nothing", StringComparison.Ordinal) + 1)).Outcome);
        Assert.Equal(NavigationOutcome.NoSymbol, (await svc.GoToDefinitionAsync(tab, tab.Text.IndexOf('1'))).Outcome);
        svc.SessionProvider = _ => null;
        Assert.Equal(NavigationOutcome.NoSession, (await svc.GoToDefinitionAsync(tab, tab.Text.IndexOf("nothing", StringComparison.Ordinal) + 1)).Outcome);
    }

    [Fact]
    public async Task Describe_lists_columns_types_and_comments_and_marks_the_column()
    {
        var (svc, _, tab, _, _) = Setup("select e.sal from emp e");
        var info = await svc.DescribeAsync(tab, tab.Text.IndexOf("sal", StringComparison.Ordinal) + 1);
        Assert.Equal("SCOTT.EMP", info!.Title);
        Assert.Contains("TABLE — 직원", info.Lines[0]);
        Assert.Contains(info.Lines, l => l.Contains("ENAME  VARCHAR2(10)") && l.Contains("NOT NULL"));
        Assert.Contains(info.Lines, l => l.StartsWith("> ", StringComparison.Ordinal) && l.Contains("SAL  NUMBER(7,2)") && l.Contains("-- 급여"));
    }

    [Fact]
    public async Task Describe_package_member_shows_signature_and_local_shows_definition()
    {
        var (svc, _, tab, _, _) = Setup("declare v_n number;\nbegin\n  v_n := 1;\n  emp_pkg.raise_salary(v_n);\nend;");
        var pkg = await svc.DescribeAsync(tab, tab.Text.IndexOf("raise_salary", StringComparison.Ordinal) + 1);
        Assert.Contains(pkg!.Lines, l => l.StartsWith("PROCEDURE RAISE_SALARY(P_ID NUMBER", StringComparison.Ordinal));
        Assert.DoesNotContain(pkg.Lines, l => l.Contains("HIRE"));

        var local = await svc.DescribeAsync(tab, tab.Text.IndexOf("v_n :=", StringComparison.Ordinal) + 1);
        Assert.Equal("V_N", local!.Title);
        Assert.Contains("변수", local.Lines[0]);
        Assert.Contains("1줄", local.Lines[0]);
    }

    [Fact]
    public void Find_references_selects_next_and_wraps()
    {
        var (svc, _, tab, _, _) = Setup("select e.a, e.b from t e where e.c = 1");
        tab.UpdateCaret(7, 7, 0);
        var r = NavigationService.FindReferences(tab, 7);
        Assert.Equal(4, r!.Ranges.Count);
        Assert.Equal(1, r.Index);
        Assert.Equal(r.Ranges[1].Start, tab.SelectionStart);
        Assert.Equal("4개 참조", r.Summary.Lines[0]);

        var last = NavigationService.FindReferences(tab, r.Ranges[3].Start);
        Assert.Equal(0, last!.Index);
        Assert.Null(NavigationService.FindReferences(tab, 6));
    }

    [Fact]
    public async Task Command_handler_dispatches_and_shows_popup_model()
    {
        var (svc, host, tab, _, ddl) = Setup("select * from emp");
        tab.UpdateCaret(16, 16, 0);
        ObjectInfo? shown = null;
        var h = new NavigationCommandHandler { Host = host, Service = svc, Presenter = (_, i) => shown = i };
        var keys = new AlwaysKeys();

        await h.ExecuteAsync(new CommandContext("oranda.nav.describeObject", keys), default);
        Assert.Equal("SCOTT.EMP", shown!.Title);

        await h.ExecuteAsync(new CommandContext("oranda.nav.goToDefinition", keys), default);
        Assert.Single(ddl);

        await h.ExecuteAsync(new CommandContext("oranda.nav.findReferences", keys), default);
        Assert.Equal("emp", shown!.Title);
    }

    [Fact]
    public async Task Describe_on_a_table_carries_the_layout_model_but_not_on_a_package()
    {
        var (svc, _, tab, _, _) = Setup("select * from emp");
        var info = await svc.DescribeAsync(tab, 16);
        var layout = info!.Layout!;
        Assert.Equal("SCOTT.EMP", layout.Title);
        Assert.Equal(["EMPNO", "ENAME", "SAL"], layout.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(["NUMBER(4)", "VARCHAR2(10)", "NUMBER(7,2)"], layout.Rows.Select(r => r.TypeText).ToArray());
        Assert.Equal([true, true, false], layout.Rows.Select(r => r.NotNull).ToArray());
        Assert.Equal("급여", layout.Rows[2].Comment);
        Assert.Equal([1, 2, 3], layout.Rows.Select(r => r.Index).ToArray());

        var (svc2, _, tab2, _, _) = Setup("begin emp_pkg.hire(1); end;");
        var pkg = await svc2.DescribeAsync(tab2, tab2.Text.IndexOf("emp_pkg", StringComparison.Ordinal) + 2);
        Assert.Null(pkg!.Layout);
        Assert.NotEmpty(pkg.Lines);
    }

    [Fact]
    public Task Ctrl_click_wiring_attaches_once_and_popup_does_not_throw_headless() => Ui.Run(() =>
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "select * from emp");
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 800, Height = 500, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        NavigationWiring.Attach(view, vm);
        NavigationWiring.Attach(view, vm);
        ObjectInfoPopup.Show(vm, new ObjectInfo("SCOTT.EMP", ["TABLE", "  ID  NUMBER"]));
        ObjectInfoPopup.Close();
        window.Close();
    });

    private sealed class AlwaysKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }

    private sealed class FakeSession : ISession
    {
        public static readonly Guid ProfileId = Guid.NewGuid();

        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "scott", "localhost/FREEPDB1", Id: ProfileId);

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMeta : IMetadataProvider
    {
        private static readonly DbObjectRef[] All =
        [
            new("SCOTT", "EMP", "TABLE"),
            new("HR", "EMPLOYEES", "TABLE"),
            new("SCOTT", "EMP_PKG", "PACKAGE"),
            new("SCOTT", "EMP_PKG", "PACKAGE BODY"),
        ];

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DbObjectRef>>([.. All.Where(o =>
                (q.Owner is null || o.Owner == q.Owner) && (q.NamePattern is null || o.Name == q.NamePattern))]);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
            => Task.FromResult(new TableInfo(table,
            [
                new ColumnInfo("EMPNO", 1, "NUMBER") { Precision = 4, IsNullable = false },
                new ColumnInfo("ENAME", 2, "VARCHAR2") { Length = 10, IsNullable = false },
                new ColumnInfo("SAL", 3, "NUMBER") { Precision = 7, Scale = 2, Comment = "급여" },
            ])
            { Comment = "직원" });

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProcedureInfo>>(
            [
                new ProcedureInfo("HIRE", false, [new ArgumentInfo("P_ID", 1, "NUMBER", ArgumentDirection.In)]),
                new ProcedureInfo("RAISE_SALARY", false, [new ArgumentInfo("P_ID", 1, "NUMBER", ArgumentDirection.In)]),
            ]);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => Task.FromResult(obj.ObjectType == "PACKAGE BODY" ? PackageBody : "package emp_pkg as end;");

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
