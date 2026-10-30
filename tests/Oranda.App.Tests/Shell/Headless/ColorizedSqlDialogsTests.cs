using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>SQL·PL/SQL 글을 보이는 대화상자 칸이 SQL 강조가 걸린 읽기 전용 코드 뷰인지 확인한다.</summary>
public sealed class ColorizedSqlDialogsTests
{
    private static SqlCodeView CodeViewIn(Window dialog)
    {
        Dispatcher.UIThread.RunJobs();
        var view = dialog.GetVisualDescendants().OfType<SqlCodeView>().Single();
        Assert.NotNull(view.Colorizer);
        Assert.True(view.TextEditorControl.IsReadOnly);
        return view;
    }

    /// <summary>주 창 위에 대화상자를 열고 안의 코드 뷰를 검사한 다음 닫는다.</summary>
    private static async Task<T> OpenAsync<T>(Func<AvaloniaExecutionDialogs, Task<T>> open, string expectedText)
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var dialogs = new AvaloniaExecutionDialogs(() => owner);
        var task = open(dialogs);
        Dispatcher.UIThread.RunJobs();
        var dialog = owner.OwnedWindows.Single();
        Assert.Contains(expectedText, CodeViewIn(dialog).TextEditorControl.Text, StringComparison.Ordinal);
        dialog.Close();
        var result = await task;
        owner.Close();
        return result;
    }

    [Fact]
    public Task Dml_preview_dialog_uses_the_sql_code_view() => HeadlessHost.Run(async () =>
        Assert.False(await OpenAsync(d => d.PreviewDmlAsync("UPDATE emp SET sal = 1", CancellationToken.None), "UPDATE emp")));

    [Fact]
    public Task Script_error_dialog_uses_the_sql_code_view() => HeadlessHost.Run(async () =>
    {
        var stmt = new SqlStatement("SELECT * FROM nope", default, StatementKind.Query);
        var action = await OpenAsync(d => d.AskScriptErrorAsync(stmt, new DbError("ORA-00942", "table or view does not exist"), CancellationToken.None), "FROM nope");
        Assert.Equal(ScriptErrorAction.Stop, action);
    });

    [Fact]
    public Task Plsql_run_preview_uses_the_sql_code_view() => HeadlessHost.Run(() =>
    {
        var vm = new PlsqlRunViewModel(new PlsqlRunDeps { Session = () => null, Metadata = () => null, Execution = () => throw new InvalidOperationException() });
        var window = new PlsqlRunWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var views = window.GetVisualDescendants().OfType<SqlCodeView>().ToList();
        Assert.Single(views);
        Assert.NotNull(views[0].Colorizer);
        window.Close();
    });

    [Fact]
    public Task Preview_edit_panel_colors_both_before_and_after() => HeadlessHost.Run(() =>
    {
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel("select 1 from dual", "select 2 from dual");
        var window = new Window { Content = panel, Width = 800, Height = 300 };
        window.Show();
        panel.Tabs.SelectedIndex = 1; // 「현재 / 제안」 탭
        Dispatcher.UIThread.RunJobs();
        var views = window.GetVisualDescendants().OfType<SqlCodeView>().ToList();
        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.NotNull(v.Colorizer));
        Assert.True(views[0].TextEditorControl.IsReadOnly); // 현재 칸은 읽기 전용, 제안 칸은 고칠 수 있다
        window.Close();
    });

    // ORANDA_RENDER_DIR 가 있을 때만 실행 확인 대화상자를 라이트·다크 PNG 로 뽑는다(렌더 점검용).
    [Fact]
    public Task Render_confirm_dialog() => HeadlessHost.Run(async () =>
    {
        var stmt = new SqlStatement("UPDATE emp e\n   SET e.sal = e.sal * 1.1, e.comm = nvl(e.comm, 0)\n WHERE e.deptno = :dept AND e.job = 'CLERK' -- 승급", default, StatementKind.Dml) { Verb = "UPDATE" };
        var decision = new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "운영 접속에서 데이터를 바꿉니다.");
        var prod = new Oranda.Core.Connection.ConnectionProfile("PROD-HR", "hr", "db:1521/ORCL", Environment: Oranda.Core.Connection.ConnectionEnvironment.Production);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var task = new AvaloniaExecutionDialogs(() => owner).ConfirmAsync(new ConfirmRequest(stmt, decision, prod), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        var dialog = owner.OwnedWindows.Single();
        dialog.UpdateLayout();
        MyBatisFlowRig.Render(dialog, "confirm-dialog", (int)Math.Ceiling(dialog.Bounds.Width), (int)Math.Ceiling(dialog.Bounds.Height));
        dialog.Close();
        await task;
        owner.Close();
    });
}
