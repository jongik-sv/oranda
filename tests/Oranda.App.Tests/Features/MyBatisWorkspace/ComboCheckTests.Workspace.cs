using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

// 실제 MyBatis 작업 공간(03-01 세션·「문장」 창·문장 편집기)에 붙인 조합 검사: 배지, 원인 줄 칠하기, 빠른 고침 단추, 고친 뒤 배지 사라짐.
public partial class ComboCheckTests
{
    private static async Task<(MyBatisStatementsViewModel Statements, MyBatisWorkspaceSession Session, string Root)> OpenEmpMapperAsync(MyBatisHooks hooks)
    {
        var root = Path.Combine(Path.GetTempPath(), "oranda-combos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "EmpMapper.xml");
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
        var host = new SqlEditorHost { MyBatisHooks = hooks };
        var statements = new MyBatisStatementsViewModel { Host = host, Text = new ShellText() };
        Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
        return (statements, Assert.IsType<MyBatisWorkspaceSession>(host.ActiveWorkspace!.MyBatis), root);
    }

    [Fact]
    public Task Workspace_shows_the_badge_error_lines_and_quick_fix_until_the_statement_is_fixed() => HeadlessHost.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var hooks = new MyBatisHooks(editorViewFactory: factory, editorDecorators: [new AutoFixDecorator()], badgeProviders: [new ComboStatementBadgeProvider()]);
        var (statements, session, root) = await OpenEmpMapperAsync(hooks);
        Window? window = null;
        try
        {
            var tab = session.OpenStatement(Id)!;
            var view = Assert.IsType<MyBatisStatementEditorView>(factory.CreateEditor(tab, session));
            window = new Window { Width = 900, Height = 600, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 검사 전: 고칠 모양이 있으니 빠른 고침 단추는 보이고, 배지·원인 줄은 없다.
            var button = Assert.Single(view.HeaderExtraSlot.Children.OfType<Button>());
            Assert.True(button.IsVisible);
            Assert.Equal("빠른 고침 · 줄 3–9: <set> 으로 고치기", button.Content);
            Assert.Empty(Node(statements).Badges);

            var panel = Panel();
            await panel.CheckAsync(session, Id);

            Assert.Equal(2, panel.ErrorCount);
            Assert.Equal("조합 오류 2", Assert.Single(Node(statements).Badges).Text);
            Assert.Equal([3, 5], ComboCheckResults.ErrorLines(session, Id));

            // 미리보기 뒤 적용 → 세션이 고친 원문을 돌려주므로 배지·원인 줄·빠른 고침이 사라지고, 다시 검사하면 모두 통과한다.
            await panel.PreviewAsync(panel.Fixes[0]);
            await panel.ApplyFixCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("<set>", session.ElementOf(Id)!.Text, StringComparison.Ordinal);
            Assert.Equal((4, 0), (panel.PassCount, panel.ErrorCount));
            Assert.Empty(Node(statements).Badges);
            Assert.Empty(ComboCheckResults.ErrorLines(session, Id));
            Assert.False(button.IsVisible);
        }
        finally
        {
            window?.Close();
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Editing_after_a_check_hides_the_badge_until_the_next_check() => HeadlessHost.Run(async () =>
    {
        var (statements, session, root) = await OpenEmpMapperAsync(new MyBatisHooks(badgeProviders: [new ComboStatementBadgeProvider()]));
        try
        {
            var panel = Panel();
            await panel.CheckAsync(session, Id);
            Assert.Single(Node(statements).Badges);

            var tab = session.OpenStatement(Id)!;
            tab.Document.Insert(tab.Document.TextLength - "</update>".Length, "  ");
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(Node(statements).Badges);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    private static MyBatisStatementNode Node(MyBatisStatementsViewModel vm) =>
        vm.Groups.SelectMany(g => g.Children).First(n => n.StatementId == Id);
}
