using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.HistoryScripts;

// 소유: 결함 TSK-12-07(사용자 보고 「스크립트에서 sql 파일을 추가하면 3개씩 추가가 된다」).
// 왼쪽 묶음에서 스크립트 탭을 오갈 때마다 ScriptsView 가 새로 만들어지는데, 보기마다 ViewModel 의 열기 알림에 받는 곳을 더하고 떼지 않아
// 새 스크립트(또는 파일 열기) 한 번에 같은 파일 편집기 탭이 받는 곳 수만큼 생겼다. 같은 경로는 트리에도, 편집기 탭에도 한 번만 보여야 한다.
public sealed class ScriptsAddOnceTests : IDisposable
{
    private const string ScriptsId = "oranda.panel.scripts";
    private const string SchemaId = "oranda.panel.schemaBrowser";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-scripts-once-" + Guid.NewGuid().ToString("N"));

    public ScriptsAddOnceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>열기 알림이 비동기로 끝날 때까지 UI 작업을 돌린다(늦게 생기는 탭까지 본다).</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 15; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static int TabsFor(string path) => SqlEditorHost.Current.Tabs.Count(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string?> VisibleNames(TreeView tree)
        => tree.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible).Select(i => (i.DataContext as ScriptNode)?.Name);

    /// <summary>스키마 브라우저 ↔ 스크립트 탭을 <paramref name="times"/> 번 오간다(그때마다 스크립트 보기가 새로 만들어진다).</summary>
    private static ScriptsViewModel SwitchTabs(ShellHarness h, int times)
    {
        var ws = h.Get<DockWorkspace>();
        for (var i = 0; i < times; i++)
        {
            ws.ActivateTool(ScriptsId);
            Pump();
            ws.ActivateTool(SchemaId);
            Pump();
        }

        ws.ActivateTool(ScriptsId);
        Pump();
        h.Window.UpdateLayout();
        return (ScriptsViewModel)h.Window.GetVisualDescendants().OfType<ScriptsView>().Single().DataContext!;
    }

    // ---- (a) 「새 스크립트」 ----

    [Fact]
    public Task New_script_after_switching_tabs_opens_one_editor_tab_and_one_tree_row() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var vm = SwitchTabs(h, 2); // 사용자가 왼쪽 탭을 두 번 오간 뒤(받는 곳 3개가 되던 상태)
        var root = Folder("work");
        vm.AddFolder(root);
        vm.SelectedNode = vm.Roots.Single(r => r.FullPath == root);
        vm.NewScriptPrompt = request => Task.FromResult(request.Create("new", false).Path);

        await vm.NewScriptAsync();
        var path = Path.Combine(root, "new.sql");
        await HeadlessHost.WaitUntil(() => TabsFor(path) > 0, 5000);
        await Settle();

