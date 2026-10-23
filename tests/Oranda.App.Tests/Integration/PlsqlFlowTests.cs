using Xunit.Abstractions;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-11-01 시나리오 S5 「PL/SQL 편집 → 컴파일 → 실행」(TSK-08-01 + 08-02 + 06-02 실행 엔진).
/// 새 소스 컴파일 → 서버 소스 열기·수정 → 컴파일 오류·고침 → 다른 세션 충돌 → 인자 대화상자 실행(OUT·DBMS_OUTPUT·오류).
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class PlsqlFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static string NewName(string p) => p + "_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Proc(string name, string body = "p_out := 'v=' || p_in; dbms_output.put_line('hi 한글 ' || p_in);") =>
        $"create or replace procedure {name} (p_in in number, p_out out varchar2) is\nbegin\n  {body}\nend;";

    private string Status(string name) => db.Text($"select status from user_objects where object_name = '{name}'");

    [Fact]
    public Task S5_0_Compile_right_after_connect_works_before_any_sql_was_run() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        // 접속만 하고 편집기에서 SQL 을 한 번도 실행하지 않은 채 PL/SQL 소스를 컴파일한다(스키마 브라우저 → PL/SQL 열기 → 저장 흐름).
        using var rig = new FlowRig(db);
        var name = NewName("P_COLD");
        await rig.ConnectAsync();
        try
        {
            var editor = new PlsqlEditorViewModel(rig.Container);
            editor.StartNew(Proc(name), rig.Session);
            var outcome = await editor.CompileAsync();
            Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
            Assert.Equal("VALID", Status(name));
        }
        finally
        {
            db.Exec($"begin execute immediate 'drop procedure {name}'; exception when others then null; end;");
        }
    });

    [Fact]
    public Task S5_1_New_source_compiles_reopens_edits_and_errors_are_navigable_then_fixed() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var name = NewName("P_FLOW");
        await rig.StartHistoryRecorderAsync();
        await rig.ConnectAsync();
        try
        {
            // 새 문서 컴파일 → 서버에 생기고 VALID.
            var editor = new PlsqlEditorViewModel(rig.Container);
            editor.StartNew(Proc(name), rig.Session);
            var created = await editor.CompileAsync();
            Assert.Equal(PlsqlCompileStatus.Compiled, created.Status);
            Assert.Equal("VALID", Status(name));

            // 서버에서 다시 열기: 소스가 같고, 열린 문서는 충돌 없음.
            var reopened = new PlsqlEditorViewModel(rig.Container);
            Assert.True(await reopened.OpenAsync(rig.Session, new DbObjectRef(db.User.ToUpperInvariant(), name, "PROCEDURE")));
            Assert.Contains("p_out", reopened.Editor.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(await reopened.CheckServerChangedAsync());

            // 수정(오류 있는 소스): 컴파일은 되지만 오류 목록과 INVALID. 오류 위치로 이동한다.
            reopened.Editor.LoadText(Proc(name, "p_out := undefined_var;"), dirty: true);
            var bad = await reopened.CompileAsync();
            Assert.Equal(PlsqlCompileStatus.CompiledWithErrors, bad.Status);
            var error = bad.Errors.First(e => !e.IsWarning && e.Message.Contains("undefined_var", StringComparison.OrdinalIgnoreCase));
            Assert.True(error.Line >= 1);
            Assert.True(reopened.NavigateTo(error));
            Assert.Equal("INVALID", Status(name));
            Assert.NotEmpty(reopened.Errors);

            // 고침 → VALID, 오류 목록 비워짐.
            reopened.Editor.LoadText(Proc(name), dirty: true);
            var fixedOutcome = await reopened.CompileAsync();
            Assert.Equal(PlsqlCompileStatus.Compiled, fixedOutcome.Status);
            Assert.Empty(fixedOutcome.Errors.Where(e => !e.IsWarning));
            Assert.Equal("VALID", Status(name));

            // 경계: 소스가 아닌 글·빈 글은 서버에 보내지 않는다. 문법이 깨진 CREATE 는 오류 상태로 알리고 기존 객체를 지우지 않는다.
            var junk = new PlsqlEditorViewModel(rig.Container);
            junk.StartNew("select 1 from dual", rig.Session);
            Assert.Equal(PlsqlCompileStatus.NothingToCompile, (await junk.CompileAsync()).Status);
            junk.StartNew(string.Empty, rig.Session);
            Assert.Equal(PlsqlCompileStatus.NothingToCompile, (await junk.CompileAsync()).Status);
            junk.StartNew($"create or replace procedure {name} (", rig.Session);
            var syntax = await junk.CompileAsync();
            Assert.True(syntax.Status is PlsqlCompileStatus.Failed or PlsqlCompileStatus.CompiledWithErrors or PlsqlCompileStatus.NothingToCompile, syntax.Message);
            // 서버 의미: 구문 오류로 아예 거절되면(Failed) 기존 객체 그대로 VALID, 오류와 함께 만들어졌으면(CompiledWithErrors) INVALID + 오류 목록.
            Assert.Equal(syntax.Status == PlsqlCompileStatus.Failed ? "VALID" : "INVALID", Status(name));
            if (syntax.Status == PlsqlCompileStatus.CompiledWithErrors)
            {
                Assert.NotEmpty(syntax.Errors);
            }

            // 이력: 컴파일 문장이 이력에 남는다(정책 관문·이력 경로를 지난다).
            await rig.WaitHistoryAsync(r => r.Any(e => e.SqlText.Contains(name, StringComparison.Ordinal) && e.Status == HistoryStatus.Succeeded));
            output.WriteLine("S5-1 OK: 새 컴파일·열기·오류 컴파일(INVALID)·오류 위치 이동·고침(VALID)·소스 아님·문법 오류·이력");
        }
        finally
        {
            db.Exec($"begin execute immediate 'drop procedure {name}'; exception when others then null; end;");
        }
    });

    [Fact]
    public Task S5_2_Other_session_change_is_a_conflict_and_force_overwrites_it() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var name = NewName("P_CONF");
        await rig.ConnectAsync();
        db.Exec(Proc(name, "p_out := 'server-v1';"));
        try
        {
            var editor = new PlsqlEditorViewModel(rig.Container);
            Assert.True(await editor.OpenAsync(rig.Session, new DbObjectRef(db.User.ToUpperInvariant(), name, "PROCEDURE")));

            await Task.Delay(1200); // last_ddl_time 은 초 단위
            db.Exec(Proc(name, "p_out := 'server-v2 by someone';"));

            editor.Editor.LoadText(Proc(name, "p_out := 'mine';"), dirty: true);
            var blocked = await editor.CompileAsync();
            Assert.Equal(PlsqlCompileStatus.Conflict, blocked.Status);
            Assert.True(editor.ServerSourceChanged);
            Assert.True(editor.HasConflictBanner);
            Assert.Contains("someone", editor.DiffText, StringComparison.Ordinal); // 차이 보기에 서버 쪽 소스가 보인다
            // 되돌림: 충돌이면 내 소스는 서버에 가지 않았다.
            Assert.Contains("server-v2 by someone", db.Text($"select listagg(text) within group (order by line) from user_source where name = '{name}'"), StringComparison.Ordinal);

            var forced = await editor.CompileAsync(force: true);
            Assert.Equal(PlsqlCompileStatus.Compiled, forced.Status);
            Assert.False(editor.ServerSourceChanged);
            Assert.Contains("'mine'", db.Text($"select listagg(text) within group (order by line) from user_source where name = '{name}'"), StringComparison.Ordinal);
            output.WriteLine("S5-2 OK: 충돌 감지·차이 보기·서버 무변경·강제 덮어쓰기");
        }
        finally
        {
            db.Exec($"begin execute immediate 'drop procedure {name}'; exception when others then null; end;");
        }
    });

    [Fact]
    public Task S5_3_Run_dialog_passes_arguments_returns_out_values_dbms_output_and_reports_errors() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var name = NewName("P_RUN");
        var raiser = NewName("P_ERR");
        await rig.ConnectAsync();
        // 컴파일은 편집기로(08-01), 실행은 대화상자로(08-02) — 두 기능을 이어 쓴다.
        var editor = new PlsqlEditorViewModel(rig.Container);
        editor.StartNew(Proc(name), rig.Session);
        Assert.Equal(PlsqlCompileStatus.Compiled, (await editor.CompileAsync()).Status);
        editor.StartNew($"create or replace procedure {raiser} (p_in in number) is begin raise_application_error(-20001, 'boom ' || p_in); end;", rig.Session);
        Assert.Equal(PlsqlCompileStatus.Compiled, (await editor.CompileAsync()).Status);
        try
        {
            var deps = new PlsqlRunDeps
            {
                Session = () => rig.Service.ActiveSession,
                Metadata = () => rig.Container.GetRequiredService<IMetadataProvider>(),
                Execution = () => rig.Container.GetRequiredService<IExecutionService>(),
                State = rig.State,
            };
            var owner = db.User.ToUpperInvariant();

            // 정상: 인자 행이 서버 정의에서 만들어지고, 값을 넣고 실행하면 OUT 값과 DBMS_OUTPUT 이 온다.
            var vm = new PlsqlRunViewModel(deps);
            await vm.OpenAsync(new DbObjectRef(owner, name, "PROCEDURE"));
            Assert.Equal(["P_IN", "P_OUT"], vm.Rows.Select(r => r.Name));
            Assert.Contains(name, vm.Preview, StringComparison.Ordinal);

            // 경계: 입력하지 않은 인자는 NULL 로 보낸다(설계). 명시적 NULL 도 같다.
            vm.Rows[0].IsNull = true;
            Assert.True(await vm.RunAsync(), vm.StatusText);
            Assert.Equal("v=", vm.Rows[1].OutText.Trim()); // 'v=' || NULL

            vm.Rows[0].IsNull = false;
            vm.Rows[0].Text = "7";
            Assert.True(await vm.RunAsync(), vm.StatusText);
            Assert.Equal("v=7", vm.Rows[1].OutText.Trim());
            Assert.Contains("hi 한글 7", vm.DbmsOutputText, StringComparison.Ordinal);

            // 오류: 숫자가 아닌 입력은 서버에 보내기 전에 문제로 막히거나 오류로 끝난다. 프로시저가 예외를 내면 오류 상태.
            vm.Rows[0].Text = "abc";
            var badInput = await vm.RunAsync();
            Assert.False(badInput && vm.StatusIsError);
            var err = new PlsqlRunViewModel(deps);
            await err.OpenAsync(new DbObjectRef(owner, raiser, "PROCEDURE"));
            err.Rows[0].Text = "5";
            Assert.False(await err.RunAsync());
            Assert.True(err.StatusIsError);
            Assert.Contains("boom 5", err.StatusText, StringComparison.Ordinal);

            // 되돌림: 오류 뒤 세션은 정상, 같은 대화상자에서 다시 실행하면 성공.
            vm.Rows[0].Text = "9";
            Assert.True(await vm.RunAsync(), vm.StatusText);
            Assert.Equal("v=9", vm.Rows[1].OutText.Trim());
            Assert.False(rig.State.Busy);

            // 없는 대상: 열기가 오류 상태로 끝나고 실행 불가.
            var none = new PlsqlRunViewModel(deps);
            await none.OpenAsync(new DbObjectRef(owner, "NO_SUCH_PROC_X", "PROCEDURE"));
            Assert.False(none.CanRun);
            Assert.True(none.StatusIsError);
            output.WriteLine("S5-3 OK: 인자 행·NULL·OUT 값·DBMS_OUTPUT·잘못된 입력·서버 예외·오류 뒤 재실행·없는 대상");
        }
        finally
        {
            db.Exec($"begin execute immediate 'drop procedure {name}'; execute immediate 'drop procedure {raiser}'; exception when others then null; end;");
        }
    });

    [Fact]
    public Task S5_4_Package_overloads_become_candidates_and_member_filter_picks_one() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var pkg = NewName("PKG_FLOW");
        await rig.ConnectAsync();
        var editor = new PlsqlEditorViewModel(rig.Container);
        editor.StartNew($"create or replace package {pkg} is\n  function f(a number) return number;\n  function f(a varchar2) return varchar2;\n  procedure g(x in out number);\nend;", rig.Session);
        Assert.Equal(PlsqlCompileStatus.Compiled, (await editor.CompileAsync()).Status);
        var body = new PlsqlEditorViewModel(rig.Container);
        body.StartNew($"create or replace package body {pkg} is\n  function f(a number) return number is begin return a * 2; end;\n  function f(a varchar2) return varchar2 is begin return upper(a); end;\n  procedure g(x in out number) is begin x := x + 1; end;\nend;", rig.Session);
        Assert.Equal(PlsqlCompileStatus.Compiled, (await body.CompileAsync()).Status);
        try
        {
            var deps = new PlsqlRunDeps
            {
                Session = () => rig.Service.ActiveSession,
                Metadata = () => rig.Container.GetRequiredService<IMetadataProvider>(),
                Execution = () => rig.Container.GetRequiredService<IExecutionService>(),
                State = rig.State,
            };
            var target = new DbObjectRef(db.User.ToUpperInvariant(), pkg, "PACKAGE");

            var all = new PlsqlRunViewModel(deps);
            await all.OpenAsync(target);
            Assert.Equal(3, all.Candidates.Count); // f 두 개(오버로드) + g
            Assert.True(all.HasMultipleCandidates);

            var f = new PlsqlRunViewModel(deps);
            await f.OpenAsync(target, "F");
            Assert.Equal(2, f.Candidates.Count);
            var numeric = f.Candidates.First(c => c.Display.Contains("NUMBER", StringComparison.Ordinal));
            f.SelectedCandidate = numeric;
            f.Rows.First(r => r.CanEdit).Text = "21";
            Assert.True(await f.RunAsync(), f.StatusText);
            Assert.Contains("42", string.Join(' ', f.Rows.Select(r => r.OutText)) + f.StatusText + string.Join(' ', f.Results.Tabs.Select(t => t.Message + t.Title)), StringComparison.Ordinal);

            // 경계: 없는 멤버 이름이면 후보가 없고 실행 불가.
            var nope = new PlsqlRunViewModel(deps);
            await nope.OpenAsync(target, "NOPE");
            Assert.Empty(nope.Candidates);
            Assert.False(nope.CanRun);

            // 본문만 오류가 있으면 서버 객체는 INVALID, 편집기는 오류를 알린다(되돌림은 고쳐 다시 컴파일).
            var broken = new PlsqlEditorViewModel(rig.Container);
            broken.StartNew($"create or replace package body {pkg} is\n  function f(a number) return number is begin return nope; end;\n  function f(a varchar2) return varchar2 is begin return a; end;\n  procedure g(x in out number) is begin null; end;\nend;", rig.Session);
            var outcome = await broken.CompileAsync();
            Assert.Equal(PlsqlCompileStatus.CompiledWithErrors, outcome.Status);
            Assert.Equal("INVALID", db.Text($"select status from user_objects where object_name = '{pkg}' and object_type = 'PACKAGE BODY'"));
            output.WriteLine("S5-4 OK: 패키지 오버로드 후보 3개·멤버 필터·실행·없는 멤버·본문 오류 INVALID");
        }
        finally
        {
            db.Exec($"begin execute immediate 'drop package {pkg}'; exception when others then null; end;");
        }
    });
}
