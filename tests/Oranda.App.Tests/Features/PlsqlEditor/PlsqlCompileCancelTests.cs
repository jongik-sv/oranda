using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using static Oranda.App.Tests.Features.PlsqlEditor.PlsqlEditorTests;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>
/// 컴파일이 끝나지 않는 문제(plsql-compile-hang, PHC-1·2·4·5): 다른 세션이 실행 중인 객체를 컴파일하면 서버가 library cache 잠금을 기다린다.
/// 가짜 실행 엔진의 CREATE 가 토큰이 취소될 때까지 돌아오지 않게 해 취소·탭 닫기·대기 상한·세션 바쁨 표시를 본다.
/// </summary>
[Collection(Oranda.App.Tests.Features.PlsqlWorkspace.PlsqlStatics.Name)]
public sealed class PlsqlCompileCancelTests : IDisposable
{
    private static readonly DbObjectRef Proc = new("SCOTT", "P", "PROCEDURE");

    private const string ProcSource = "PROCEDURE p IS BEGIN\n  NULL;\nEND;\n";

    public PlsqlCompileCancelTests() => PlsqlEditorViewModel.Active = null;

    public void Dispose() => PlsqlEditorViewModel.Active = null;

    private static async Task<(PlsqlEditorViewModel Vm, FakeExec Exec, FakeMeta Meta, FakeSession Session)> OpenAsync(FakeExec? exec = null, FakeMeta? meta = null)
    {
        exec ??= new FakeExec();
        meta ??= new FakeMeta { Source = ProcSource };
        var session = new FakeSession();
        var vm = new PlsqlEditorViewModel(meta, exec);
        Assert.True(await vm.OpenAsync(session, Proc));
        return (vm, exec, meta, session);
    }

    // ---- PHC-1 취소 ----

