using Oranda.App.Features.Navigation;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>DB 객체로 이동(<c>oranda.nav.quickOpenObject</c>)과 스키마 브라우저에서 찾기(<c>oranda.schema.reveal</c>) 명령 시험.</summary>
public class QuickOpenObjectTests
{
    private static readonly DbObjectRef[] Catalog =
    [
        new("SCOTT", "EMP", "TABLE"),
        new("SCOTT", "EMP_DEPT", "VIEW"),
        new("SCOTT", "EMPLOYEE_HISTORY", "TABLE"),
        new("SCOTT", "OLD_EMP", "TABLE"),
        new("SCOTT", "EMP_PKG", "PACKAGE"),
        new("SCOTT", "EMP_PKG", "PACKAGE BODY"),
        new("SCOTT", "LONELY_BODY", "PACKAGE BODY"),
        new("HR", "EMP", "SYNONYM"),
        new("HR", "EMPLOYEES", "TABLE"),
        new("SYS", "DBA_OBJECTS", "VIEW"),
    ];

    [Fact]
    public void Commands_are_registered_on_one_handler_with_session_enablement_and_editor_context_menu()
    {
        var reg = ShellTestHelpers.CreateCommandRegistry();
        var quick = reg.Find("oranda.nav.quickOpenObject")!;
        var reveal = reg.Find("oranda.schema.reveal")!;
        Assert.Equal("hasSession", quick.Enablement);
        Assert.Equal("hasSession", reveal.Enablement);
        Assert.Equal(CommandCategory.Navigate, quick.Category);
        Assert.Contains("context:editor/revealInSchema@330", reveal.Placements);
        Assert.Contains("menu:edit/go/quickOpenObject@500", quick.Placements); // 편집 > 이동 ▸ (TSK-09-02)
        var handlers = ShellTestHelpers.AppContributions.Commands
            .Where(c => c.Definition.Id is "oranda.nav.quickOpenObject" or "oranda.schema.reveal")
            .Select(c => c.HandlerType).Distinct();
        Assert.Equal(typeof(ObjectNavigationCommandHandler), Assert.Single(handlers));
    }

    // ---- 거르기 ---------------------------------------------------------------------------

    [Fact]
    public void Search_ranks_exact_then_prefix_then_contains_then_word_heads_and_prefers_the_users_schema()
    {
        var found = QuickOpenObjectViewModel.Search(Catalog, "emp", "SCOTT");
        Assert.Equal(
            ["SCOTT.EMP", "HR.EMP", "SCOTT.EMP_PKG", "SCOTT.EMP_DEPT", "SCOTT.EMPLOYEE_HISTORY", "HR.EMPLOYEES", "SCOTT.OLD_EMP"],
            found.Select(o => $"{o.Owner}.{o.Name}"));

        // 밑줄로 나뉜 단어 머리글자: ed → EMP_DEPT, eh → EMPLOYEE_HISTORY
        Assert.Equal("EMP_DEPT", Assert.Single(QuickOpenObjectViewModel.Search(Catalog, "ed", "SCOTT")).Name);
        Assert.Equal("EMPLOYEE_HISTORY", QuickOpenObjectViewModel.Search(Catalog, "emhi", "SCOTT").Single().Name);
    }

    [Fact]
    public void Search_hides_bodies_that_have_a_spec_and_supports_owner_prefix_and_wildcards()
    {
        var pkg = QuickOpenObjectViewModel.Search(Catalog, "pkg", "SCOTT");
        Assert.Equal(new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE"), Assert.Single(pkg));
        Assert.Equal("PACKAGE BODY", Assert.Single(QuickOpenObjectViewModel.Search(Catalog, "lonely", "SCOTT")).ObjectType);

        Assert.Equal(["EMP", "EMPLOYEES"], QuickOpenObjectViewModel.Search(Catalog, "hr.emp", "SCOTT").Select(o => o.Name));
        Assert.Equal(["EMP", "EMPLOYEES"], QuickOpenObjectViewModel.Search(Catalog, "\"HR\".", "SCOTT").Select(o => o.Name).Order());
        Assert.Equal(["EMP_DEPT", "EMP_PKG"], QuickOpenObjectViewModel.Search(Catalog, "emp_*", "SCOTT").Select(o => o.Name).Order());
        Assert.Equal(["OLD_EMP"], QuickOpenObjectViewModel.Search(Catalog, "%_emp", "SCOTT").Select(o => o.Name));
        Assert.Empty(QuickOpenObjectViewModel.Search(Catalog, "  ", "SCOTT"));
        Assert.Empty(QuickOpenObjectViewModel.Search(Catalog, "zzz", "SCOTT"));
        Assert.Equal(3, QuickOpenObjectViewModel.Search(Catalog, "emp", "SCOTT", limit: 3).Count);
    }

    [Theory]
    [InlineData("emp", null, "emp")]
    [InlineData("scott.emp", "scott", "emp")]
    [InlineData("\"Scott\".\"Emp\"", "Scott", "Emp")]
    [InlineData(".emp", null, "emp")]
    [InlineData("hr.", "hr", "")]
    public void Query_splits_owner_and_name(string query, string? owner, string name) =>
        Assert.Equal((owner, name), QuickOpenObjectViewModel.ParseQuery(query));

    [Fact]
    public void Search_of_a_large_catalog_stays_fast()
    {
        var big = Enumerable.Range(0, 100_000).Select(i => new DbObjectRef($"S{i % 50}", $"OBJ_{i:D6}_DATA", i % 3 == 0 ? "VIEW" : "TABLE")).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = QuickOpenObjectViewModel.Search(big, "obj_0999", "S1");
        watch.Stop();
        Assert.Equal(100, found.Count);
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"{watch.ElapsedMilliseconds} ms");
    }

