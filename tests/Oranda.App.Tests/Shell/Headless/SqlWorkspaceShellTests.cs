using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// 여러 작업 공간(오렌지 SQL Tool): 문서 탭 하나가 작업 공간 하나이고, 그 안에 SQL 탭 줄이 있다.
/// 셸의 닫기(Ctrl+W)는 안쪽 SQL 탭부터, 다음 문서(Ctrl+Tab)는 안쪽 탭을 지나 다음 작업 공간으로 간다.
/// </summary>
public class SqlWorkspaceShellTests
{
    [Fact]
    public Task Workspace_is_one_document_with_its_own_sql_tab_strip() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();

        // 하네스는 시작 작업을 예약하지 않는다: 작업 공간 ↔ 문서 연결 작업만 직접 돌린다.
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var host = SqlEditorHost.Current;
        var dock = h.Get<DockWorkspace>();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();

        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        Dispatcher.UIThread.RunJobs();

        var doc = Assert.Single(dock.Documents);
        var first = Assert.IsType<SqlWorkspaceViewModel>(doc.Panel);
        Assert.Equal("기본", doc.Title);
        Assert.Equal(2, first.Tabs.Count);
        Assert.Equal("Oranda - [기본 / SQL 2]", h.Window.Title); // 오렌지처럼 창 제목에 작업 공간 / SQL 탭

        // 화면: 문서 내용은 작업 공간 View(ViewLocator 가 이름으로 찾는다). 그 안에 SQL 탭 줄(탭 2개)과 고른 탭의 편집기.
        Assert.True(new ViewLocator().Match(first));
        var view = Assert.IsType<SqlWorkspaceView>(new ViewLocator().Build(first));
        view.DataContext = first;
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var strip = view.FindControl<ListBox>("SqlTabs")!;
        Assert.Equal(2, strip.ItemCount);
        Assert.Same(host.Active, strip.SelectedItem);
        var editor = Assert.IsType<SqlEditorView>(view.FindControl<ContentControl>("EditorHost")!.Content);
        Assert.Same(host.Active, editor.DataContext);

        await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, dock.Documents.Count);
        var second = Assert.IsType<SqlWorkspaceViewModel>(dock.ActiveDocument!.Panel);
        Assert.Equal("작업 공간 2", second.Name);
        Assert.Single(second.Tabs);

        // Ctrl+Tab: 둘째 작업 공간의 마지막 탭에서 다음 작업 공간(돌아서 첫째)의 첫 탭으로.
        await registry.ExecuteAsync("oranda.window.nextDocument", keys);
        Assert.Same(first, host.ActiveWorkspace);
        Assert.Same(first.Tabs[0], host.Active);
        await registry.ExecuteAsync("oranda.window.nextDocument", keys);
        Assert.Same(first.Tabs[1], host.Active); // 같은 작업 공간 안의 다음 탭

        // Ctrl+W: 안쪽 SQL 탭을 닫는다. 작업 공간 문서는 남는다.
        await registry.ExecuteAsync("oranda.file.close", keys);
        Assert.Single(first.Tabs);
        Assert.Equal(2, dock.Documents.Count);

        // 문서 탭의 × 로 작업 공간을 닫으면 호스트에서도 빠진다.
        dock.CloseDocument(dock.Documents.Single(d => ReferenceEquals(d.Panel, second)));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(second, host.Workspaces);
        Assert.Same(first, host.ActiveWorkspace);
        window.Close();
    });

    [Fact]
    public Task Many_sql_tabs_stay_on_one_row_and_the_chosen_tab_scrolls_into_view() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        Dispatcher.UIThread.RunJobs();
        var ws = Assert.IsType<SqlWorkspaceViewModel>(h.Get<DockWorkspace>().ActiveDocument!.Panel);

        var view = new SqlWorkspaceView { DataContext = ws };
        var window = new Window { Content = view, Width = 600, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var strip = view.FindControl<ListBox>("SqlTabs")!;
        var list = view.FindControl<Button>("TabListButton")!;
        var oneRow = strip.Bounds.Height;
        Assert.False(list.IsVisible); // 다 들어가면 탭 목록 단추는 숨는다

        for (var i = 0; i < 24; i++)
        {
            await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(25, ws.Tabs.Count);
        Assert.Equal(oneRow, strip.Bounds.Height); // 줄을 바꾸지 않는다
        Assert.True(list.IsVisible);

        bool Visible(SqlEditorViewModel tab)
        {
            var item = (Control)strip.ContainerFromItem(tab)!;
            var x = item.TranslatePoint(default, strip)!.Value.X;
            return x >= -0.5 && x + item.Bounds.Width <= strip.Bounds.Width + 0.5;
        }

        ws.ActiveTab = ws.Tabs[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(Visible(ws.Tabs[0]));
        Assert.False(Visible(ws.Tabs[^1]));

        ws.ActiveTab = ws.Tabs[^1];
        Dispatcher.UIThread.RunJobs();
        Assert.True(Visible(ws.Tabs[^1]));
        window.Close();
    });
}
