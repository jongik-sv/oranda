using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// 여러 문장 스크립트 실행 중 지금 실행하는 문장으로 커서를 옮기고 그 범위를 칠한다(실행 중 문장 따라가기).
/// 끝·취소면 표시를 지우고, 오류로 멈추면 실패한 문장에 커서(오류 자리)와 오류 표시를 남긴다. 다른 탭은 건드리지 않는다.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ScriptRunFollowTests
{
    // 문장 시작: 0, 20, 40 (각 문장 19글자 + 줄바꿈)
    private const string Script = "select 1 from dual;\nselect 2 from dual;\nselect 3 from dual;";

    private static TextRange Range2 => new(20, 18);

    private static bool IsSecond(SqlStatement s) => s.Text.Contains('2', StringComparison.Ordinal);

    /// <summary>문장 2 는 문 앞에서 기다리고, 나머지는 바로 성공한다.</summary>
    private static TaskCompletionSource<ExecutionResult> GateSecond(Rig rig)
    {
        var gate = new TaskCompletionSource<ExecutionResult>();
        rig.Exec.Handler = (s, _) => IsSecond(s) ? gate.Task : Task.FromResult(Rig.Query(s));
        return gate;
    }

    [Fact]
    public Task The_caret_and_mark_follow_the_running_statement_and_the_mark_is_gone_at_the_end() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument(Script);
        tab.Select(0, 0);
        var gate = GateSecond(rig);

        var run = rig.Controller.RunAsync(RunMode.Script);
        await HeadlessHost.WaitUntil(() => rig.Exec.Statements.Count == 2 && tab.CaretOffset == 20);

        var mark = Assert.IsType<RunningStatementMark>(tab.RunningStatement);
        Assert.False(mark.Failed);
        Assert.Equal([Range2], mark.Ranges);
        Assert.Equal(Script, tab.Text); // 글은 바꾸지 않는다

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        Assert.True(await run);
        Assert.Equal(3, rig.Exec.Statements.Count);
        Assert.Null(tab.RunningStatement);
        Assert.InRange(tab.CaretOffset, 20, 40); // 마지막으로 옮긴 문장(2 또는 모아 그린 3)의 시작
    });

    [Fact]
    public Task An_error_stop_leaves_the_caret_at_the_error_and_a_failed_mark_until_typing() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument(Script);
        rig.State.ErrorMode = ScriptErrorMode.Stop;
        rig.Exec.Handler = (s, _) => Task.FromResult(IsSecond(s)
            ? new ExecutionResult(s, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942, Offset = 14 } }
            : Rig.Query(s));

        await rig.Controller.RunAsync(RunMode.Script);

        Assert.Equal(2, rig.Exec.Statements.Count); // 문장 3 은 보내지 않았다
        var mark = Assert.IsType<RunningStatementMark>(tab.RunningStatement);
        Assert.True(mark.Failed);
        Assert.Equal([Range2], mark.Ranges);
        Assert.Equal(20 + 14, tab.CaretOffset); // 오류 자리(「오류로 이동」 과 같은 위치)

        tab.Document.Insert(tab.Document.TextLength, "\n");
        Assert.Null(tab.RunningStatement); // 글이 바뀌면 지운다
    });

    [Fact]
    public Task A_new_run_clears_the_previous_failed_mark() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument(Script);
        rig.State.ErrorMode = ScriptErrorMode.Stop;
        rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "x") });
        await rig.Controller.RunAsync(RunMode.Script);
        Assert.True(tab.RunningStatement!.Failed);

        var gate = new TaskCompletionSource<ExecutionResult>();
        rig.Exec.Handler = (_, _) => gate.Task;
        tab.Select(25, 0);
        var run = rig.Controller.RunAsync(RunMode.Statement); // 한 문장 실행: 따라가기는 없지만 지난 표시는 지운다
        await HeadlessHost.WaitUntil(() => rig.State.Busy);
        Assert.Null(tab.RunningStatement);
        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;
        Assert.Equal(25, tab.CaretOffset); // 한 문장 실행은 커서를 옮기지 않는다
        Assert.Null(tab.RunningStatement);
    });

    [Fact]
    public Task Cancel_clears_the_mark_and_keeps_the_caret_on_the_cancelled_statement() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument(Script);
        rig.Exec.Handler = async (s, ct) =>
        {
            if (!IsSecond(s))
            {
                return Rig.Query(s);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            return new ExecutionResult(s, ExecutionStatus.Cancelled);
        };

        var run = rig.Controller.RunAsync(RunMode.Script);
        await HeadlessHost.WaitUntil(() => rig.Exec.Statements.Count == 2 && tab.RunningStatement is { Failed: false } m && m.Ranges[0] == Range2);
        rig.Controller.Cancel();
        Assert.False(await run);

        Assert.Null(tab.RunningStatement);
        Assert.Equal(20, tab.CaretOffset);
    });

    [Fact]
    public Task Another_tab_is_not_touched_while_the_first_tab_runs() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument(Script);
        a.Select(0, 0);
        var gate = GateSecond(rig);
        var run = rig.Controller.RunAsync(RunMode.Script);
        await HeadlessHost.WaitUntil(() => rig.Exec.Statements.Count == 2 && a.CaretOffset == 20);

        var b = rig.Host.NewDocument(Script);
        rig.Host.Active = b;
        b.Select(3, 0);

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;

        Assert.Equal(3, b.CaretOffset);
        Assert.Null(b.RunningStatement);
        Assert.Same(b, rig.Host.Active); // 활성 탭을 되돌리지 않는다
        Assert.InRange(a.CaretOffset, 20, 40);
    });

    [Fact]
    public Task A_one_statement_script_does_not_move_the_caret() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("\n\nselect 1 from dual;");
        tab.Select(0, 0);
        var seen = false;
        tab.RunningStatementChanged += (_, _) => seen = true;

        Assert.True(await rig.Controller.RunAsync(RunMode.Script));

        Assert.Equal(0, tab.CaretOffset);
        Assert.False(seen);
    });

    [Fact]
    public Task Updates_inside_the_interval_are_coalesced_to_the_latest_statement() => Ui.Run(() =>
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), Script);
        var stmts = new[]
        {
            new SqlStatement("select 1 from dual", new TextRange(0, 18), StatementKind.Query),
            new SqlStatement("select 2 from dual", new TextRange(20, 18), StatementKind.Query),
            new SqlStatement("select 3 from dual", new TextRange(40, 18), StatementKind.Query),
        };
        var now = TimeSpan.Zero;
        var changes = 0;
        tab.RunningStatementChanged += (_, _) => changes++;
        var follow = new ScriptRunFollower(tab, stmts, () => now);

        follow.Show(0);
        Assert.Equal(0, tab.CaretOffset);
        Assert.Equal(1, changes);

        now = TimeSpan.FromMilliseconds(5);
        follow.Show(1);
        now = TimeSpan.FromMilliseconds(10);
        follow.Show(2);
        Assert.Equal(0, tab.CaretOffset); // 아직 간격 안: 모아 둔다
        Assert.Equal(1, changes);

        follow.Flush();
        Assert.Equal(40, tab.CaretOffset); // 마지막 것 하나만 그린다
        Assert.Equal(2, changes);

        now = TimeSpan.FromMilliseconds(100);
        follow.End();
        Assert.Null(tab.RunningStatement);
        follow.Show(1); // 끝난 뒤의 갱신은 무시
        Assert.Null(tab.RunningStatement);
        Assert.Equal(40, tab.CaretOffset);
    });

    [Fact]
    public Task Editing_during_the_run_stops_following() => Ui.Run(() =>
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), Script);
        var stmts = new[]
        {
            new SqlStatement("select 1 from dual", new TextRange(0, 18), StatementKind.Query),
            new SqlStatement("select 2 from dual", new TextRange(20, 18), StatementKind.Query),
        };
        var follow = new ScriptRunFollower(tab, stmts, () => TimeSpan.FromSeconds(1));
        follow.Show(0);
        tab.Document.Insert(0, "-- x\n");
        Assert.Null(tab.RunningStatement);
        int caret = tab.CaretOffset;

        follow.Show(1);
        follow.Fail(stmts[1], 3);
        Assert.Null(tab.RunningStatement);
        Assert.Equal(caret, tab.CaretOffset);
    });

    [Fact]
    public Task The_view_paints_the_mark_with_a_theme_aware_brush_and_drops_it_on_detach() => Ui.Run(() =>
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), Script);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var renderer = Assert.IsType<RunningStatementRenderer>(view.Running);
        Assert.Contains(renderer, view.TextEditorControl.TextArea.TextView.BackgroundRenderers);
        vm.SetRunningStatement([new TextRange(20, 18), new TextRange(50, 1000)], failed: false);
        Assert.Equal([new TextRange(20, 18), new TextRange(50, Script.Length - 50)], vm.RunningStatement!.Ranges); // 문서 안으로 자른다
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(RunningStatementRenderer.FillFor(false, dark: false), RunningStatementRenderer.FillFor(false, dark: true));
        Assert.NotSame(RunningStatementRenderer.FillFor(false, dark: false), RunningStatementRenderer.FillFor(true, dark: false));

        view.DataContext = null;
        Assert.DoesNotContain(renderer, view.TextEditorControl.TextArea.TextView.BackgroundRenderers);
        window.Close();
    });
}