    // ---- ViewModel --------------------------------------------------------------------------

    [Fact]
    public async Task Objects_are_loaded_once_and_each_keystroke_filters_in_memory()
    {
        var loads = 0;
        var vm = NewVm(() => loads++, out _, out _);
        vm.Query = "em";
        Assert.False(vm.HasResults); // 받기 전

        await vm.LoadAsync();
        Assert.True(vm.HasResults);
        Assert.Equal("SCOTT.EMP", vm.Selected!.Display);

        vm.Query = "emp_d";
        vm.Query = "emp_de";
        Assert.Equal("SCOTT.EMP_DEPT", vm.Selected!.Display);
        Assert.Equal("objView", vm.Selected.IconKey);
        Assert.Equal(1, loads);

        vm.Query = "nothing-like-this";
        Assert.False(vm.HasResults);
        Assert.Equal("맞는 객체가 없다", vm.StatusText);
    }

    [Fact]
    public async Task Enter_opens_the_selected_object_and_alt_enter_reveals_it_closing_the_window_first()
    {
        var vm = NewVm(() => { }, out var opened, out var revealed);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        await vm.LoadAsync();
        vm.Query = "emp";
        vm.MoveSelection(1);
        Assert.Equal("HR.EMP", vm.Selected!.Display);

        Assert.True(await vm.OpenSelectedAsync());
        Assert.Equal(new DbObjectRef("HR", "EMP", "SYNONYM"), Assert.Single(opened));
        Assert.Equal(1, closed);

        vm.MoveSelection(-5);
        Assert.True(await vm.RevealSelectedAsync());
        Assert.Equal(new DbObjectRef("SCOTT", "EMP", "TABLE"), Assert.Single(revealed));
        Assert.Equal(2, closed);

        vm.Query = "nothing-like-this";
        Assert.False(await vm.OpenSelectedAsync());
        Assert.Equal(2, closed);
        vm.Dispose();
    }

    [Fact]
    public async Task Enter_right_after_typing_uses_the_new_query_even_while_the_debounce_waits()
    {
        var vm = NewVm(() => { }, out var opened, out _);
        vm.DebounceMilliseconds = 60_000;
        await vm.LoadAsync();
        vm.Query = "employees";

        Assert.True(await vm.OpenSelectedAsync());
        Assert.Equal(new DbObjectRef("HR", "EMPLOYEES", "TABLE"), Assert.Single(opened));
        vm.Dispose();
    }

    [Fact]
    public async Task Load_failure_is_shown_in_the_status_line()
    {
        using var vm = new QuickOpenObjectViewModel(
            _ => throw new InvalidOperationException("ORA-00942"),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult(true));
        await vm.LoadAsync();
        Assert.Contains("ORA-00942", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsLoading);
    }

    // ---- 명령 처리기 ------------------------------------------------------------------------

    [Fact]
    public async Task Quick_open_lists_every_object_once_with_an_unfiltered_query_and_prefills_the_selection()
    {
        var meta = new NavFakeMeta();
        meta.Objects.AddRange(Catalog);
        var editor = new NavFakeEditor { Doc = NavFakeEditor.At("select * from |emp") };
        editor.Doc = editor.Doc with { Selection = new Oranda.Core.Text.TextRange(14, 3) };
        QuickOpenObjectViewModel? shown = null;
        var opened = new List<DbObjectRef>();
        var h = new ObjectNavigationCommandHandler
        {
            Metadata = meta,
            Editor = editor,
            SessionProvider = () => new NavFakeSession(),
            Presenter = vm => shown = vm,
            OpenObject = (r, _, _) =>
            {
                opened.Add(r);
                return Task.CompletedTask;
            },
        };

        await h.ExecuteAsync(new CommandContext("oranda.nav.quickOpenObject", FakeContextKeys.AllTrue), default);

        Assert.NotNull(shown);
        Assert.Equal("emp", shown!.Query);
        Assert.Equal("SCOTT", shown.CurrentUser);
        await WaitForAsync(() => shown.ObjectCount > 0);
        var q = Assert.Single(meta.Queries);
        Assert.Null(q.NamePattern);
        Assert.Null(q.Limit);
        Assert.Null(q.Owner);

        await WaitForAsync(() => shown.HasResults);
        Assert.True(await shown.OpenSelectedAsync());
        Assert.Equal(new DbObjectRef("SCOTT", "EMP", "TABLE"), Assert.Single(opened));
        Assert.Equal(1, meta.ListObjectsCalls);
        shown.Dispose();
    }

