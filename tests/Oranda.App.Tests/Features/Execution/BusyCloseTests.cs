using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Oranda.App.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Events;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// PHC-3·3b: 세션에서 명령이 도는 중(누가 명령 잠금을 쥠 또는 실행 중)에 앱 종료·접속 끊기를 하면 미커밋 확인보다 먼저 「취소하고 닫기」를 묻고,
/// 닫는 경로의 커밋·롤백은 잠금 대기까지 넣어 상한 안에 돌아온다(멈춘 DDL 뒤에 끝없이 줄 서지 않는다).
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class BusyCloseTests
{
    /// <summary>상한을 넘었는지 볼 넉넉한 시한(시험 PC 부하). 상한은 수백 ms 로 줄여 둔다.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private static Rig NewRig()
    {
        var rig = new Rig();
        rig.Controller.CancelWaitTimeout = TimeSpan.FromMilliseconds(300);
        rig.Controller.CloseEndTimeout = TimeSpan.FromMilliseconds(300);
        rig.Controller.BusyGrace = TimeSpan.FromMilliseconds(100);
        return rig;
    }

    private static void Pending(Rig rig, bool pending = true)
    {
        rig.Session.HasPendingTransaction = pending;
        rig.Bus.Publish(new TransactionStateChanged(rig.Session.Id, pending));
    }

    /// <summary>잠금 뒤에 줄 선 커밋·롤백 흉내: 토큰이 취소될 때까지 끝나지 않는다.</summary>
    private static Task WaitForGate(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    [Fact]
    public Task A_session_that_is_not_busy_keeps_the_old_close_flow() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig);

        Assert.True(await rig.Controller.ConfirmCancelRunningAsync());
        Assert.Empty(rig.Dialogs.CancelRunningAsked);

        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Rollback;
        Assert.True(await rig.Controller.ConfirmCloseAsync());
        Assert.Equal(1, rig.Dialogs.UncommittedAsked);
        Assert.Equal(1, rig.Session.Rollbacks);
        Assert.False(rig.State.TxnPending);
    });

    [Fact]
    public Task A_command_holding_the_gate_is_asked_first_and_cancel_and_close_finishes_within_the_limit() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig);
        rig.Session.CommandRunning = true; // 멈춘 DDL(컴파일)이 잠금을 쥐고 있다, 취소 방법도 맡기지 않았다
        rig.Session.EndWait = WaitForGate;
        var watch = Stopwatch.StartNew();

        Assert.True(await rig.Controller.ConfirmCancelRunningAsync());
        Assert.True(await rig.Controller.ConfirmCloseAsync());

        Assert.True(watch.Elapsed < Generous, $"{watch.Elapsed} 걸림");
        var asked = Assert.Single(rig.Dialogs.CancelRunningAsked);
        Assert.Equal(["scott@dev"], asked.Connections);
        Assert.True(asked.Uncommitted); // DDL 뒤의 커밋 여부는 알 수 없다고 밝힌다(PHC-3b)
        Assert.Equal(0, rig.Dialogs.UncommittedAsked); // 기다리지 않고 닫기로 한 세션에 커밋·롤백을 묻지 않는다(그 뒤에 줄 선다)
        Assert.Equal(0, rig.Session.Commits + rig.Session.Rollbacks);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains("기다리지 않고 닫습니다", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_stale_abandoned_mark_does_not_skip_a_later_uncommitted_question() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        rig.Session.CommandRunning = true;
        Assert.True(await rig.Controller.ConfirmCancelRunningAsync()); // 기다리지 않고 닫기로 했다
        rig.Controller.ForgetAbandoned(); // 그런데 종료를 그만뒀다(다른 질문에서 취소)
        rig.Session.CommandRunning = false; // 그 명령은 끝났고
        Pending(rig); // 새 변경이 생겼다

        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Rollback;
        Assert.True(await rig.Controller.ConfirmCloseAsync());
        Assert.Equal(1, rig.Dialogs.UncommittedAsked);
        Assert.Equal(1, rig.Session.Rollbacks);
    });

    [Fact]
    public Task An_abandoned_session_with_a_brief_gate_hold_is_still_asked() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig);
        rig.Session.CommandRunning = true;
        Assert.True(await rig.Controller.ConfirmCancelRunningAsync());
        rig.Session.CommandRunning = false; // 명령이 끝났다
        rig.Controller.BusyGrace = Generous;
        rig.Session.CommandRunning = true; // 묻는 순간 Keep Alive 가 잠깐 잠금을 쥐었다
        _ = Task.Delay(50).ContinueWith(_ => rig.Session.CommandRunning = false, TaskScheduler.Default);

        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Rollback;
        Assert.True(await rig.Controller.ConfirmCloseAsync());
        Assert.Equal(1, rig.Dialogs.UncommittedAsked); // 말없이 롤백하지 않는다
        Assert.Equal(1, rig.Session.Rollbacks);
    });

    [Fact]
    public Task Cancel_and_close_breaks_the_run_and_then_asks_with_the_fresh_transaction_state() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig); // 앱 표시는 미커밋(낡음): 도는 DDL 이 서버에서 이미 커밋했다
        rig.State.BeginRun(rig.Session.Id);
        var cancels = 0;
        using var _ = rig.State.RegisterCancel(rig.Session.Id, () =>
        {
            cancels++;
            Pending(rig, false); // DDL 이 돌아오며 미커밋 표시가 맞춰진다
            rig.State.EndRun(rig.Session.Id);
        });

        Assert.True(await rig.Controller.ConfirmCancelRunningAsync());
        Assert.Equal(1, cancels);
        Assert.True(Assert.Single(rig.Dialogs.CancelRunningAsked).Uncommitted);

        Assert.True(await rig.Controller.ConfirmCloseAsync());
        Assert.Equal(0, rig.Dialogs.UncommittedAsked); // 미커밋이 없어졌으니 묻지 않는다
        Assert.DoesNotContain(rig.Log.Messages, m => m.Text.Contains("기다리지 않고 닫습니다", StringComparison.Ordinal));
    });

    [Fact]
    public Task Going_back_cancels_nothing() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        rig.State.BeginRun(rig.Session.Id);
        var cancels = 0;
        using var _ = rig.State.RegisterCancel(rig.Session.Id, () => cancels++);
        rig.Dialogs.CancelRunningAnswer = false;

        Assert.False(await rig.Controller.ConfirmCancelRunningAsync());

        Assert.Equal(0, cancels);
        Assert.True(rig.State.IsRunning(rig.Session.Id));
        Assert.False(Assert.Single(rig.Dialogs.CancelRunningAsked).Uncommitted);
        rig.State.EndRun(rig.Session.Id);
    });

    [Fact]
    public Task A_briefly_held_gate_is_not_asked() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        rig.Controller.BusyGrace = Generous;
        rig.Session.CommandRunning = true; // 메타데이터 읽기 같은 짧은 잠금
        _ = Task.Delay(50).ContinueWith(_ => rig.Session.CommandRunning = false, TaskScheduler.Default);

        Assert.True(await rig.Controller.ConfirmCancelRunningAsync());
        Assert.Empty(rig.Dialogs.CancelRunningAsked);
    });

    [Fact]
    public Task Commit_on_close_returns_within_the_limit_and_asks_whether_to_close_without_committing() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig);
        rig.Session.EndWait = WaitForGate; // 잠금을 다른 명령이 쥐고 있다
        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Commit;
        var watch = Stopwatch.StartNew();

        Assert.False(await rig.Controller.ConfirmConnectionCloseAsync(rig.Session)); // 기본 = 돌아가기

        Assert.True(watch.Elapsed < Generous, $"{watch.Elapsed} 걸림");
        Assert.Equal(["scott@dev"], rig.Dialogs.CloseWithoutCommitAsked);
        Assert.Equal(0, rig.Session.Commits);
        Assert.True(rig.State.TxnPending);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains("커밋하지 못했습니다", StringComparison.Ordinal));

        rig.Dialogs.CloseWithoutCommitAnswer = true;
        Assert.True(await rig.Controller.ConfirmConnectionCloseAsync(rig.Session)); // 커밋하지 않고 닫기
    });

    [Fact]
    public Task Rollback_on_close_that_times_out_closes_anyway() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig);
        rig.Session.EndWait = WaitForGate;
        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Rollback;
        var watch = Stopwatch.StartNew();

        Assert.True(await rig.Controller.ConfirmCloseAsync());

        Assert.True(watch.Elapsed < Generous, $"{watch.Elapsed} 걸림");
        Assert.Empty(rig.Dialogs.CloseWithoutCommitAsked); // 롤백 실패는 묻지 않고 닫는다(서버가 되돌린다)
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains("롤백하지 못했습니다", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_commit_that_fails_within_the_limit_stays_open_as_before() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig);
        rig.Session.EndWait = _ => Task.FromException(new InvalidOperationException("ORA-00000 실패 흉내"));
        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Commit;

        Assert.False(await rig.Controller.ConfirmCloseAsync());

        Assert.Empty(rig.Dialogs.CloseWithoutCommitAsked);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Error && m.Text.StartsWith("커밋 실패", StringComparison.Ordinal));
    });

    [Fact]
    public Task Paths_that_keep_the_session_still_wait_for_a_slow_commit() => Ui.Run(async () =>
    {
        using var rig = NewRig();
        Pending(rig);
        rig.Session.EndWait = _ => Task.Delay(600, CancellationToken.None); // 닫는 경로 상한(300 ms)보다 길다: 바꾸기는 세션이 남으므로 끝까지 기다린다
        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Commit;

        Assert.True(await rig.Controller.ConfirmSwitchAsync(rig.Session));
        Assert.Equal(1, rig.Session.Commits);

        Pending(rig); // 가져오기 전 확인(닫지 않는 경로)도 지금처럼 끝까지 기다린다
        Assert.True(await rig.Controller.ConfirmConnectionUncommittedAsync(rig.Session));
        Assert.Equal(2, rig.Session.Commits);
        Assert.Empty(rig.Dialogs.CloseWithoutCommitAsked);
    });

    [Fact]
    public void The_busy_close_text_explains_the_ddl_commit_only_when_uncommitted()
    {
        var withPending = ExecutionDialogText.BusyClose(["1 - scott@dev"], uncommitted: true, TimeSpan.FromSeconds(5));
        var clean = ExecutionDialogText.BusyClose(["1 - scott@dev"], uncommitted: false, TimeSpan.FromSeconds(5));

        Assert.Contains("「1 - scott@dev」", withPending, StringComparison.Ordinal);
        Assert.Contains("취소하고 닫을까요?", withPending, StringComparison.Ordinal);
        Assert.Contains("5초", withPending, StringComparison.Ordinal);
        Assert.Contains("DDL", withPending, StringComparison.Ordinal);
        Assert.DoesNotContain("DDL", clean, StringComparison.Ordinal);
    }

    [Fact]
    public Task The_cancel_and_close_dialog_defaults_to_going_back() => Ui.Run(() =>
    {
        var dialog = AvaloniaExecutionDialogs.CreateCancelRunningDialog(["1 - scott@dev"], uncommitted: true, TimeSpan.FromSeconds(5));
        var buttons = dialog.GetLogicalDescendants().OfType<Button>().ToList();

        var back = Assert.Single(buttons, b => b.Name == "BackButton");
        var proceed = Assert.Single(buttons, b => b.Name == "ProceedButton");
        Assert.True(back.IsDefault);
        Assert.True(back.IsCancel);
        Assert.Equal("돌아가기", back.Content);
        Assert.Equal("취소하고 닫기", proceed.Content);
        Assert.False(proceed.IsDefault);
        return Task.CompletedTask;
    });
}