    [Fact]
    public Task Cancel_button_stops_a_compile_waiting_on_the_server() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true });

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsCompiling);

        vm.CancelCompile();
        var outcome = await compile.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlsqlCompileStatus.Cancelled, outcome.Status);
        Assert.True(exec.CreateSawCancel);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsCompiling);
        Assert.Equal("컴파일이 취소됐다", vm.StatusText);
    });

    [Fact]
    public Task Caller_token_cancels_the_compile_too_and_cancel_without_compile_does_nothing() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true });
        vm.CancelCompile(); // 컴파일 중이 아니면 아무것도 안 한다

        using var cts = new CancellationTokenSource();
        var compile = vm.CompileAsync(ct: cts.Token);
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        Assert.Equal(PlsqlCompileStatus.Cancelled, (await compile.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.True(exec.CreateSawCancel);
    });

    [Fact]
    public Task Second_compile_while_one_runs_is_refused() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true });
        var first = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await vm.CompileAsync(force: true);

        Assert.Equal(PlsqlCompileStatus.Blocked, second.Status);
        Assert.Single(exec.Statements, s => s.Verb == "CREATE"); // 두 번째 CREATE 가 첫째 뒤에 줄 서지 않는다
        vm.CancelCompile();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
    });

    [Fact]
    public Task Closing_the_tab_or_its_workspace_document_cancels_the_compile() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var router = PlsqlWorkspaceRouter.Of(h.Services)!;
        var docking = h.Get<DockWorkspace>();

        // 탭 하나를 닫는다
        var (tab, exec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true });
        router.Place(tab);
        Dispatcher.UIThread.RunJobs();
        var workspace = router.WorkspaceOf(tab)!;
        var compile = tab.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        workspace.Remove(tab);
        Assert.Equal(PlsqlCompileStatus.Cancelled, (await compile.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.True(exec.CreateSawCancel);

        // 작업 공간 문서를 통째로 닫는다
        var (other, otherExec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true });
        var doc = router.Place(other)!;
        Dispatcher.UIThread.RunJobs();
        var second = other.CompileAsync();
        await otherExec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        docking.CloseDocument(doc);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PlsqlCompileStatus.Cancelled, (await second.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.True(otherExec.CreateSawCancel);
    });

    // ---- PHC-1 세션 실행 상태 ----

    [Fact]
    public Task Session_is_busy_while_compiling_and_idle_after_even_when_the_driver_throws() => Ui.Run(async () =>
    {
        var exec = new FakeExec();
        var (vm, _, _, session) = await OpenAsync(exec);
        var state = new ExecutionState();
        state.AddDedicated(session.Id, session.Profile);
        vm.State = state;
        bool? busyDuring = null;
        exec.OnCreate = () => busyDuring = state.IsBusy(session.Id);

        Assert.Equal(PlsqlCompileStatus.Compiled, (await vm.CompileAsync()).Status);
        Assert.True(busyDuring);
        Assert.False(state.IsBusy(session.Id));

        exec.CreateThrows = new InvalidOperationException("driver broke");
        busyDuring = null;
        var failed = await vm.CompileAsync(force: true);
        Assert.Equal(PlsqlCompileStatus.Failed, failed.Status);
        Assert.True(busyDuring);
        Assert.False(state.IsBusy(session.Id));
        Assert.False(state.CancelBackground(session.Id)); // 끝난 컴파일은 실행 취소에 남지 않는다
    });

    [Fact]
    public Task Execution_cancel_of_the_session_stops_the_compile() => Ui.Run(async () =>
    {
        var (vm, exec, _, session) = await OpenAsync(new FakeExec { BlockCreate = true });
        var state = new ExecutionState();
        state.AddDedicated(session.Id, session.Profile);
        vm.State = state;

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(state.IsBusy(session.Id));

        Assert.True(state.CancelBackground(session.Id)); // oranda.exec.cancel(세션 바쁨 막대·Ctrl+Break)가 부르는 길
        Assert.Equal(PlsqlCompileStatus.Cancelled, (await compile.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.False(state.IsBusy(session.Id));
    });

    [Fact]
    public Task Cancel_and_close_on_exit_or_disconnect_stops_the_compile() => Ui.Run(async () =>
    {
        // phc-exit: 앱 종료·접속 끊기의 「실행 중인 작업을 취소하고 닫기」는 맡겨 둔 취소(RegisterCancel)로 컴파일도 끊는다.
        using var rig = new Oranda.App.Tests.Features.Execution.Rig();
        rig.Controller.CancelWaitTimeout = TimeSpan.FromSeconds(5);
        Dispatcher.UIThread.RunJobs(); // SessionOpened 가 실행 상태에 세션을 등록한다
        var exec = new FakeExec { BlockCreate = true };
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = ProcSource }, exec) { State = rig.State };
        Assert.True(await vm.OpenAsync(rig.Session, Proc));

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rig.State.IsBusy(rig.Session.Id));

        Assert.True(await rig.Controller.ConfirmCancelRunningAsync());

        Assert.Single(rig.Dialogs.CancelRunningAsked);
        Assert.Equal(PlsqlCompileStatus.Cancelled, (await compile.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.True(exec.CreateSawCancel);
        Assert.False(rig.State.IsBusy(rig.Session.Id));
    });

    // ---- PHC-2 대기 상한 ----

    [Fact]
    public Task Compile_over_the_wait_limit_stops_with_timeout_and_explains_the_lock() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true });
        vm.CompileWaitOverride = TimeSpan.FromMilliseconds(200);

        var outcome = await vm.CompileAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlsqlCompileStatus.Timeout, outcome.Status); // 취소·실패와 다르다
        Assert.True(exec.CreateSawCancel);
        Assert.Contains("다른 세션이 이 객체를 실행 중이면 컴파일이 잠금을 기다립니다", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(outcome.Message, vm.WarningText); // 경고 띠에 보인다
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task Wait_limit_counts_from_sending_not_while_the_confirm_dialog_is_open() => Ui.Run(async () =>
    {
        // 확인 창을 읽는 동안(상한보다 오래)은 세지 않는다: 승인한 컴파일이 「시간 초과」로 버려지지 않는다.
        var (vm, _, _, _) = await OpenAsync(new FakeExec { ConfirmDelay = TimeSpan.FromMilliseconds(400) });
        vm.CompileWaitOverride = TimeSpan.FromMilliseconds(100);

        var outcome = await vm.CompileAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
    });

    [Fact]
    public Task Compile_is_refused_while_another_run_holds_the_session_and_keeps_its_cancel() => Ui.Run(async () =>
    {
        var (vm, exec, _, session) = await OpenAsync();
        var state = new ExecutionState();
        state.AddDedicated(session.Id, session.Profile);
        vm.State = state;
        var otherCancelled = false;
        state.BeginRun(session.Id, null); // 같은 접속에서 다른 실행(변경 묶음 등)이 돈다
        using var other = state.RegisterCancel(session.Id, () => otherCancelled = true);

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Blocked, outcome.Status);
        Assert.Contains("다른 문장이 실행 중", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(exec.Statements, s => s.Verb == "CREATE");
        Assert.True(state.CancelBackground(session.Id)); // 그 실행의 취소 방법이 그대로 남았다
        Assert.True(otherCancelled);
        Assert.True(state.IsBusy(session.Id)); // 남의 실행 표시를 끄지 않았다
        state.EndRun(session.Id);
    });

    [Fact]
    public Task A_preview_tab_that_starts_compiling_is_pinned_so_the_next_preview_does_not_cancel_it() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true });
        var workspace = new PlsqlWorkspaceViewModel { Name = "PL/SQL 1" };
        workspace.Add(vm, preview: true);
        Assert.True(vm.IsPreviewTab);

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.IsPreviewTab);

        var next = new PlsqlEditorViewModel();
        workspace.Add(next, preview: true); // 다음 객체를 한 번 눌러 미리보기
        Assert.Contains(vm, workspace.Tabs);
        Assert.False(compile.IsCompleted);

        vm.CancelCompile();
        await compile.WaitAsync(TimeSpan.FromSeconds(10));
    });

    [Fact]
    public void Wait_limit_setting_defaults_to_sixty_seconds()
    {
        Assert.Equal(60, new ExecutionSettings().PlsqlCompileWaitSeconds);
        Assert.Equal(60, ExecutionSettings.DefaultPlsqlCompileWaitSeconds);
    }

    // ---- PHC-4 사용 중 사전 확인 ----

    [Fact]
    public Task Users_check_without_privilege_warns_that_it_cannot_tell_but_still_compiles() => Ui.Run(async () =>
    {
        var exec = new FakeExec { UsersQueryError = new DbError("ORA-00942", "table or view does not exist") { Number = 942, Kind = DbErrorKind.ObjectNotFound } };
        var (vm, _, _, _) = await OpenAsync(exec);
        string? warningDuring = null;
        exec.OnCreate = () => warningDuring = vm.WarningText;

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Contains("다른 세션의 사용 여부를 확인할 수 없습니다", warningDuring, StringComparison.Ordinal);
        Assert.Contains("권한", warningDuring, StringComparison.Ordinal);
        Assert.Contains("확인하지 못했습니다", outcome.Message, StringComparison.Ordinal); // 조용히 「컴파일됨」 만 보이지 않는다
        Assert.Equal(string.Empty, vm.WarningText);
    });

    [Fact]
    public Task Users_check_that_does_not_answer_gives_up_after_the_precheck_limit() => Ui.Run(async () =>
    {
        var exec = new FakeExec { UsersQueryHangs = true };
        var (vm, _, _, _) = await OpenAsync(exec);
        vm.PrecheckLimit = TimeSpan.FromMilliseconds(100);
        string? warningDuring = null;
        exec.OnCreate = () => warningDuring = vm.WarningText;

        var outcome = await vm.CompileAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Contains("확인할 수 없습니다", warningDuring, StringComparison.Ordinal);
        Assert.Contains("답이 없음", warningDuring, StringComparison.Ordinal);
    });

    [Fact]
    public Task Account_with_only_local_v_access_still_gets_the_in_use_warning() => Ui.Run(async () =>
    {
        var exec = new FakeExec { GvAccessDenied = true, OtherSessions = 2 };
        var (vm, _, _, _) = await OpenAsync(exec);

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.InUse, outcome.Status);
        Assert.Contains(exec.Queries, q => q.Contains("gv$access", StringComparison.Ordinal));
        Assert.Contains(exec.Queries, q => q.Contains("FROM v$access", StringComparison.Ordinal));
    });

    [Fact]
    public Task Known_zero_users_gives_no_warning() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync();
        string? warningDuring = null;
        exec.OnCreate = () => warningDuring = vm.WarningText;

        var outcome = await vm.CompileAsync();

        Assert.Equal("컴파일됨", outcome.Message);
        Assert.Equal(string.Empty, warningDuring);
    });

    // ---- PHC-5 소유자·오류 목록 ----

    [Fact]
    public Task Header_without_owner_compiles_into_the_current_schema() => Ui.Run(async () =>
    {
        var exec = new FakeExec { SessionSchema = ("HR", "SCOTT") };
        var vm = new PlsqlEditorViewModel(new FakeMeta(), exec);
        vm.StartNew("CREATE OR REPLACE PROCEDURE hello IS BEGIN NULL; END;", new FakeSession());
        string? warningDuring = null;
        exec.OnCreate = () => warningDuring = vm.WarningText;

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Equal(new DbObjectRef("HR", "HELLO", "PROCEDURE"), vm.Target); // 로그인 계정(SCOTT)이 아니라 현재 스키마
        Assert.Contains("CREATE ANY PROCEDURE", warningDuring, StringComparison.Ordinal);
    });

    [Fact]
    public Task Owner_unlike_the_login_account_is_told_before_and_on_privilege_failure() => Ui.Run(async () =>
    {
        var exec = new FakeExec
        {
            CreateStatus = ExecutionStatus.Failed,
            CreateError = new DbError("ORA-01031", "insufficient privileges") { Number = 1031, Kind = DbErrorKind.InsufficientPrivileges },
        };
        var vm = new PlsqlEditorViewModel(new FakeMeta(), exec);
        vm.StartNew("CREATE OR REPLACE PACKAGE \"OTHER\".pk AS END;", new FakeSession());
        string? warningDuring = null;
        exec.OnCreate = () => warningDuring = vm.WarningText;

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Failed, outcome.Status);
        Assert.Contains("OTHER", warningDuring, StringComparison.Ordinal);
        Assert.Contains("CREATE ANY PROCEDURE", warningDuring, StringComparison.Ordinal);
        Assert.Contains("ORA-01031", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("CREATE ANY PROCEDURE", outcome.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Owner_equal_to_the_login_account_needs_no_schema_query() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync();

        await vm.CompileAsync();

        Assert.DoesNotContain(exec.Queries, q => q.Contains("CURRENT_SCHEMA", StringComparison.Ordinal));
        Assert.Single(exec.Queries); // 다른 세션 수만
    });

    [Fact]
    public Task Error_list_read_failure_is_not_reported_as_plain_compiled() => Ui.Run(async () =>
    {
        var meta = new FakeMeta { Source = ProcSource };
        var (vm, _, _, _) = await OpenAsync(meta: meta);
        meta.ErrorsFailure = new InvalidOperationException("ORA-00942 all_errors");

        var outcome = await vm.CompileAsync();

        Assert.NotEqual("컴파일됨", outcome.Message);
        Assert.Contains("오류 목록을 읽지 못했습니다", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("all_errors", outcome.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Driver_reporting_compiled_with_errors_reads_the_error_list() => Ui.Run(async () =>
    {
        var meta = new FakeMeta { Source = ProcSource };
        var exec = new FakeExec
        {
            CreateStatus = ExecutionStatus.Failed,
            CreateError = new DbError("ORA-24344", "success with compilation error") { Number = 24344, Kind = DbErrorKind.CompiledWithErrors },
        };
        var (vm, _, _, _) = await OpenAsync(exec, meta);
        meta.Errors = [new CompileError(2, 3, "PLS-00201: identifier 'X' must be declared", false)];

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.CompiledWithErrors, outcome.Status);
        Assert.Single(outcome.Errors);
        Assert.Single(vm.Errors);
        Assert.Contains("오류 1건", outcome.Message, StringComparison.Ordinal);

        // 오류 목록이 비어도(다른 소유자로 읽음 등) 서버가 알린 오류를 「컴파일됨」 으로 바꾸지 않는다.
        meta.Errors = [];
        var empty = await vm.CompileAsync(force: true);
        Assert.Equal(PlsqlCompileStatus.CompiledWithErrors, empty.Status);
    });

    // ---- 화면(헤드리스) ----

    [Fact]
    public Task Overwrite_button_is_off_and_cancel_button_shows_while_compiling() => Ui.Run(async () =>
    {
        var (vm, exec, _, _) = await OpenAsync(new FakeExec { BlockCreate = true, OtherSessions = 1 });
        var view = new PlsqlEditorView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 500 };
        window.Show();
        try
        {
            Assert.Equal(PlsqlCompileStatus.InUse, (await vm.CompileAsync()).Status); // 경고 띠와 「무시하고 덮어쓰기」 가 보인다
            Dispatcher.UIThread.RunJobs();
            var force = view.FindControl<Button>("ForceButton")!;
            var cancel = view.FindControl<Button>("CancelCompileButton")!;
            Assert.True(force.IsEffectivelyEnabled);
            Assert.False(cancel.IsVisible);

            var compile = vm.CompileAsync(force: true);
            await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();
            Assert.False(force.IsEffectivelyEnabled); // 두 번 눌러 두 번째 CREATE 가 줄 서지 않게
            Assert.True(cancel.IsVisible);

            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(PlsqlCompileStatus.Cancelled, (await compile.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(cancel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
