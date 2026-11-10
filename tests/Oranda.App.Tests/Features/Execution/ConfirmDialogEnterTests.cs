using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// 실행 확인 창의 이름 재입력(DROP 등 위험 명령) 칸에서 친 Enter·Esc. 사용자 보고(2026-11-11):
/// 이름을 맞게 치고 Enter 를 누르면 기본 단추 「취소」 가 눌려 창이 취소되었다.
/// 이제 맞으면 실행, 틀리면 창을 둔 채 알리고, Esc 는 취소다. 재입력이 없는 확인 창의 Enter 는 그대로 「취소」(ui-shell.md 4.3).
/// </summary>
public sealed class ConfirmDialogEnterTests
{
    private const string Target = "T_EMP";

    private static ConfirmRequest DropRequest()
    {
        var stmt = new SqlStatement($"DROP TABLE {Target}", default, StatementKind.Ddl) { Verb = "DROP", DangerousTarget = Target };
        var decision = new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.DangerousCommand, "위험 명령 DROP 입니다.") { RetypeText = Target };
        return new ConfirmRequest(stmt, decision, new ConnectionProfile("TEST-HR", "hr", "db:1521/ORCL", Environment: ConnectionEnvironment.Test));
    }

    private static ConfirmRequest UpdateRequest()
    {
        var stmt = new SqlStatement("UPDATE emp SET sal = 1 WHERE id = 1", default, StatementKind.Dml) { Verb = "UPDATE" };
        var decision = new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "운영 접속에서 데이터를 바꿉니다.");
        return new ConfirmRequest(stmt, decision, new ConnectionProfile("PROD-HR", "hr", "db:1521/ORCL", Environment: ConnectionEnvironment.Production));
    }

    /// <summary>주 창 위에 실행 확인 창을 연다. 열린 창과 결과 작업을 돌려준다.</summary>
    private static (Window Owner, Window Dialog, Task<bool> Answer) Open(ConfirmRequest request)
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var answer = new AvaloniaExecutionDialogs(() => owner).ConfirmAsync(request, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        return (owner, owner.OwnedWindows.Single(), answer);
    }

    private static TextBox RetypeBox(Window dialog) => dialog.GetVisualDescendants().OfType<TextBox>().Single();

    private static void Press(Window dialog, Key key, PhysicalKey physical)
    {
        // 누름만 보낸다: 눌림에 창이 닫히면 뗌을 보낼 창이 없다.
        dialog.KeyPress(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Enter_with_the_matching_name_runs() => HeadlessHost.Run(async () =>
    {
        var (owner, dialog, answer) = Open(DropRequest());
        var box = RetypeBox(dialog);
        Assert.True(box.IsFocused); // 재입력 칸에서 바로 칠 수 있다

        dialog.KeyTextInput("t_emp"); // 대소문자는 가리지 않는다
        Dispatcher.UIThread.RunJobs();
        Press(dialog, Key.Enter, PhysicalKey.Enter);

        Assert.True(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(dialog, owner.OwnedWindows);
        owner.Close();
    });

    [Fact]
    public Task Enter_with_a_wrong_name_keeps_the_dialog_open_and_esc_cancels() => HeadlessHost.Run(async () =>
    {
        var (owner, dialog, answer) = Open(DropRequest());
        RetypeBox(dialog).Focus();

        dialog.KeyTextInput("T_EM");
        Dispatcher.UIThread.RunJobs();
        Press(dialog, Key.Enter, PhysicalKey.Enter);

        Assert.False(answer.IsCompleted); // 취소되지 않았다
        Assert.Contains(dialog, owner.OwnedWindows);
        var mismatch = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "입력한 이름이 맞지 않습니다.");
        Assert.True(mismatch.IsVisible);

        // 마저 치면 알림이 사라지고, Esc 는 취소다(이름이 맞아도).
        dialog.KeyTextInput("P");
        Dispatcher.UIThread.RunJobs();
        Assert.False(mismatch.IsVisible);
        Press(dialog, Key.Escape, PhysicalKey.Escape);

        Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(dialog, owner.OwnedWindows);
        owner.Close();
    });

    [Fact]
    public Task Esc_on_a_fresh_retype_dialog_cancels() => HeadlessHost.Run(async () =>
    {
        var (owner, dialog, answer) = Open(DropRequest());

        Press(dialog, Key.Escape, PhysicalKey.Escape);

        Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        owner.Close();
    });

    [Fact]
    public Task Enter_on_a_confirm_without_retype_still_presses_cancel() => HeadlessHost.Run(async () =>
    {
        var (owner, dialog, answer) = Open(UpdateRequest());
        Assert.Empty(dialog.GetVisualDescendants().OfType<TextBox>());

        Press(dialog, Key.Enter, PhysicalKey.Enter);

        Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5))); // 기본 단추는 「취소」(ui-shell.md 4.3)
        owner.Close();
    });
}
