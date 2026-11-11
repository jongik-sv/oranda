using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.PlsqlWorkspace;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;
using static Oranda.App.Tests.Features.PlsqlEditor.PlsqlEditorTests;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>
/// PKG1(D-067): 패키지·타입은 PL/SQL 탭 하나에 명세와 본문 두 쪽을 갖는다. 「명세 | 본문」 전환·쪽마다 글·되돌리기·고침 표시,
/// 「이 쪽만 컴파일」·「명세+본문 컴파일」, 컴파일 오류의 쪽 표시, 「구성원」 목록, 「본문 만들기」, 작업 공간 저장 형식(묶음·예전 파일 읽기).
/// </summary>
[Collection(PlsqlStatics.Name)]
public sealed class PlsqlPackageTabTests : IDisposable
{
    private static readonly DbObjectRef Spec = new("SCOTT", "PK", "PACKAGE");
    private static readonly DbObjectRef Body = new("SCOTT", "PK", "PACKAGE BODY");

    private const string SpecText =
        "PACKAGE pk AS\n"
        + "  TYPE t_rec IS RECORD (a NUMBER);\n"
        + "  CURSOR c_emp IS SELECT CASE WHEN 1 = 1 THEN 1 END FROM dual;\n"
        + "  PROCEDURE a;\n"
        + "  FUNCTION f(x NUMBER) RETURN NUMBER;\n"
        + "  PROCEDURE ov(x NUMBER);\n"
        + "  PROCEDURE ov(x VARCHAR2 -- 글자\n    );\n"
        + "END pk;\n";

    private const string BodyText =
        "PACKAGE BODY pk AS\n"
        + "  -- PROCEDURE commented;\n"
        + "  PROCEDURE helper IS\n"
        + "    PROCEDURE inner_local IS BEGIN NULL; END;\n"
        + "    TYPE t_local IS TABLE OF NUMBER;\n"
        + "  BEGIN\n"
        + "    IF 1 = 1 THEN inner_local; END IF;\n"
        + "    FOR i IN 1 .. 2 LOOP NULL; END LOOP;\n"
        + "  END helper;\n"
        + "  PROCEDURE a IS BEGIN helper; END a;\n"
        + "  FUNCTION f(x NUMBER) RETURN NUMBER IS\n"
        + "  BEGIN\n"
        + "    RETURN CASE WHEN x > 0 THEN 1 ELSE 0 END;\n"
        + "  END f;\n"
        + "  PROCEDURE ov(x NUMBER) IS BEGIN NULL; END;\n"
        + "  PROCEDURE ov(x VARCHAR2) IS BEGIN NULL; END;\n"
        + "END pk;\n";

    public PlsqlPackageTabTests() => PlsqlEditorViewModel.Active = null;

    public void Dispose() => PlsqlEditorViewModel.Active = null;

    private static FakeMeta PackageMeta()
    {
        var meta = new FakeMeta { Source = BodyText };
        meta.Sources["PACKAGE"] = SpecText;
        meta.Sources["PACKAGE BODY"] = BodyText;
        return meta;
    }

    private static async Task<(PlsqlEditorViewModel Vm, FakeMeta Meta, FakeExec Exec)> OpenAsync(DbObjectRef target, FakeMeta? meta = null)
    {
        meta ??= PackageMeta();
        var exec = new FakeExec();
        var vm = new PlsqlEditorViewModel(meta, exec);
        Assert.True(await vm.OpenAsync(new FakeSession(), target));
        return (vm, meta, exec);
    }

    private static int LineOf(SqlEditorViewModel editor) => editor.CaretPosition.Line;

    private static int LineOfText(string text, string needle) => text[..text.IndexOf(needle, StringComparison.Ordinal)].Count(c => c == '\n') + 1;

    // ---- 구성원 훑개 ----

