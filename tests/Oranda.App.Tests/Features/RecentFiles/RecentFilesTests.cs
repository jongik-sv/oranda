using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.RecentFiles;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.RecentFiles;

/// <summary>최근 파일·최근 작업 공간: 맨 위·중복·개수·저장·손상 파일·없는 파일, 편집기 호스트의 열기·저장 기록, 파일 메뉴 하위 메뉴 줄.</summary>
[Collection("GridEditStatics")] // SaveFeedback·MenuItemSources·SqlEditorHost.Current 정적 상태를 함께 쓴다
public sealed class RecentFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-recent-" + Guid.NewGuid().ToString("N"));

    public RecentFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Touch(string name, string text = "select 1")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("a.sql", RecentKind.File)]
    [InlineData("pkg.pks", RecentKind.File)]
    [InlineData("UserMapper.xml", RecentKind.File)]
    [InlineData("x.workspace.md", RecentKind.Workspace)]
    [InlineData("orange.sqd", RecentKind.Workspace)]
    [InlineData("README.md", RecentKind.File)] // 종류를 모를 때만. 작업 공간 열기로 연 .md 는 호스트가 작업 공간이라고 알린다
    public void Kind_follows_how_the_file_opens(string name, RecentKind expected) =>
        Assert.Equal(expected, RecentFilesService.KindOf(Path.Combine(_root, name)));

    [Fact]
    public void Used_file_goes_to_the_top_without_duplicates_and_the_list_keeps_ten()
    {
        var service = new RecentFilesService(dataDirectory: null);
        var changed = 0;
        service.Changed += (_, _) => changed++;
        var paths = Enumerable.Range(0, 12).Select(i => Path.Combine(_root, $"f{i}.sql")).ToList();
        paths.ForEach(p => service.Add(p));
        service.Add(paths[5]);

        Assert.Equal(RecentFilesService.MaxItems, service.Files.Count);
        Assert.Equal(paths[5], service.Files[0]);
        Assert.Single(service.Files, p => p == paths[5]);
        Assert.DoesNotContain(paths[0], service.Files);
        Assert.DoesNotContain(paths[1], service.Files);
        Assert.Empty(service.Workspaces);
        Assert.Equal(13, changed);

        service.Add(Path.Combine(_root, "w.workspace.md"));
        Assert.Equal([Path.Combine(_root, "w.workspace.md")], service.Workspaces);
        service.Clear(RecentKind.File);
        Assert.Empty(service.Files);
        Assert.Single(service.Workspaces);
    }

    [Fact]
    public void Lists_survive_a_restart_and_a_damaged_file_starts_empty()
    {
        var a = Path.Combine(_root, "a.sql");
        var w = Path.Combine(_root, "w.sqd");
        var first = new RecentFilesService(_root);
        first.Add(a);
        first.Add(w);

        var again = new RecentFilesService(_root);
        Assert.Equal([a], again.Files);
        Assert.Equal([w], again.Workspaces);

        File.WriteAllText(Path.Combine(_root, "recent-files.json"), "{ not json");
        var damaged = new RecentFilesService(_root);
        Assert.Empty(damaged.Files);
        damaged.Add(a); // 손상 파일을 새 목록으로 덮는다
        Assert.Equal([a], new RecentFilesService(_root).Files);
    }

    [Fact]
    public Task Missing_file_leaves_the_list_and_shows_one_status_line() => Ui.Run(async () =>
    {
        var gone = Path.Combine(_root, "gone.sql");
        var ran = new List<(string Command, string Path)>();
        var service = new RecentFilesService(dataDirectory: null) { Execute = (c, p) => { ran.Add((c, p)); return Task.CompletedTask; } };
        service.Add(gone);
        SaveFeedback.Clear();

        Assert.False(await service.OpenAsync(gone, RecentKind.File));
        Assert.Empty(service.Files);
        Assert.Empty(ran);
        Assert.Equal(SaveFeedbackKind.Failure, SaveFeedback.Current?.Kind);
        Assert.Contains("gone.sql", SaveFeedback.Current?.Text, StringComparison.Ordinal);
        SaveFeedback.Clear();

        var sql = Touch("a.sql");
        var ws = Touch("w.workspace.md", "# w\n");
        Assert.True(await service.OpenAsync(sql, RecentKind.File));
        Assert.True(await service.OpenAsync(ws, RecentKind.Workspace));
        Assert.Equal([("oranda.file.open", sql), ("oranda.file.openWorkspaceFile", ws)], ran);
    });

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("/Users/me/projects/oranda/mappers", 15, "/Users/…mappers")]
    [InlineData("abcdefghij", 5, "ab…ij")]
    public void Shorten_middle_keeps_head_and_tail(string text, int max, string expected)
    {
        var shortened = RecentFilesService.ShortenMiddle(text, max);
        Assert.Equal(expected, shortened);
        Assert.True(shortened.Length <= Math.Max(max, text.Length));
    }

    [Fact]
    public void Menu_lines_are_name_and_gray_folder_or_one_gray_notice_when_empty()
    {
        var service = new RecentFilesService(dataDirectory: null);
        var empty = Assert.Single(service.MenuItems(RecentKind.Workspace));
        Assert.Equal("최근 작업 공간이 없습니다.", empty.Title);
        Assert.Null(empty.Run);

        var deep = Path.Combine(_root, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("folder", 12)), "UserMapper.xml");
        service.Add(deep);
        var line = Assert.Single(service.MenuItems(RecentKind.File));
        Assert.Equal("UserMapper.xml", line.Title);
        Assert.Equal(RecentFilesService.DetailLength, line.Detail!.Length);
        Assert.Contains('…', line.Detail);
        Assert.Equal(deep, line.ToolTip);
        Assert.NotNull(line.Run);
    }

    [Fact]
    public Task Host_open_save_and_workspace_file_are_recorded_but_new_unsaved_tabs_are_not() => Ui.Run(async () =>
    {
        var dialogs = new SaveTo();
        var host = new SqlEditorHost(dialogs: dialogs);
        var service = new RecentFilesService(dataDirectory: null);
        service.Attach(host);
        try
        {
            host.NewDocument("select 1");
            Assert.Empty(service.Files);

            var a = Touch("a.sql");
            var b = Touch("b.sql");
            await host.OpenFileAsync(a, default);
            await host.OpenFileAsync(b, default);
            Assert.Equal([b, a], service.Files);
            await host.OpenFileAsync(a, default); // 이미 연 파일
            Assert.Equal([a, b], service.Files);
            await host.OpenFileAsync(b, default);

            // 이미 연 파일을 다시 열거나 저장해도 맨 위로 온다.
            var tabA = host.Tabs.Single(t => t.FilePath == a);
            Assert.True(await host.SaveAsync(tabA, default));
            Assert.Equal([a, b], service.Files);

            // 다른 이름으로 저장 = 새 경로가 맨 위.
            dialogs.Path = Path.Combine(_root, "c.sql");
            Assert.True(await host.SaveAsAsync(tabA, default));
            Assert.Equal(Path.Combine(_root, "c.sql"), service.Files[0]);

            dialogs.Path = Path.Combine(_root, "w.workspace.md");
            Assert.True(await host.SaveWorkspaceFileAsync(askPath: true, default));
            Assert.Equal([Path.Combine(_root, "w.workspace.md")], service.Workspaces);

            var other = Touch("o.workspace.md", "# o\n\n## t1\n\n```sql\nselect 2\n```\n");
            await host.OpenWorkspaceFileAsync(other, default);
            Assert.Equal(other, service.Workspaces[0]);

            // 확장자가 .md 하나뿐인 작업 공간도 작업 공간 열기로 열었으면 작업 공간 목록(누르면 작업 공간으로 연다).
            var plain = Touch("plain.md", "# plain\n\n## t1\n\n```sql\nselect 3\n```\n");
            await host.OpenWorkspaceFileAsync(plain, default);
            Assert.Equal(plain, service.Workspaces[0]);
            Assert.DoesNotContain(plain, service.Files);
        }
        finally
        {
            service.Dispose();
        }

        // 뗀 뒤에는 기록하지 않는다.
        await host.OpenFileAsync(Touch("d.sql"), default);
        Assert.DoesNotContain(Path.Combine(_root, "d.sql"), service.Files);
    });

    [Fact]
    public async Task Clear_commands_empty_only_their_own_list()
    {
        var service = new RecentFilesService(dataDirectory: null);
        service.Add(Path.Combine(_root, "a.sql"));
        service.Add(Path.Combine(_root, "w.sqd"));
        var commands = new RecentFilesCommands { Service = service };

        await commands.ExecuteAsync(new CommandContext(RecentFilesCommands.ClearWorkspaces, new ContextKeyService()), default);
        Assert.Empty(service.Workspaces);
        Assert.Single(service.Files);
        await commands.ExecuteAsync(new CommandContext(RecentFilesCommands.ClearFiles, new ContextKeyService()), default);
        Assert.Empty(service.Files);
    }

    [Fact]
    public Task Native_menu_lines_are_name_dash_folder_and_refill_only_when_the_list_changed() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness(nativeMenu: true);
        var service = h.Get<RecentFilesService>();
        service.Attach(SqlEditorHost.Current);
        var file = NativeMenu.GetMenu(h.Window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "파일").Menu!;
        var recent = file.Items.OfType<NativeMenuItem>().Single(i => i.Header == "최근 파일").Menu!;
        // 시스템이 메뉴를 열 때 부르는 NeedsUpdate 는 내부 인터페이스로만 올릴 수 있다.
        var raise = typeof(NativeMenu).GetInterfaces().Single(i => i.Name == "INativeMenuExporterEventsImplBridge").GetMethod("RaiseNeedsUpdate")!;
        void Open() => raise.Invoke(recent, null);

        var folder = Path.Combine(_root, "user_mapper");
        service.Add(Path.Combine(folder, "a_b.sql"));
        Open();
        var line = (NativeMenuItem)recent.Items[0];
        var shortFolder = RecentFilesService.ShortenMiddle(folder, RecentFilesService.DetailLength);
        Assert.Equal($"a__b.sql — {shortFolder.Replace("_", "__", StringComparison.Ordinal)}", line.Header);
        Assert.Equal(Path.Combine(folder, "a_b.sql"), line.ToolTip);
        Assert.IsType<NativeMenuItemSeparator>(recent.Items[1]);

        Open(); // 단축키 찾기처럼 목록이 그대로면 같은 줄을 둔다
        Assert.Same(line, recent.Items[0]);
        Assert.Equal(3, recent.Items.Count);
    });

    [Fact]
    public Task File_menu_submenu_is_filled_with_recent_lines_when_opened() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var service = h.Get<RecentFilesService>();
        service.Attach(SqlEditorHost.Current);
        var file = (MenuItem)h.Window.Menus.ItemsSource!.Cast<object>().ElementAt(MenuModelBuilder.TopMenus.ToList().IndexOf("file"));
        var recent = file.ItemsSource!.Cast<object>().OfType<MenuItem>().Single(i => AutomationProperties.GetName(i) == "최근 파일");

        void Open()
        {
            recent.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, recent));
            Dispatcher.UIThread.RunJobs();
        }

        Open();
        var lines = recent.ItemsSource!.Cast<object>().ToList();
        var notice = Assert.IsType<MenuItem>(lines[0]);
        Assert.Equal("최근 파일이 없습니다.", AutomationProperties.GetName(notice));
        Assert.False(notice.IsEnabled);
        Assert.IsType<Separator>(lines[1]);
        Assert.Equal(RecentFilesCommands.ClearFiles, Assert.IsType<RegistryCommand>(((MenuItem)lines[2]).Command).CommandId);

        service.Add(Path.Combine(_root, "a.sql"));
        service.Add(Path.Combine(_root, "b.sql"));
        Open();
        lines = [.. recent.ItemsSource!.Cast<object>()];
        Assert.Equal(["b.sql", "a.sql"], lines.Take(2).Cast<MenuItem>().Select(AutomationProperties.GetName));
        Assert.Equal(Path.Combine(_root, "b.sql"), ToolTip.GetTip((MenuItem)lines[0]));
        Assert.IsType<Separator>(lines[2]);
        Assert.Equal(4, lines.Count); // 이전 줄은 바뀌고 쌓이지 않는다

        file.IsSubMenuOpen = true;
        recent.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        MyBatisFlowRig.Render(h.Window, "recent-file-menu", 1100, 720);
    });

    [Fact]
    public Task Start_page_shows_both_recent_lists_and_follows_changes_while_attached() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var service = h.Get<RecentFilesService>();
        h.Window.UpdateLayout();
        var page = Assert.Single(h.Window.GetVisualDescendants().OfType<StartPage>());
        string?[] Texts() => [.. page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

        Assert.Contains("최근 파일", Texts());
        Assert.Contains("최근 작업 공간", Texts());
        Assert.Contains("최근 파일이 없습니다.", Texts());
        Assert.Contains("최근 작업 공간이 없습니다.", Texts());

        var deep = Path.Combine(_root, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("mappers", 10)));
        service.Add(Path.Combine(deep, "EmpMapper.xml"));
        service.Add(Path.Combine(_root, "findEmp.sql"));
        service.Add(Path.Combine(_root, "daily.workspace.md"));
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();

        var buttons = page.GetVisualDescendants().OfType<Button>().Where(b => ToolTip.GetTip(b) is string).ToList();
        Assert.Equal(["findEmp.sql", "EmpMapper.xml", "daily.workspace.md"], buttons.Select(AutomationProperties.GetName));
        Assert.Equal(Path.Combine(deep, "EmpMapper.xml"), ToolTip.GetTip(buttons[1]));
        Assert.DoesNotContain("최근 파일이 없습니다.", Texts());
        MyBatisFlowRig.Render(h.Window, "recent-startpage", 1100, 720);

        // 떼어 낸 시작 화면은 더 듣지 않는다(호스트마다 새로 생긴다).
        h.Window.Content = null;
        Dispatcher.UIThread.RunJobs();
        service.Clear(RecentKind.File);
        Assert.Equal(3, page.GetVisualDescendants().OfType<Button>().Count(b => ToolTip.GetTip(b) is string));
    });

    private sealed class SaveTo : IEditorDialogs
    {
        public string? Path { get; set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult(Path);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);
    }
}