        Assert.Equal(1, TabsFor(path));
        Assert.Single(vm.Roots.Single(r => r.FullPath == root).Children, c => c.FullPath == path);
        var tree = h.Window.GetVisualDescendants().OfType<ScriptsView>().Single().FindControl<TreeView>("Tree")!;
        Assert.Single(VisibleNames(tree), n => n == "new.sql");
    });

    [Fact]
    public Task Opening_an_existing_file_after_switching_tabs_opens_one_editor_tab() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var vm = SwitchTabs(h, 3);
        var root = Folder("work");
        var path = Path.Combine(root, "old.sql");
        File.WriteAllText(path, "select 1 from dual");
        vm.AddFolder(root);
        var node = vm.Roots.Single(r => r.FullPath == root).Children.Single();

        await vm.OpenAsync(node);
        await HeadlessHost.WaitUntil(() => TabsFor(path) > 0, 5000);
        await Settle();

        Assert.Equal(1, TabsFor(path));
    });

    [Fact]
    public Task A_view_that_left_the_screen_no_longer_answers_open_requests() => HeadlessHost.Run(async () =>
    {
        var previous = SqlEditorHost.Current;
        SqlEditorHost.Current = new SqlEditorHost();
        try
        {
            var root = Folder("work");
            var path = Path.Combine(root, "a.sql");
            File.WriteAllText(path, "select 1 from dual");
            var vm = new ScriptsViewModel(new ScriptFolderList()) { ActiveEditorText = () => null };
            vm.AddFolder(root);
            var window = new Window { Width = 420, Height = 360 };
            window.Show();
            for (var i = 0; i < 3; i++)
            {
                window.Content = new ScriptsView { DataContext = vm }; // 도킹이 탭을 다시 그릴 때처럼 보기만 새로
                Pump();
            }

            await vm.OpenAsync(vm.Roots[0].Children.Single());
            await HeadlessHost.WaitUntil(() => TabsFor(path) > 0, 5000);
            await Settle();

            Assert.Equal(1, TabsFor(path));
            window.Close();
        }
        finally
        {
            SqlEditorHost.Current = previous;
        }
    });

    // ---- (b) 바깥에서 생긴 파일 ----

    [Fact]
    public Task A_file_made_outside_shows_once_after_refresh_even_when_refreshed_again() => HeadlessHost.Run(() =>
    {
        var root = Folder("work");
        var vm = new ScriptsViewModel(new ScriptFolderList()) { ActiveEditorText = () => null };
        vm.AddFolder(root);
        var view = new ScriptsView { DataContext = vm };
        var window = new Window { Content = view, Width = 420, Height = 360 };
        window.Show();
        Pump();
        var refresh = view.FindControl<Button>("RefreshButton")!;
        var tree = view.FindControl<TreeView>("Tree")!;

        File.WriteAllText(Path.Combine(root, "outside.sql"), "select 1 from dual");
        for (var i = 0; i < 3; i++)
        {
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }

        window.UpdateLayout();
        Assert.Single(vm.Roots[0].Children, c => c.Name == "outside.sql");
        Assert.Single(VisibleNames(tree), n => n == "outside.sql");
        window.Close();
    });

    [Fact]
    public Task Saving_the_same_file_many_times_quickly_keeps_one_row() => HeadlessHost.Run(() =>
    {
        var root = Folder("work");
        var path = Path.Combine(root, "busy.sql");
        var vm = new ScriptsViewModel(new ScriptFolderList()) { ActiveEditorText = () => null };
        vm.AddFolder(root);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(path, "select " + i + " from dual");
            vm.Refresh();
            vm.RevealFile(path);
        }

        Assert.Single(vm.Roots[0].Children, c => c.FullPath == path);
    });

    // ---- 겹친 연결 폴더 ----

    [Fact]
    public async Task New_script_in_overlapping_folders_is_selected_once_and_opened_once()
    {
        var parent = Folder("work");
        var child = Path.Combine(parent, "sql");
        Directory.CreateDirectory(child);
        var vm = new ScriptsViewModel(new ScriptFolderList()) { ActiveEditorText = () => null };
        vm.AddFolder(parent);
        vm.AddFolder(child);
        vm.SelectedNode = vm.Roots.Single(r => r.FullPath == child);
        var opened = new List<string>();
        vm.OpenRequested += (_, p) => opened.Add(p);
        vm.NewScriptPrompt = request => Task.FromResult(request.Create("new", false).Path);

        await vm.NewScriptAsync();

        var path = Path.Combine(child, "new.sql");
        Assert.Equal([path], opened);
        Assert.Single(vm.Roots.Single(r => r.FullPath == child).Children, c => c.FullPath == path);
        var outer = vm.Roots.Single(r => r.FullPath == parent);
        outer.IsExpanded = true;
        var sub = outer.Children.Single(c => c.IsDirectory);
        sub.IsExpanded = true;
        Assert.Single(sub.Children, c => c.FullPath == path); // 바깥 연결 폴더의 하위 폴더 안에서도 한 번
    }

    // ---- 같은 경로를 겹쳐 여는 경우(편집기) ----

    [Fact]
    public Task Opening_the_same_path_several_times_at_once_makes_one_tab() => HeadlessHost.Run(async () =>
    {
        var path = Path.Combine(Folder("work"), "Same.sql");
        File.WriteAllText(path, "select 1 from dual");
        var host = new SqlEditorHost();

        var tabs = await Task.WhenAll(host.OpenFileAsync(path, default), host.OpenFileAsync(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path, default), host.OpenFileAsync(path, default));

        Assert.Single(host.Tabs, t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        Assert.All(tabs, t => Assert.Same(tabs[0], t));
    });
}
