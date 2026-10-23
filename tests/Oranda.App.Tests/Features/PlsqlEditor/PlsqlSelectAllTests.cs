using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>TSK-13-22: 포커스가 PL/SQL 편집기에 있으면 모두 선택(⌘A)이 숨은 SQL 탭이 아니라 그 편집기를 선택한다.</summary>
[Collection(Oranda.App.Tests.Features.PlsqlWorkspace.PlsqlStatics.Name)]
public class PlsqlSelectAllTests
{
    [Fact]
    public Task Select_all_selects_the_focused_plsql_editor_not_the_hidden_sql_tab() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        Dispatcher.UIThread.RunJobs();
        var sql = SqlEditorHost.Current.Active!;
        sql.Document.Text = "select 1 from dual";

        var plsql = new PlsqlEditorViewModel();
        plsql.Editor.Document.Text = "create or replace procedure p is begin null; end;";
        var view = new PlsqlEditorView { DataContext = plsql };
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        PlsqlEditorViewModel.Active = plsql;
        h.Get<ContextKeyService>().SetFocusSlot("plsql");

        await registry.ExecuteAsync("oranda.edit.selectAll", keys);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(plsql.Editor.Text.Length, plsql.Editor.SelectionLength);
        Assert.Equal(0, sql.SelectionLength);
        window.Close();
        PlsqlEditorViewModel.Active = null;
    });
}