    [Fact]
    public async Task Quick_open_without_a_session_only_notifies()
    {
        string? note = null;
        var shown = false;
        var h = new ObjectNavigationCommandHandler { Metadata = new NavFakeMeta(), SessionProvider = () => null, Presenter = _ => shown = true, Notify = m => note = m };
        await h.ExecuteAsync(new CommandContext("oranda.nav.quickOpenObject", FakeContextKeys.AllTrue), default);
        Assert.False(shown);
        Assert.NotNull(note);
    }

    [Theory]
    [InlineData("PACKAGE", "oranda.plsql.openSource")]
    [InlineData("PACKAGE BODY", "oranda.plsql.openSource")]
    [InlineData("PROCEDURE", "oranda.plsql.openSource")]
    [InlineData("TRIGGER", "oranda.plsql.openSource")]
    [InlineData("TABLE", "oranda.schema.openDetail")]
    [InlineData("VIEW", "oranda.schema.openDetail")]
    [InlineData("SEQUENCE", "oranda.schema.openDetail")]
    public void Plsql_units_open_as_source_and_other_objects_as_detail(string type, string command) =>
        Assert.Equal(command, ObjectNavigationCommandHandler.OpenCommandFor(new DbObjectRef("SCOTT", "X", type)));

    [Fact]
    public async Task Reveal_uses_the_command_parameter_first()
    {
        var b = new NavBrowserHarness();
        var h = RevealHandler(b, active: null);
        await h.ExecuteAsync(new CommandContext("oranda.schema.reveal", FakeContextKeys.AllTrue, new DbObjectRef("HR", "JOBS", "TABLE")), default);
        Assert.Equal(new DbObjectRef("HR", "JOBS", "TABLE"), b.Vm.SelectedNode!.Ref);
    }

    [Fact]
    public async Task Reveal_uses_the_object_of_the_active_detail_document()
    {
        var b = new NavBrowserHarness();
        var detail = new ObjectDetailViewModel(b.Meta);
        await detail.OpenAsync(b.Session, new DbObjectRef("SCOTT", "DEPT", "TABLE"), showDdl: false);
        var h = RevealHandler(b, active: detail);

        await h.ExecuteAsync(new CommandContext("oranda.schema.reveal", FakeContextKeys.AllTrue), default);

        Assert.Equal(new DbObjectRef("SCOTT", "DEPT", "TABLE"), b.Vm.SelectedNode!.Ref);
    }

    [Fact]
    public async Task Reveal_resolves_the_name_under_the_editor_caret_through_aliases()
    {
        var b = new NavBrowserHarness();
        b.Editor.Doc = NavFakeEditor.At("select e.ename from v_emp e where |e.empno = 1", b.Session.Profile.Id);
        var h = RevealHandler(b, active: new SqlEditorViewModel());

        await h.ExecuteAsync(new CommandContext("oranda.schema.reveal", FakeContextKeys.AllTrue), default);

        Assert.Equal(new DbObjectRef("SCOTT", "V_EMP", "VIEW"), b.Vm.SelectedNode!.Ref);
    }

    [Fact]
    public async Task Reveal_without_a_target_notifies_and_leaves_the_tree_alone()
    {
        var b = new NavBrowserHarness();
        b.Editor.Doc = NavFakeEditor.At("select 1 from dual  |  ", b.Session.Profile.Id);
        string? note = null;
        var h = RevealHandler(b, active: null);
        h.Notify = m => note = m;

        await h.ExecuteAsync(new CommandContext("oranda.schema.reveal", FakeContextKeys.AllTrue), default);
        Assert.NotNull(note);
        Assert.Null(b.Vm.SelectedNode);

        // 편집기가 아닌 다른 문서가 활성이면 뒤에 있는 편집기의 커서를 쓰지 않는다.
        b.Editor.Doc = NavFakeEditor.At("select * from |emp", b.Session.Profile.Id);
        note = null;
        h.ActiveDocumentProvider = () => new object();
        await h.ExecuteAsync(new CommandContext("oranda.schema.reveal", FakeContextKeys.AllTrue), default);
        Assert.NotNull(note);
        Assert.Null(b.Vm.SelectedNode);
    }

    private static ObjectNavigationCommandHandler RevealHandler(NavBrowserHarness b, object? active) => new()
    {
        Metadata = b.Meta,
        Editor = b.Editor,
        SessionProvider = () => b.Session,
        ActiveDocumentProvider = () => active,
        BrowserProvider = () => b.Vm,
    };

    private static QuickOpenObjectViewModel NewVm(Action onLoad, out List<DbObjectRef> opened, out List<DbObjectRef> revealed)
    {
        var o = opened = [];
        var r = revealed = [];
        return new QuickOpenObjectViewModel(
            _ =>
            {
                onLoad();
                return Task.FromResult<IReadOnlyList<DbObjectRef>>(Catalog);
            },
            (t, _) =>
            {
                o.Add(t);
                return Task.CompletedTask;
            },
            (t, _) =>
            {
                r.Add(t);
                return Task.FromResult(true);
            })
        {
            CurrentUser = "SCOTT",
            DebounceMilliseconds = 0,
        };
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