    [Fact]
    public void The_member_scanner_finds_the_outer_members_of_a_spec_and_a_body_and_skips_local_ones_and_comments()
    {
        var spec = PlsqlMemberScanner.Scan(SpecText);
        Assert.Equal(
            [(PlsqlMemberKind.Type, "T_REC", 0), (PlsqlMemberKind.Cursor, "C_EMP", 0), (PlsqlMemberKind.Procedure, "A", 0), (PlsqlMemberKind.Function, "F", 0),
             (PlsqlMemberKind.Procedure, "OV", 0), (PlsqlMemberKind.Procedure, "OV", 1)],
            spec.Select(m => (m.Kind, m.Name, m.Ordinal)));
        Assert.Equal("FUNCTION f(x NUMBER) RETURN NUMBER", spec.Single(m => m.Name == "F").Head);
        Assert.Equal("PROCEDURE ov(x VARCHAR2 )", spec[^1].Head); // 줄 주석은 머리에 들어가지 않는다

        var body = PlsqlMemberScanner.Scan(PlsqlSourceText.ToEditorText(BodyText, "SCOTT", out _));
        Assert.Equal(["HELPER", "A", "F", "OV", "OV"], body.Select(m => m.Name));
        Assert.DoesNotContain(body, m => m.Name is "INNER_LOCAL" or "T_LOCAL" or "COMMENTED");

        Assert.Empty(PlsqlMemberScanner.Scan("PROCEDURE p IS BEGIN NULL; END;"));
        Assert.Empty(PlsqlMemberScanner.Scan("PACKAGE broken AS PROCEDURE")); // 깨진 글도 예외 없이
    }

    [Fact]
    public void The_member_scanner_reads_object_type_specs_and_bodies()
    {
        var spec = PlsqlMemberScanner.Scan(
            "CREATE OR REPLACE TYPE t_point AS OBJECT (\n  x NUMBER,\n  MEMBER FUNCTION dist(p t_point) RETURN NUMBER,\n"
            + "  CONSTRUCTOR FUNCTION t_point(x NUMBER) RETURN SELF AS RESULT,\n  STATIC PROCEDURE reset\n);\n");
        Assert.Equal([(PlsqlMemberKind.Function, "DIST"), (PlsqlMemberKind.Function, "T_POINT"), (PlsqlMemberKind.Procedure, "RESET")], spec.Select(m => (m.Kind, m.Name)));
        Assert.Equal("CONSTRUCTOR FUNCTION t_point(x NUMBER) RETURN SELF AS RESULT", spec[1].Head);

        var body = PlsqlMemberScanner.Scan(
            "TYPE BODY t_point AS\n  MEMBER FUNCTION dist(p t_point) RETURN NUMBER IS BEGIN RETURN 0; END;\n"
            + "  CONSTRUCTOR FUNCTION t_point(x NUMBER) RETURN SELF AS RESULT IS BEGIN RETURN; END;\nEND;\n");
        Assert.Equal(["DIST", "T_POINT"], body.Select(m => m.Name));

        var skeleton = PlsqlSourceText.BodySkeleton("TYPE", "SCOTT", "t_point",
            "TYPE t_point AS OBJECT (x NUMBER, CONSTRUCTOR FUNCTION t_point(x NUMBER) RETURN SELF AS RESULT)");
        Assert.StartsWith("CREATE OR REPLACE TYPE BODY \"SCOTT\".t_point AS\n", skeleton, StringComparison.Ordinal);
        Assert.Contains("RETURN;", skeleton, StringComparison.Ordinal);
        Assert.EndsWith("\nEND;\n", skeleton, StringComparison.Ordinal);
    }

    // ---- 열기 · 전환 · 고침 표시 ----

