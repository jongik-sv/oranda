using Avalonia.Threading;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>
/// PL/SQL 편집기에 포커스가 있을 때의 공용 단축키: 저장(Primary+S)은 숨은 SQL 탭이 아니라 그 편집기를 저장하고,
/// 실행(Primary+Enter, <c>oranda.plsql.run</c>)은 그 편집기의 객체를 대상으로 켜진다.
/// </summary>
public class PlsqlShortcutTests
{
    [Fact]
    public Task Save_writes_the_focused_plsql_editor_not_the_hidden_sql_tab() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        Dispatcher.UIThread.RunJobs();
        var sql = SqlEditorHost.Current.Active!;
        sql.Document.Text = "select 1 from dual";

        var path = Path.Combine(h.DataDirectory, "p.sql");
        await File.WriteAllTextAsync(path, string.Empty);
        var plsql = new PlsqlEditorViewModel();
        plsql.Editor.FilePath = path;
        plsql.Editor.Document.Text = "create or replace procedure p is begin null; end;";
        h.Get<DockWorkspace>().OpenDocument("oranda.panel.plsqlEditor", plsql, "P"); // 실제 앱처럼 도킹 문서로 연다
        Dispatcher.UIThread.RunJobs();
        PlsqlEditorViewModel.Active = plsql;
        h.Get<ContextKeyService>().SetFocusSlot("plsql");
        try
        {
            await registry.ExecuteAsync("oranda.file.save", keys);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(plsql.Editor.Text, await File.ReadAllTextAsync(path));
            Assert.False(plsql.Editor.IsDirty);
            Assert.True(sql.IsDirty); // 숨은 SQL 탭은 저장하지 않는다(파일 대화상자도 열지 않는다)
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task Run_is_enabled_for_a_focused_editor_holding_a_runnable_object() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var keys = h.Get<IContextKeys>();
        var procedure = PlsqlEditorTests.OpenedProcedure();
        var fresh = new PlsqlEditorViewModel();
        fresh.StartNew("create or replace procedure q is begin null; end;", null);
        try
        {
            PlsqlEditorViewModel.Active = await procedure;
            Assert.True(keys.Evaluate("plsqlRunnable"));
            var source = Assert.IsAssignableFrom<IPlsqlRunSource>(PlsqlEditorViewModel.Active);
            Assert.Equal("P", source.RunTarget!.Name);

            PlsqlEditorViewModel.Active = fresh; // 아직 서버 객체가 아닌 새 문서는 실행 대상이 없다
            Assert.False(keys.Evaluate("plsqlRunnable"));
            Assert.Null(((IPlsqlRunSource)(object)fresh).RunTarget);
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });
}
