using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>TSK-13-22: SQL 탭을 닫은 뒤 이웃 탭이 활성이 되고 편집기에 보인다(탭 줄이 선택을 먼저 비워도).</summary>
public class TabCloseActivationTests
{
    [Fact]
    public Task Closing_the_active_sql_tab_activates_the_right_neighbor_then_the_left() => HeadlessHost.Run(async () =>
    {
        var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var host = SqlEditorHost.Current;
        var dock = h.Get<DockWorkspace>();
        using (h)
        {
            var registry = h.Get<ICommandRegistry>();
            var keys = h.Get<IContextKeys>();
            for (var i = 0; i < 3; i++)
            {
                await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
            }

            Dispatcher.UIThread.RunJobs();
            var ws = Assert.IsType<SqlWorkspaceViewModel>(dock.ActiveDocument!.Panel);

            // 화면: 앱과 같은 작업 공간 View. 탭 줄 ListBox 가 ActiveTab 과 양방향으로 묶인다.
            var view = new SqlWorkspaceView { DataContext = ws };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var strip = view.FindControl<ListBox>("SqlTabs")!;
            var editorHost = view.FindControl<ContentControl>("EditorHost")!;

            var (a, b, c) = (ws.Tabs[0], ws.Tabs[1], ws.Tabs[2]);
            host.Active = b;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(b, strip.SelectedItem);

            host.Close(b); // 가운데 탭: 오른쪽 이웃이 활성
            Dispatcher.UIThread.RunJobs();
            Assert.Same(c, ws.ActiveTab);
            Assert.Same(c, host.Active);
            Assert.Same(c, strip.SelectedItem);
            Assert.Same(c, Assert.IsType<SqlEditorView>(editorHost.Content).DataContext);

            host.Close(c); // 맨 오른쪽 탭: 왼쪽 이웃이 활성
            Dispatcher.UIThread.RunJobs();
            Assert.Same(a, ws.ActiveTab);
            Assert.Same(a, host.Active);
            Assert.Same(a, strip.SelectedItem);
            Assert.Same(a, Assert.IsType<SqlEditorView>(editorHost.Content).DataContext);
            window.Close();
        }
    });
}