    [Fact]
    public async Task Opening_a_package_reads_spec_and_body_into_one_tab()
    {
        var (vm, meta, _) = await OpenAsync(Spec);

        Assert.True(vm.HasSides);
        Assert.True(vm.IsPackageDoc);
        Assert.Equal(PlsqlSide.Spec, vm.Side);
        Assert.Equal("SCOTT.PK", vm.Title);
        Assert.Equal(Spec, vm.SpecPart!.Target);
        Assert.Equal(Body, vm.BodyPart!.Target);
        Assert.True(vm.BodyPart.Loaded);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE \"SCOTT\".pk AS", vm.Editor.Text, StringComparison.Ordinal);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY \"SCOTT\".pk AS", vm.BodyPart.Editor.Text, StringComparison.Ordinal);
        Assert.Equal(["PACKAGE", "PACKAGE BODY"], meta.SourceReads);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Each_side_keeps_its_own_text_undo_and_dirty_mark()
    {
        var (vm, _, _) = await OpenAsync(Spec);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var specEditor = vm.Editor;

        Assert.True(await vm.SwitchSideAsync());
        Assert.Contains(nameof(PlsqlEditorViewModel.Editor), changed); // 화면이 다른 편집기로 바꿔 끼운다
        Assert.NotSame(specEditor, vm.Editor);
        Assert.Equal(1, vm.SideIndex);
        vm.Editor.Document.Insert(0, "-- 본문만 고침\n");

        Assert.True(vm.BodyPart!.IsDirty);
        Assert.Equal("본문 *", vm.BodyPart.Label);
        Assert.False(vm.SpecPart!.IsDirty);
        Assert.Equal("명세", vm.SpecPart.Label);
        Assert.True(vm.Editor.CanUndo);
        Assert.False(specEditor.CanUndo);
        Assert.True(vm.IsDirty);
        Assert.True(vm.NeedsSaveConfirm);

        vm.SideIndex = 0; // 도구줄 「명세」 칸(읽어 둔 쪽: 서버를 부르지 않고 바로 바뀐다)
        Assert.Equal(PlsqlSide.Spec, vm.Side);
        Assert.Same(specEditor, vm.Editor);
        Assert.StartsWith("-- 본문만 고침", vm.BodyPart.Editor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_asks_for_each_changed_side()
    {
        var (vm, _, _) = await OpenAsync(Spec);
        vm.SpecPart!.Editor.Document.Insert(0, "-- a\n");
        vm.BodyPart!.Editor.Document.Insert(0, "-- b\n");
        var dialogs = new AnswerDialogs(SaveChoice.DontSave, SaveChoice.DontSave);

        Assert.True(await vm.ConfirmCloseAsync(new SqlEditorHost(dialogs: dialogs), default));

        Assert.Equal(["SCOTT.PK (명세)", "SCOTT.PK (본문)"], dialogs.Asked);
    }

    // ---- 컴파일 ----

    [Fact]
    public async Task Compile_this_side_sends_only_the_visible_side_and_keeps_the_other_dirty()
    {
        var (vm, _, exec) = await OpenAsync(Spec);
        vm.SpecPart!.Editor.Document.Insert(vm.SpecPart.Editor.Document.TextLength, "-- 명세 고침\n");
        Assert.True(await vm.SwitchSideAsync(PlsqlSide.Body));
        vm.Editor.Document.Replace(vm.Editor.Text.IndexOf("NULL; END;", StringComparison.Ordinal), 5, "NULL; NULL;");

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        var sent = Assert.Single(exec.Statements);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY \"SCOTT\".pk AS", sent.Text, StringComparison.Ordinal);
        Assert.False(vm.BodyPart!.IsDirty);
        Assert.True(vm.SpecPart.IsDirty);
    }

    [Fact]
    public async Task Compile_spec_and_body_sends_the_spec_first_then_the_body()
    {
        var (vm, _, exec) = await OpenAsync(Body);
        vm.SpecPart!.Editor.Document.Insert(0, "-- x\n");
        vm.BodyPart!.Editor.Document.Insert(0, "-- y\n");

        var outcome = await vm.CompileBothAsync();

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Equal(2, exec.Statements.Count);
        Assert.Contains("PACKAGE \"SCOTT\".pk AS", exec.Statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("PACKAGE BODY \"SCOTT\".pk AS", exec.Statements[1].Text, StringComparison.Ordinal);
        Assert.False(vm.IsDirty);
        Assert.Equal(PlsqlSide.Body, vm.Side); // 보던 쪽 그대로
        Assert.Contains("명세: 컴파일됨", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("본문: 컴파일됨", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_spec_is_blocked_the_body_is_not_sent()
    {
        var (vm, meta, exec) = await OpenAsync(Body);
        meta.LastDdl = meta.LastDdl.AddMinutes(1); // 다른 사람이 서버를 바꿨다 → 명세가 충돌

        var outcome = await vm.CompileBothAsync();

        Assert.Equal(PlsqlCompileStatus.Conflict, outcome.Status);
        Assert.Empty(exec.Statements);
        Assert.Equal(PlsqlSide.Spec, vm.Side); // 막힌 쪽(충돌 배너)을 보인다
        Assert.True(vm.HasConflictBanner);

        exec.CreateStatus = ExecutionStatus.Failed;
        var failed = await vm.CompileBothAsync(force: true);
        Assert.Equal(PlsqlCompileStatus.Failed, failed.Status);
        Assert.Single(exec.Statements); // 명세만 보내고 멈췄다
    }

    [Fact]
    public async Task A_side_refuses_the_other_sides_source()
    {
        var (vm, _, exec) = await OpenAsync(Spec);
        Assert.True(await vm.SwitchSideAsync(PlsqlSide.Body));
        vm.Editor.LoadText(vm.SpecPart!.Editor.Text, dirty: true); // 본문 쪽에 명세 글

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.NothingToCompile, outcome.Status);
        Assert.Empty(exec.Statements);
    }

    [Fact]
    public async Task Compile_commands_route_to_this_side_and_to_both_sides()
    {
        var (vm, _, exec) = await OpenAsync(Spec);
        var handler = new PlsqlEditorCommandHandler(new EmptyServices()) { ActiveProvider = () => vm, ShowErrorsPanel = () => { } };

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.toggleSpecBody", new AnyKeys(), null), default);
        Assert.Equal(PlsqlSide.Body, vm.Side);

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.compile", new AnyKeys(), null), default);
        Assert.Contains("PACKAGE BODY", Assert.Single(exec.Statements).Text, StringComparison.Ordinal);

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.compileSpecAndBody", new AnyKeys(), null), default);
        Assert.Equal(3, exec.Statements.Count);
        Assert.DoesNotContain("PACKAGE BODY", exec.Statements[1].Text, StringComparison.Ordinal);
        Assert.Contains("PACKAGE BODY", exec.Statements[2].Text, StringComparison.Ordinal);
    }

    // ---- 컴파일 오류의 쪽 ----

    [Fact]
    public async Task Compile_errors_are_marked_spec_or_body_and_selecting_one_jumps_to_that_side_and_line()
    {
        var errorsVm = new CompileErrorsViewModel();
        var meta = PackageMeta();
        meta.ErrorsByType["PACKAGE"] = [new CompileError(4, 3, "PLS-00103: spec", false)];
        meta.ErrorsByType["PACKAGE BODY"] = [new CompileError(10, 5, "PLS-00201: body", false)];
        var (vm, _, _) = await OpenAsync(Spec, meta);

        Assert.Equal(["PACKAGE", "PACKAGE BODY"], vm.Errors.Select(e => e.ObjectType));
        Assert.Equal(["명세", "본문"], errorsVm.Items.Select(i => i.Side));
        Assert.Contains("오류 2건", errorsVm.Header, StringComparison.Ordinal);

        errorsVm.SelectedItem = errorsVm.Items[1];

        Assert.Equal(PlsqlSide.Body, vm.Side);
        Assert.Equal(10, LineOf(vm.Editor));

        errorsVm.SelectedItem = errorsVm.Items[0];
        Assert.Equal(PlsqlSide.Spec, vm.Side);
        Assert.Equal(4, LineOf(vm.Editor));

        // 프로시저처럼 쌍이 없는 탭은 쪽 표시가 없다.
        var proc = await OpenedProcedure();
        Assert.All(proc.Errors, e => Assert.Null(proc.SideLabel(e)));
    }

    [Fact]
    public async Task Compiling_one_side_keeps_the_other_sides_errors_in_the_list()
    {
        var meta = PackageMeta();
        meta.ErrorsByType["PACKAGE"] = [new CompileError(2, 1, "spec", false)];
        var (vm, _, _) = await OpenAsync(Body, meta);
        meta.ErrorsByType["PACKAGE BODY"] = [new CompileError(3, 1, "body", false)];

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.CompiledWithErrors, outcome.Status);
        Assert.Equal(["spec", "body"], vm.Errors.Select(e => e.Message));
    }

    // ---- 구성원 목록 ----

    [Fact]
    public async Task The_member_list_pairs_spec_and_body_members_and_shows_where_each_lives()
    {
        var (vm, _, _) = await OpenAsync(Spec);

        Assert.Equal(
            [("T_REC", "명세만"), ("C_EMP", "명세만"), ("A", "명세·본문"), ("F", "명세·본문"), ("OV", "명세·본문"), ("OV", "명세·본문"), ("HELPER", "본문만")],
            vm.Members.Select(m => (m.Name, m.WhereText)));
        Assert.Equal(["타입", "커서", "프로시저", "함수", "프로시저", "프로시저", "프로시저"], vm.Members.Select(m => m.KindText));
        Assert.True(vm.MembersExpanded);
        vm.MembersExpanded = false; // 접을 수 있다
        Assert.False(vm.MembersExpanded);
    }

    [Fact]
    public async Task Clicking_a_member_goes_there_on_this_side_or_switches_when_it_lives_only_on_the_other_side()
    {
        var (vm, _, _) = await OpenAsync(Spec);

        Assert.True(vm.GoToMember(vm.Members.Single(m => m.Name == "F")));
        Assert.Equal(PlsqlSide.Spec, vm.Side);
        Assert.Equal(LineOfText(vm.Editor.Text, "FUNCTION f"), LineOf(vm.Editor));

        Assert.True(vm.GoToMember(vm.Members.Single(m => m.Name == "HELPER"))); // 본문에만 있다
        Assert.Equal(PlsqlSide.Body, vm.Side);
        Assert.Equal(LineOfText(vm.Editor.Text, "PROCEDURE helper"), LineOf(vm.Editor));

        Assert.True(vm.GoToMember(vm.Members.Where(m => m.Name == "OV").ElementAt(1))); // 겹쳐 쓰기의 둘째
        Assert.Equal(PlsqlSide.Body, vm.Side);
        Assert.Equal(LineOfText(vm.Editor.Text, "PROCEDURE ov(x VARCHAR2)"), LineOf(vm.Editor));

        Assert.True(vm.GoToMember(vm.Members.Single(m => m.Name == "T_REC"))); // 명세에만 있다
        Assert.Equal(PlsqlSide.Spec, vm.Side);
    }

    // ---- 본문 없는 패키지 ----

    [Fact]
    public async Task A_package_without_a_body_shows_an_empty_body_side_whose_hint_inserts_a_skeleton()
    {
        var meta = PackageMeta();
        meta.FailTypes.Add("PACKAGE BODY");
        var (vm, _, exec) = await OpenAsync(Spec, meta);

        Assert.True(vm.BodyPart!.Missing);
        Assert.False(vm.ShowCreateBodyHint);
        Assert.True(await vm.SwitchSideAsync());
        Assert.True(vm.ShowCreateBodyHint);
        Assert.Contains("서버에 아직 없다", vm.StatusText, StringComparison.Ordinal);

        Assert.True(vm.CreateBody());

        Assert.False(vm.ShowCreateBodyHint);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY \"SCOTT\".PK AS\n", vm.Editor.Text, StringComparison.Ordinal);
        Assert.Contains("  PROCEDURE a IS\n  BEGIN\n    NULL;\n  END;", vm.Editor.Text, StringComparison.Ordinal);
        Assert.Contains("  FUNCTION f(x NUMBER) RETURN NUMBER IS\n  BEGIN\n    RETURN NULL;\n  END;", vm.Editor.Text, StringComparison.Ordinal);
        Assert.EndsWith("END PK;\n", vm.Editor.Text, StringComparison.Ordinal);
        Assert.True(vm.Editor.CanUndo); // 되돌리기로 지울 수 있다
        Assert.True(vm.BodyPart.IsDirty);
        Assert.False(vm.CreateBody()); // 이미 글이 있으면 다시 넣지 않는다

        meta.FailTypes.Clear();
        var outcome = await vm.CompileAsync();
        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Contains("PACKAGE BODY", Assert.Single(exec.Statements).Text, StringComparison.Ordinal);
        Assert.False(vm.BodyPart.Missing);
    }

    [Fact]
    public async Task A_new_document_compiled_as_a_package_gains_a_body_side_read_on_first_view()
    {
        var meta = PackageMeta();
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());
        vm.StartNew("CREATE OR REPLACE PACKAGE scott.pk AS\n  PROCEDURE a;\nEND pk;\n", new FakeSession());
        Assert.False(vm.HasSides);

        Assert.Equal(PlsqlCompileStatus.Compiled, (await vm.CompileAsync()).Status);

        Assert.True(vm.HasSides);
        Assert.False(vm.BodyPart!.Loaded);
        Assert.True(await vm.SwitchSideAsync());
        Assert.True(vm.BodyPart.Loaded);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY", vm.Editor.Text, StringComparison.Ordinal);
    }

    // ---- 저장 형식 ----

    [Fact]
    public async Task A_package_tab_saves_both_sides_and_comes_back_on_the_side_it_showed()
    {
        var (vm, _, _) = await OpenAsync(Spec);
        Assert.True(await vm.SwitchSideAsync());
        vm.Editor.Document.Insert(0, "-- mine\n");

        var file = vm.Capture();

        Assert.Equal("PACKAGE", file.Type);
        Assert.False(file.IsDirty);
        Assert.Equal("PACKAGE BODY", file.Body!.Type);
        Assert.True(file.Body.IsDirty);
        Assert.StartsWith("-- mine", file.Body.Text, StringComparison.Ordinal);
        Assert.Equal(PlsqlTabFile.BodySide, file.Side);
        var json = PlsqlWorkspaceStore.Serialize(new PlsqlWorkspaceFile { Id = "w", Name = "w", Tabs = [file] });
        Assert.Contains("\"body\":", json, StringComparison.Ordinal);

        var back = new PlsqlEditorViewModel(PackageMeta(), new FakeExec());
        back.Restore(file);

        Assert.True(back.HasSides);
        Assert.Equal(PlsqlSide.Body, back.Side);
        Assert.True(back.BodyPart!.IsDirty);
        Assert.False(back.SpecPart!.IsDirty);
        Assert.Equal(vm.SpecPart!.Editor.Text, back.SpecPart.Editor.Text);
        Assert.True(back.IsPackageDoc); // 두 쪽 글이 다 있어 접속 없이도 바꿔 볼 수 있다
        Assert.Equal(file, back.Capture());
    }

    [Fact]
    public async Task An_old_single_body_tab_restores_into_the_pair_and_reads_the_spec_when_its_session_attaches()
    {
        var meta = PackageMeta();
        var session = new FakeSession();
        var old = new PlsqlTabFile
        {
            Owner = "SCOTT", Name = "PK", Type = "PACKAGE BODY", ConnectionProfileId = session.Profile.Id, LastDdlTime = meta.LastDdl,
            Title = "SCOTT.PK (본문)", Text = "CREATE OR REPLACE PACKAGE BODY \"SCOTT\".pk AS\nEND pk;\n",
        };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());

        vm.Restore(old);

        Assert.Equal("SCOTT.PK", vm.Title); // 예전 「(본문)」 꼬리는 뗀다
        Assert.Equal(PlsqlSide.Body, vm.Side);
        Assert.False(vm.SpecPart!.Loaded);
        Assert.False(vm.IsPackageDoc); // 명세 글이 없고 접속도 없다
        Assert.False(await vm.SwitchSideAsync());

        Assert.True(await vm.AttachSessionAsync(session));

        Assert.True(vm.SpecPart.Loaded);
        Assert.True(vm.IsPackageDoc);
        Assert.Equal(old.Text, vm.Editor.Text); // 본문 글은 그대로
        Assert.True(await vm.SwitchSideAsync());
        Assert.StartsWith("CREATE OR REPLACE PACKAGE \"SCOTT\".pk AS", vm.Editor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_separate_spec_and_body_tabs_merge_into_one_tab_and_the_active_index_follows()
    {
        var profile = Guid.NewGuid();
        PlsqlTabFile Tab(string name, string type, Guid? conn = null) => new() { Owner = "SCOTT", Name = name, Type = type, ConnectionProfileId = conn ?? profile, Text = type + " " + name };
        var proc = Tab("P", "PROCEDURE");
        var spec = Tab("PK", "PACKAGE");
        var other = Tab("Q", "FUNCTION");
        var body = Tab("PK", "PACKAGE BODY");
        var lone = Tab("LONE", "PACKAGE BODY");
        var elsewhere = Tab("PK", "PACKAGE BODY", Guid.NewGuid()); // 다른 접속의 같은 이름은 따로
        var file = new PlsqlTabFile { Text = "new" };

        var (tabs, active) = PlsqlTabMerge.Merge([proc, spec, other, body, lone, elsewhere, file], activeTab: 3);

        Assert.Equal(6, tabs.Count);
        Assert.Equal(1, active);
        Assert.Equal(spec with { Body = body, Side = PlsqlTabFile.BodySide }, tabs[1]); // 보던 탭이 본문이었다
        Assert.Same(lone, tabs[3]);
        Assert.Same(elsewhere, tabs[4]);
        Assert.Same(file, tabs[5]);

        // 본문 탭이 먼저 나와도 앞 자리에 묶이고, 겉은 명세다.
        var (reversed, at) = PlsqlTabMerge.Merge([body, spec], activeTab: 1);
        Assert.Equal(spec with { Body = body }, Assert.Single(reversed));
        Assert.Equal(0, at);

        // Markdown 은 두 절로 푼다(사람이 읽는 형식은 그대로).
        Assert.Equal([proc, spec, body], PlsqlTabMerge.Split([proc, spec with { Body = body, Side = PlsqlTabFile.BodySide }]));
    }

    [Fact]
    public void A_markdown_workspace_writes_the_pair_as_two_sections_and_reads_back_into_one_tab()
    {
        var profile = Guid.NewGuid();
        var spec = new PlsqlTabFile { Owner = "APP", Name = "PK_ORDER", Type = "PACKAGE", ConnectionProfileId = profile, Title = "APP.PK_ORDER", Text = "CREATE OR REPLACE PACKAGE \"APP\".pk_order AS\nEND;\n" };
        var body = new PlsqlTabFile { Owner = "APP", Name = "PK_ORDER", Type = "PACKAGE BODY", ConnectionProfileId = profile, Title = "APP.PK_ORDER", Text = "CREATE OR REPLACE PACKAGE BODY \"APP\".pk_order AS\nEND;\n" };
        var md = PlsqlWorkspaceMarkdown.Write(new PlsqlWorkspaceFile { Id = "w", Name = "주문", Tabs = [spec with { Body = body }] }, _ => "ORA 개발");

        Assert.Contains("## APP.PK_ORDER (PACKAGE)\n", md, StringComparison.Ordinal);
        Assert.Contains("## APP.PK_ORDER (PACKAGE BODY)\n", md, StringComparison.Ordinal);

        var read = PlsqlWorkspaceMarkdown.Read(md, "주문", _ => profile);
        Assert.Equal(2, read.Tabs.Count);
        var (tabs, _) = PlsqlTabMerge.Merge(read.Tabs, read.ActiveTab);
        var merged = Assert.Single(tabs);
        Assert.Equal("PACKAGE", merged.Type);
        Assert.Equal(body.Text, merged.Body!.Text);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class AnyKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }
}
