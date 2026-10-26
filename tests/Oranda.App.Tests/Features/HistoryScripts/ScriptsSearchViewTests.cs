using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.HistoryScripts;

// 소유: TSK-10-01. 스크립트 창 검색 — 모델(입력 지연·취소·상태 줄·열기)과 Headless 화면(입력·Esc·Ctrl+F·Enter·PNG).
public sealed class ScriptsSearchViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-ssview-" + Guid.NewGuid().ToString("N"));

    public ScriptsSearchViewTests() => Directory.CreateDirectory(_dir);

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

    private string Write(string rel, string text)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private void Seed()
    {
        Write("월마감.sql", "-- 월 마감\nselect * from closing where ym = :ym;");
        Write(Path.Combine("dev", "proc_a.sql"), "create or replace procedure proc_a is\nbegin\n  select * from emp where deptno = 10;\n  select * from EMP e join dept d on e.deptno = d.deptno;\nend;");
        Write(Path.Combine("dev", "proc", "proc_b.pkb"), "-- body\nbegin\n  null;\nend;");
        Write(Path.Combine("dev", "z_notes.txt"), "emp 메모");
        Write("readme.md", "emp");
    }

    private ScriptsViewModel NewVm(bool addFolder = true)
    {
        var vm = new ScriptsViewModel(new ScriptFolderList()) { SearchDebounceMilliseconds = 0 };
        if (addFolder)
        {
            Assert.True(vm.AddFolder(_dir));
        }

        return vm;
    }

    private static async Task Search(ScriptsViewModel vm, string text, bool content = false)
    {
        vm.SearchContent = content;
        vm.SearchText = text;
        await vm.PendingSearch;
    }

    // ---- 모델 ----

    [Fact]
    public async Task Typing_replaces_the_tree_with_file_rows_and_clearing_goes_back()
    {
        Seed();
        var vm = NewVm();
        Assert.False(vm.IsSearchActive);

        await Search(vm, "proc");

        Assert.True(vm.IsSearchActive);
        Assert.Equal(["proc_a.sql", "proc_b.pkb"], vm.Results.OfType<ScriptSearchFileRow>().Select(r => r.Name));
        Assert.All(vm.Results, r => Assert.IsType<ScriptSearchFileRow>(r)); // 이름만이면 줄이 없다
        Assert.Equal("2개 파일", vm.StatusMessage);
        Assert.False(vm.IsSearchRunning);
        Assert.False(vm.HasEmptyHint);

        vm.ClearSearch();

        Assert.False(vm.IsSearchActive);
        Assert.Empty(vm.Results);
        Assert.Equal("폴더 1개 연결됨", vm.StatusMessage);
    }

    [Fact]
    public async Task Content_on_groups_line_rows_under_their_file_row()
    {
        Seed();
        var vm = NewVm();

        await Search(vm, "EMP", content: true);

        var rows = vm.Results.ToList();
        Assert.Equal("proc_a.sql", Assert.IsType<ScriptSearchFileRow>(rows[0]).Name);
        Assert.Equal(2, ((ScriptSearchFileRow)rows[0]).MatchCount);
        var lines = rows.Skip(1).Take(2).Cast<ScriptSearchLineRow>().ToList();
        Assert.Equal([3, 4], lines.Select(l => l.Line));
        Assert.All(lines, l => Assert.NotEmpty(l.Ranges));
        Assert.Equal("z_notes.txt", ((ScriptSearchFileRow)rows[3]).Name);
        Assert.StartsWith("2개 파일에서 3곳", vm.StatusMessage);
        Assert.DoesNotContain("한도", vm.StatusMessage);
        Assert.Same(lines[0], vm.FirstResult);
    }

    [Fact]
    public async Task Toggling_content_reruns_the_search()
    {
        Seed();
        var vm = NewVm();
        await Search(vm, "closing");
        Assert.Empty(vm.Results);

        vm.SearchContent = true;
        await vm.PendingSearch;

        Assert.Equal("월마감.sql", vm.Results.OfType<ScriptSearchFileRow>().Single().Name);
    }

    [Fact]
    public async Task A_newer_input_cancels_the_older_search_and_only_the_last_result_stays()
    {
        Seed();
        var vm = NewVm();
        vm.SearchDebounceMilliseconds = 120;

        vm.SearchText = "월마감";
        var first = vm.PendingSearch;
        vm.SearchText = "proc_b";
        var second = vm.PendingSearch;
        await second;
        await first; // 취소된 검색은 오류 없이 끝난다

        Assert.Equal(["proc_b.pkb"], vm.Results.OfType<ScriptSearchFileRow>().Select(r => r.Name));
        Assert.True(first.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_late_result_of_a_cancelled_search_never_reaches_the_screen()
    {
        Seed();
        var vm = NewVm();
        vm.SearchDebounceMilliseconds = 60;

        vm.SearchText = "월마감";
        var stale = vm.PendingSearch;
        vm.ClearSearch(); // 입력을 지웠다
        await stale;
        await Task.Delay(150);

        Assert.Empty(vm.Results);
        Assert.False(vm.IsSearchRunning);
        Assert.Equal("폴더 1개 연결됨", vm.StatusMessage);
    }

    [Fact]
    public void The_default_pause_after_typing_is_250_ms()
    {
        Assert.Equal(250, new ScriptsViewModel().SearchDebounceMilliseconds);
    }

    [Fact]
    public async Task The_status_line_says_limit_reached_when_the_thousandth_match_is_passed()
    {
        Write("big.sql", string.Join('\n', Enumerable.Repeat("select emp from dual;", 1500)));
        var vm = NewVm();

        await Search(vm, "emp", content: true);

        Assert.Equal("1개 파일에서 1000곳 (한도 도달)", vm.StatusMessage);
        Assert.Equal(1001, vm.Results.Count);
    }

    [Fact]
    public async Task Files_over_two_megabytes_are_counted_in_the_status_line()
    {
        File.WriteAllText(Path.Combine(_dir, "huge.sql"), "emp" + new string(' ', 2 * 1024 * 1024 + 10));
        Write("small.sql", "emp");
        var vm = NewVm();

        await Search(vm, "emp", content: true);

        Assert.Equal("1개 파일에서 1곳 · 2 MB 넘는 파일 1개 건너뜀", vm.StatusMessage);
    }

    [Fact]
    public void Unreadable_files_are_counted_in_the_status_line()
    {
        var vm = NewVm();

        vm.ShowResult(new ScriptSearchResult([], 0, false, 0, 3), "x", true, 1);

        Assert.Contains("읽지 못한 항목 3개", vm.StatusMessage);
    }

    [Fact]
    public async Task Empty_results_say_what_to_do_next()
    {
        Seed();
        var vm = NewVm();

        await Search(vm, "zzz-nothing");
        Assert.Contains("「내용」을 켜세요", vm.EmptyHint);
        Assert.True(vm.HasEmptyHint);
        Assert.Equal("0개 파일", vm.StatusMessage);

        await Search(vm, "zzz-nothing", content: true);
        Assert.DoesNotContain("「내용」", vm.EmptyHint);
        Assert.Contains("일치하는 스크립트가 없습니다", vm.EmptyHint);

        var none = NewVm(addFolder: false);
        await Search(none, "emp");
        Assert.Contains("연결한 폴더가 없습니다", none.EmptyHint);
    }

    [Fact]
    public async Task Linking_a_folder_while_a_search_is_shown_searches_it_too()
    {
        Seed();
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "emp_other.sql"), "x");
        var vm = new ScriptsViewModel(new ScriptFolderList()) { SearchDebounceMilliseconds = 0 };
        vm.AddFolder(Path.Combine(_dir, "dev"));
        await Search(vm, "emp");
        Assert.Empty(vm.Results);

        vm.AddFolder(other);
        await vm.PendingSearch;

        Assert.Equal(["emp_other.sql"], vm.Results.OfType<ScriptSearchFileRow>().Select(r => r.Name));
    }

    [Fact]
    public async Task Opening_a_line_row_asks_for_that_line_and_a_file_row_opens_the_file()
    {
        Seed();
        var vm = NewVm();
        await Search(vm, "emp", content: true);
        var at = new List<ScriptOpenAt>();
        var plain = new List<string>();
        vm.OpenAtRequested += (_, r) => at.Add(r);
        vm.OpenRequested += (_, p) => plain.Add(p);

        var line = vm.Results.OfType<ScriptSearchLineRow>().First();
        await vm.OpenResultAsync(line);
        await vm.OpenResultAsync(vm.Results.OfType<ScriptSearchFileRow>().First());

        Assert.Equal([new ScriptOpenAt(Path.Combine(_dir, "dev", "proc_a.sql"), 3)], at);
        Assert.Equal([Path.Combine(_dir, "dev", "proc_a.sql")], plain);
    }

    [Fact]
    public async Task Without_a_line_handler_a_line_row_falls_back_to_a_plain_open_and_null_does_nothing()
    {
        Seed();
        var vm = NewVm();
        await Search(vm, "emp", content: true);
        var plain = new List<string>();
        vm.OpenRequested += (_, p) => plain.Add(p);

        await vm.OpenResultAsync(vm.Results.OfType<ScriptSearchLineRow>().First());
        await vm.OpenResultAsync(null);

        Assert.Equal([Path.Combine(_dir, "dev", "proc_a.sql")], plain);
    }

    [Fact]
    public async Task A_mapper_xml_result_opens_through_the_file_open_command_without_a_line()
    {
        Write("m.xml", "<mapper>\n<select id=\"emp\"/>\n</mapper>");
        var vm = NewVm();
        await Search(vm, "emp", content: true);
        var viaCommand = new List<string>();
        var at = new List<ScriptOpenAt>();
        vm.OpenWithFileCommand = p => { viaCommand.Add(p); return Task.CompletedTask; };
        vm.OpenAtRequested += (_, r) => at.Add(r);

        await vm.OpenResultAsync(vm.Results.OfType<ScriptSearchLineRow>().Single());

        Assert.Equal([Path.Combine(_dir, "m.xml")], viaCommand);
        Assert.Empty(at);
    }

    // ---- Headless 화면 ----

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (Window Window, ScriptsView View) Show(ScriptsViewModel vm, int width = 380, int height = 520)
    {
        var view = new ScriptsView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Pump();
        window.UpdateLayout();
        return (window, view);
    }

    private static T Named<T>(ScriptsView view, string name)
        where T : Control => view.FindControl<T>(name)!;

    private static async Task Type(ScriptsView view, ScriptsViewModel vm, string text)
    {
        Named<TextBox>(view, "SearchBox").Text = text;
        Pump();
        await vm.PendingSearch;
        Pump();
        view.UpdateLayout();
    }

    private static void Render(Window window, string name, int width = 380, int height = 520)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"scripts-search-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    [Fact]
    public Task Typing_shows_the_result_list_instead_of_the_tree_and_Esc_returns_to_the_tree_with_selection_kept() => HeadlessHost.Run(async () =>
    {
        Seed();
        var vm = NewVm();
        var (window, view) = Show(vm);
        try
        {
            vm.SelectedNode = vm.Roots[0].Children.First(c => !c.IsDirectory);
            var picked = vm.SelectedNode;
            var tree = Named<TreeView>(view, "Tree");
            var list = Named<ListBox>(view, "ResultList");
            Assert.True(tree.IsEffectivelyVisible);
            Assert.False(list.IsEffectivelyVisible);

            await Type(view, vm, "proc");

            Assert.Equal("proc", vm.SearchText);
            Assert.False(tree.IsEffectivelyVisible);
            Assert.True(list.IsEffectivelyVisible);
            Assert.Equal(2, list.ItemCount);
            Render(window, "results-name");

            Named<TextBox>(view, "SearchBox").Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Pump();

            Assert.Equal(string.Empty, vm.SearchText);
            Assert.True(tree.IsEffectivelyVisible);
            Assert.False(list.IsEffectivelyVisible);
            Assert.Same(picked, vm.SelectedNode); // 트리의 선택·펼침은 그대로
            Assert.True(vm.Roots[0].IsExpanded);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Content_results_are_grouped_by_file_with_line_numbers_and_Enter_opens_the_first_line() => HeadlessHost.Run(async () =>
    {
        Seed();
        var vm = NewVm();
        var (window, view) = Show(vm);
        try
        {
            var opened = new List<ScriptOpenAt>();
            vm.OpenAtRequested += (_, r) => opened.Add(r);
            Named<ToggleButton>(view, "ContentToggle").IsChecked = true;
            Pump();
            Assert.True(vm.SearchContent);

            await Type(view, vm, "emp");

            var list = Named<ListBox>(view, "ResultList");
            Assert.Equal(5, list.ItemCount); // 파일 2 + 줄 3
            Assert.Contains(view.GetVisualDescendants().OfType<ScriptHitText>(), t => t.Inlines is { Count: > 1 }); // 일치 부분이 강조 조각으로 나뉜다
            Render(window, "results-content");

            var box = Named<TextBox>(view, "SearchBox");
            box.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            for (var i = 0; i < 50 && opened.Count == 0; i++)
            {
                await Task.Delay(20);
                Pump();
            }

            Assert.Equal([new ScriptOpenAt(Path.Combine(_dir, "dev", "proc_a.sql"), 3)], opened);

            // 결과 목록에서 Down → 첫 줄을 고르고, 목록의 Enter 로 고른 줄을 연다.
            opened.Clear();
            box.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            for (var i = 0; i < 50 && vm.SelectedResult is null; i++)
            {
                await Task.Delay(20);
                Pump();
            }

            Assert.IsType<ScriptSearchLineRow>(vm.SelectedResult);
            vm.SelectedResult = vm.Results.OfType<ScriptSearchLineRow>().ElementAt(1);
            Pump();
            list.GetVisualDescendants().OfType<ListBoxItem>().First(i => ReferenceEquals(i.DataContext, vm.SelectedResult)).Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            for (var i = 0; i < 50 && opened.Count == 0; i++)
            {
                await Task.Delay(20);
                Pump();
            }

            Assert.Equal([new ScriptOpenAt(Path.Combine(_dir, "dev", "proc_a.sql"), 4)], opened);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Ctrl_F_inside_the_window_moves_focus_to_the_search_box() => HeadlessHost.Run(() =>
    {
        Seed();
        var vm = NewVm();
        var (window, view) = Show(vm);
        try
        {
            Named<Button>(view, "RefreshButton").Focus();
            Pump();
            Assert.True(Named<Button>(view, "RefreshButton").IsFocused);
            Assert.False(Named<TextBox>(view, "SearchBox").IsFocused);

            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            Pump();

            Assert.True(Named<TextBox>(view, "SearchBox").IsFocused);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Search_row_has_accessible_names_and_the_content_toggle_explains_itself() => HeadlessHost.Run(() =>
    {
        var vm = NewVm(addFolder: false);
        var (window, view) = Show(vm);
        try
        {
            Assert.Equal("스크립트 검색", Avalonia.Automation.AutomationProperties.GetName(Named<TextBox>(view, "SearchBox")));
            Assert.Equal("내용까지 검색", Avalonia.Automation.AutomationProperties.GetName(Named<ToggleButton>(view, "ContentToggle")));
            Assert.Equal("검색 결과", Avalonia.Automation.AutomationProperties.GetName(Named<ListBox>(view, "ResultList")));
            Assert.NotNull(ToolTip.GetTip(Named<ToggleButton>(view, "ContentToggle")));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Every_state_draws_in_light_and_dark() => HeadlessHost.Run(async () =>
    {
        Seed();
        // 처음: 연결한 폴더 없음
        var empty = NewVm(addFolder: false);
        var (w0, v0) = Show(empty);
        try
        {
            Assert.True(Named<TextBlock>(v0, "EmptyHintText").IsEffectivelyVisible); // 처음: 폴더부터 연결하라는 안내
            Assert.False(Named<ListBox>(v0, "ResultList").IsEffectivelyVisible);
            Render(w0, "initial-no-folder");
            await Type(v0, empty, "emp");
            Assert.True(Named<TextBlock>(v0, "EmptyHintText").IsEffectivelyVisible);
            Render(w0, "state-no-folder");
        }
        finally
        {
            w0.Close();
        }

        var vm = NewVm();
        var (window, view) = Show(vm);
        try
        {
            Render(window, "tree");

            // 검색 중: 입력 멈춤을 기다리는 동안
            vm.SearchDebounceMilliseconds = 60_000;
            Named<TextBox>(view, "SearchBox").Text = "emp";
            Pump();
            Assert.True(vm.IsSearchRunning);
            Assert.True(Named<ProgressBar>(view, "SearchBar").IsEffectivelyVisible);
            Render(window, "running");
            vm.SearchDebounceMilliseconds = 0;

            await Type(view, vm, "zzz-nothing");
            Assert.True(Named<TextBlock>(view, "EmptyHintText").IsEffectivelyVisible);
            Render(window, "empty-name");

            Named<ToggleButton>(view, "ContentToggle").IsChecked = true;
            await vm.PendingSearch;
            Pump();
            Render(window, "empty-content");

            // 오류: 읽지 못한 항목과 큰 파일 건너뜀
            vm.ShowResult(new ScriptSearchResult([], 0, false, 2, 3), "emp", true, 1);
            Pump();
            Render(window, "skipped-unreadable");

            // 한도
            Write("big.sql", string.Join('\n', Enumerable.Range(1, 1500).Select(i => $"select emp{i} from dual;")));
            await Type(view, vm, "emp");
            Assert.Contains("(한도 도달)", vm.StatusMessage);
            Render(window, "limit");
        }
        finally
        {
            window.Close();
        }
    });
}
