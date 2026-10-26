using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.HistoryScripts;

// 소유: TSK-10-02. 새 스크립트 — 대상 폴더 판정(모델)·창 안 검사·만든 뒤 트리 선택과 열기(Headless).
// ORANDA_RENDER_DIR 가 있으면 상태별 라이트·다크 PNG(newscript-*.png)를 그 폴더에 쓴다.
public sealed class NewScriptViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-newscript-view-" + Guid.NewGuid().ToString("N"));

    public NewScriptViewTests() => Directory.CreateDirectory(_dir);

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

    private static ScriptsViewModel NewVm(params string[] roots)
    {
        var vm = new ScriptsViewModel(new ScriptFolderList()) { ActiveEditorText = () => null }; // 공용 편집기 호스트는 시험에서 건드리지 않는다
        foreach (var root in roots)
        {
            vm.AddFolder(root);
        }

        return vm;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---- 대상 폴더(모델) ----

    [Fact]
    public void One_root_and_no_selection_targets_that_root()
    {
        var root = Folder("one");
        var vm = NewVm(root);
        vm.SelectedNode = null;

        Assert.Equal(root, vm.NewScriptFolder);
        Assert.True(vm.CanNewScript);
        Assert.Contains(root, vm.NewScriptTip);
    }

    [Fact]
    public void Several_roots_and_no_selection_turn_the_button_off_with_the_pick_a_folder_tip()
    {
        var vm = NewVm(Folder("a"), Folder("b"));
        vm.SelectedNode = null;

        Assert.Null(vm.NewScriptFolder);
        Assert.False(vm.CanNewScript);
        Assert.Equal("폴더를 먼저 고르세요", vm.NewScriptTip);
    }

    [Fact]
    public void Without_any_root_the_tip_asks_to_link_a_folder()
    {
        var vm = NewVm();

        Assert.False(vm.CanNewScript);
        Assert.Equal("스크립트 폴더를 먼저 연결하세요", vm.NewScriptTip);
    }

    [Fact]
    public void Selecting_a_root_folder_or_file_chooses_the_target_and_raises_changes()
    {
        var a = Folder("a");
        var b = Folder("b");
        Directory.CreateDirectory(Path.Combine(b, "sub"));
        File.WriteAllText(Path.Combine(b, "sub", "x.sql"), "select 1");
        var vm = NewVm(a, b);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedNode = vm.Roots[1];
        Assert.Equal(b, vm.NewScriptFolder);
        Assert.Contains(nameof(ScriptsViewModel.CanNewScript), changed);
        Assert.Contains(nameof(ScriptsViewModel.NewScriptTip), changed);

        vm.Roots[1].IsExpanded = true;
        var sub = vm.Roots[1].Children.Single(c => c.IsDirectory);
        vm.SelectedNode = sub;
        Assert.Equal(sub.FullPath, vm.NewScriptFolder);

        sub.IsExpanded = true;
        vm.SelectedNode = sub.Children.Single(c => !c.IsDirectory);
        Assert.Equal(sub.FullPath, vm.NewScriptFolder); // 파일을 골랐으면 그 파일의 폴더
    }

    [Fact]
    public void Removing_the_selected_root_changes_the_state()
    {
        var vm = NewVm(Folder("a"), Folder("b"));
        vm.SelectedNode = vm.Roots[0];
        Assert.True(vm.CanNewScript);

        vm.RemoveFolder(vm.Roots[0]);
        vm.SelectedNode = null;

        Assert.True(vm.CanNewScript); // 남은 연결 폴더가 하나다
        Assert.Equal(Path.Combine(_dir, "b"), vm.NewScriptFolder);
    }

    // ---- 만든 뒤(모델, 창은 끼워 넣은 것) ----

    [Fact]
    public async Task New_script_is_created_selected_and_opened()
    {
        var root = Folder("work");
        var vm = NewVm(root);
        var opened = new List<string>();
        vm.OpenRequested += (_, path) => opened.Add(path);
        NewScriptRequest? seen = null;
        vm.NewScriptPrompt = request =>
        {
            seen = request;
            return Task.FromResult(request.Create("월마감", false) is { Ok: true } ok ? ok.Path : null);
        };

        await vm.NewScriptAsync();

        var expected = Path.Combine(root, "월마감.sql");
        Assert.Equal(root, seen!.Folder);
        Assert.Equal("새 스크립트.sql", seen.SuggestedName);
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, vm.SelectedNode!.FullPath);
        Assert.Equal([expected], opened);
        Assert.Contains("월마감.sql", vm.StatusMessage);
        Assert.Contains(vm.Roots[0].Children, c => c.FullPath == expected);
    }

    [Fact]
    public async Task Creating_into_an_unexpanded_subfolder_expands_it_and_selects_the_new_file()
    {
        var root = Folder("work");
        var deep = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "old.sql"), "select 1");
        var vm = NewVm(root);
        vm.Roots[0].IsExpanded = true;
        var a = vm.Roots[0].Children.Single();
        a.IsExpanded = true;
        var b = a.Children.Single();
        vm.SelectedNode = b; // 아직 펼치지 않은 폴더를 골랐다
        vm.NewScriptPrompt = request => Task.FromResult(request.Create("new", false).Path);

        await vm.NewScriptAsync();

        Assert.True(b.IsExpanded);
        Assert.Equal(Path.Combine(deep, "new.sql"), vm.SelectedNode!.FullPath);
        Assert.Equal(["new.sql", "old.sql"], b.Children.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Creating_from_a_selected_file_puts_the_new_file_next_to_it()
    {
        var root = Folder("work");
        File.WriteAllText(Path.Combine(root, "a.sql"), "select 1");
        var vm = NewVm(root);
        vm.Roots[0].IsExpanded = true;
        vm.SelectedNode = vm.Roots[0].Children.Single();
        vm.NewScriptPrompt = request => Task.FromResult(request.Create("b", false).Path);

        await vm.NewScriptAsync();

        Assert.Equal(Path.Combine(root, "b.sql"), vm.SelectedNode!.FullPath);
        Assert.Equal(2, vm.Roots[0].Children.Count);
    }

    [Fact]
    public async Task A_cancelled_prompt_creates_nothing_and_changes_nothing()
    {
        var root = Folder("work");
        var vm = NewVm(root);
        vm.SelectedNode = vm.Roots[0];
        var opened = 0;
        vm.OpenRequested += (_, _) => opened++;
        vm.NewScriptPrompt = _ => Task.FromResult<string?>(null);

        await vm.NewScriptAsync();

        Assert.Empty(Directory.GetFileSystemEntries(root));
        Assert.Equal(vm.Roots[0], vm.SelectedNode);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Without_a_target_folder_the_prompt_is_not_even_shown()
    {
        var vm = NewVm(Folder("a"), Folder("b"));
        vm.SelectedNode = null;
        var asked = false;
        vm.NewScriptPrompt = _ =>
        {
            asked = true;
            return Task.FromResult<string?>(null);
        };

        await vm.NewScriptAsync();

        Assert.False(asked);
    }

    [Fact]
    public async Task The_suggested_name_skips_names_that_already_exist()
    {
        var root = Folder("work");
        File.WriteAllText(Path.Combine(root, "새 스크립트.sql"), "keep");
        var vm = NewVm(root);
        string? suggested = null;
        vm.NewScriptPrompt = request =>
        {
            suggested = request.SuggestedName;
            return Task.FromResult<string?>(null);
        };

        await vm.NewScriptAsync();

        Assert.Equal("새 스크립트 2.sql", suggested);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "새 스크립트.sql")));
    }

    [Fact]
    public async Task Fill_is_offered_only_when_the_active_editor_has_text_and_writes_that_text()
    {
        var root = Folder("work");
        var vm = NewVm(root);
        vm.ActiveEditorText = () => "select * from emp;";
        NewScriptRequest? seen = null;
        vm.NewScriptPrompt = request =>
        {
            seen = request;
            return Task.FromResult<string?>(null);
        };
        await vm.NewScriptAsync();
        Assert.True(seen!.CanFill);

        Assert.True(seen.Create("filled", true).Ok);
        Assert.True(seen.Create("plain", false).Ok);
        Assert.Equal("select * from emp;", File.ReadAllText(Path.Combine(root, "filled.sql")));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(root, "plain.sql")));

        foreach (var empty in new string?[] { null, "", "  \r\n " })
        {
            vm.ActiveEditorText = () => empty;
            await vm.NewScriptAsync();
            Assert.False(seen.CanFill, $"[{empty}]");
        }
    }

    // ---- 검색 결과 목록이 보이는 중(TRD 12.3) ----

    [Fact]
    public async Task New_script_during_a_search_uses_the_folder_of_the_chosen_result_then_clears_the_search_and_opens_the_file()
    {
        var root = Folder("work");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "found.sql"), "select 'needle' from dual;");
        File.WriteAllText(Path.Combine(root, "top.sql"), "select 1 from dual;");
        var other = Folder("other");
        var vm = NewVm(root, other);
        vm.SelectedNode = null; // 폴더가 둘이고 트리 선택이 없으면 보통은 꺼진다
        vm.SearchDebounceMilliseconds = 0;
        var opened = new List<string>();
        vm.OpenRequested += (_, path) => opened.Add(path);
        string? seenFolder = null;
        vm.NewScriptPrompt = request =>
        {
            seenFolder = request.Folder;
            return Task.FromResult(request.Create("from search", false).Path);
        };

        await HeadlessHost.Run(async () =>
        {
            var view = new ScriptsView { DataContext = vm };
            var window = new Window { Content = view, Width = 420, Height = 360 };
            window.Show();
            Pump();
            var tree = view.FindControl<TreeView>("Tree")!;
            var button = view.FindControl<Button>("NewScriptButton")!;
            Assert.False(button.IsEnabled);

            vm.SearchContent = true;
            vm.SearchText = "needle";
            await vm.PendingSearch;
            Pump();
            Assert.True(vm.IsSearchActive);
            Assert.False(tree.IsVisible);
            Assert.False(button.IsEnabled); // 결과를 고르기 전에는 트리와 같은 판정

            vm.SelectedResult = vm.Results.OfType<ScriptSearchLineRow>().Single();
            Pump();
            Assert.Equal(sub, vm.NewScriptFolder);
            Assert.True(button.IsEnabled);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var expected = Path.Combine(sub, "from search.sql");
            await HeadlessHost.WaitUntil(() => opened.Count == 1);
            Pump();

            Assert.Equal(sub, seenFolder);
            Assert.False(vm.IsSearchActive);
            Assert.Equal(string.Empty, vm.SearchText);
            Assert.True(tree.IsVisible);
            Assert.Equal(expected, vm.SelectedNode!.FullPath);
            Assert.Equal([expected], opened);
            Assert.Contains("from search.sql", vm.StatusMessage);
            Assert.Equal(sub, vm.NewScriptFolder); // 이제 트리에서 고른 새 파일의 폴더
            Assert.True(button.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public async Task During_a_search_without_a_chosen_result_the_tree_selection_still_decides()
    {
        var root = Folder("work");
        File.WriteAllText(Path.Combine(root, "a.sql"), "select 'needle' from dual;");
        var vm = NewVm(root);
        vm.SelectedNode = null;
        vm.SearchDebounceMilliseconds = 0;

        await HeadlessHost.Run(async () =>
        {
            vm.SearchContent = true;
            vm.SearchText = "needle";
            await vm.PendingSearch;
            Pump();

            Assert.Null(vm.SelectedResult);
            Assert.Equal(root, vm.NewScriptFolder); // 연결 폴더가 하나뿐이다
            vm.ClearSearch();
        });
    }

    // ---- 이름 입력 창 ----

    private static NewScriptDialog OpenDialog(string folder, bool canFill = true, Func<string, bool, NewScriptResult>? create = null, string suggested = "새 스크립트.sql")
    {
        var dialog = new NewScriptDialog(new NewScriptRequest(folder, suggested, canFill, create ?? ((name, fill) => NewScriptFile.Create(folder, name, fill ? "x" : null))));
        dialog.Show();
        Pump();
        dialog.UpdateLayout();
        return dialog;
    }

    [Fact]
    public async Task Dialog_starts_with_the_default_name_selected_up_to_the_extension_and_no_error()
    {
        var folder = Folder("work");
        await HeadlessHost.Run(() =>
        {
            var d = OpenDialog(folder);

            Assert.Equal("새 스크립트.sql", d.NameBox.Text);
            Assert.Equal(0, d.NameBox.SelectionStart);
            Assert.Equal("새 스크립트".Length, d.NameBox.SelectionEnd);
            Assert.True(d.CreateButton.IsEnabled);
            Assert.False(d.ShowsError);
            Assert.Equal(string.Empty, d.MessageText);
            Assert.Equal(folder, d.FolderText.Text);
            Assert.False(d.FillBox.IsChecked);
            Assert.True(d.FillBox.IsEnabled);
            Assert.True(d.CreateButton.IsDefault);
            Assert.True(d.CancelButton.IsCancel);
            d.Close();
        });
    }

    [Fact]
    public async Task Dialog_shows_errors_inside_and_turns_the_create_button_off()
    {
        var folder = Folder("work");
        File.WriteAllText(Path.Combine(folder, "taken.sql"), "keep");
        await HeadlessHost.Run(() =>
        {
            var d = OpenDialog(folder);

            d.NameBox.Text = "taken";
            Assert.True(d.ShowsError);
            Assert.Contains("같은 이름의 파일이 이미 있습니다", d.MessageText);
            Assert.False(d.CreateButton.IsEnabled);

            d.NameBox.Text = "a?b";
            Assert.Contains("?", d.MessageText);
            Assert.False(d.CreateButton.IsEnabled);

            d.NameBox.Text = "   ";
            Assert.False(d.ShowsError); // 아직 안 쳤을 뿐이라 나무라지 않고 안내만
            Assert.Contains("이름을 입력하세요", d.MessageText);
            Assert.False(d.CreateButton.IsEnabled);

            d.NameBox.Text = "fresh";
            Assert.False(d.ShowsError);
            Assert.True(d.CreateButton.IsEnabled);
            Assert.Contains("fresh.sql", d.MessageText); // 붙을 확장자를 알려 준다

            d.NameBox.Text = "fresh.sql";
            Assert.Equal(string.Empty, d.MessageText);
            d.Close();
        });
    }

    [Fact]
    public async Task Create_button_creates_the_file_and_closes_with_its_path()
    {
        var folder = Folder("work");
        await HeadlessHost.Run(() =>
        {
            var d = OpenDialog(folder);
            d.NameBox.Text = "월마감";

            d.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(Path.Combine(folder, "월마감.sql"), d.CreatedPath);
            Assert.True(File.Exists(d.CreatedPath));
            Assert.False(d.IsVisible);
        });
    }

    [Fact]
    public async Task Cancel_closes_without_creating()
    {
        var folder = Folder("work");
        await HeadlessHost.Run(() =>
        {
            var d = OpenDialog(folder);

            d.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Null(d.CreatedPath);
            Assert.False(d.IsVisible);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        });
    }

    [Fact]
    public async Task A_name_taken_after_the_check_keeps_the_dialog_open_with_an_error()
    {
        var folder = Folder("work");
        await HeadlessHost.Run(() =>
        {
            var d = OpenDialog(folder, create: (name, _) =>
            {
                // 검사 뒤 만들기 사이에 다른 프로그램이 같은 이름을 만든 경우를 흉내 낸다.
                File.WriteAllText(Path.Combine(folder, "late.sql"), "theirs");
                return NewScriptFile.Create(folder, name);
            });
            d.NameBox.Text = "late";

            d.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(d.IsVisible);
            Assert.Null(d.CreatedPath);
            Assert.True(d.ShowsError);
            Assert.Contains("이미 있습니다", d.MessageText);
            Assert.Equal("theirs", File.ReadAllText(Path.Combine(folder, "late.sql")));
            d.Close();
        });
    }

    [Fact]
    public async Task A_permission_error_stays_in_the_dialog()
    {
        var folder = Folder("work");
        await HeadlessHost.Run(() =>
        {
            var d = OpenDialog(folder, create: (_, _) => new NewScriptResult(null, NewScriptError.NoPermission));

            d.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(d.IsVisible);
            Assert.Contains("권한", d.MessageText);
            d.Close();
        });
    }

    [Fact]
    public async Task Fill_checkbox_is_off_by_default_disabled_without_editor_text_and_passed_on_when_checked()
    {
        var folder = Folder("work");
        await HeadlessHost.Run(() =>
        {
            var none = OpenDialog(folder, canFill: false);
            Assert.False(none.FillBox.IsEnabled);
            Assert.False(none.FillBox.IsChecked);
            none.Close();

            bool? passed = null;
            var d = OpenDialog(folder, create: (name, fill) =>
            {
                passed = fill;
                return NewScriptFile.Create(folder, name, fill ? "select 1" : null);
            });
            d.FillBox.IsChecked = true;
            d.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(passed);
            Assert.Equal("select 1", File.ReadAllText(d.CreatedPath!));
        });
    }

    // ---- 스크립트 창 안의 흐름 ----

    [Fact]
    public async Task Toolbar_button_opens_the_dialog_and_the_new_file_is_selected_and_opened()
    {
        var root = Folder("work");
        File.WriteAllText(Path.Combine(root, "old.sql"), "select 1");
        var vm = NewVm(root);
        vm.ActiveEditorText = () => "select 'from editor' from dual;";
        var opened = new List<string>();
        vm.OpenRequested += (_, path) => opened.Add(path);

        await HeadlessHost.Run(async () =>
        {
            var view = new ScriptsView { DataContext = vm };
            var window = new Window { Content = view, Width = 420, Height = 360 };
            window.Show();
            Pump();
            var button = view.FindControl<Button>("NewScriptButton")!;
            Assert.True(button.IsEnabled);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => window.OwnedWindows.OfType<NewScriptDialog>().Any());
            var dialog = window.OwnedWindows.OfType<NewScriptDialog>().Single();
            Assert.Equal("새 스크립트.sql", dialog.NameBox.Text);
            dialog.NameBox.Text = "새 쿼리";
            dialog.FillBox.IsChecked = true;
            dialog.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => opened.Count == 1);
            Pump();

            var expected = Path.Combine(root, "새 쿼리.sql");
            Assert.Equal(expected, vm.SelectedNode!.FullPath);
            Assert.Equal("select 'from editor' from dual;", File.ReadAllText(expected));
            Assert.Equal(expected, view.FindControl<TreeView>("Tree")!.SelectedItem is ScriptNode n ? n.FullPath : null);
            window.Close();
        });
    }

    [Fact]
    public async Task Toolbar_button_is_off_with_several_roots_until_a_folder_is_picked_and_the_menu_follows()
    {
        var vm = NewVm(Folder("a"), Folder("b"));
        vm.SelectedNode = null;

        await HeadlessHost.Run(() =>
        {
            var view = new ScriptsView { DataContext = vm };
            var window = new Window { Content = view, Width = 420, Height = 360 };
            window.Show();
            Pump();
            var button = view.FindControl<Button>("NewScriptButton")!;
            var menu = view.FindControl<MenuItem>("NewScriptMenu")!;

            Assert.False(button.IsEnabled);
            Assert.Equal("폴더를 먼저 고르세요", ToolTip.GetTip(button));
            Assert.False(menu.IsEnabled);

            vm.SelectedNode = vm.Roots[1];
            Pump();

            Assert.True(button.IsEnabled);
            Assert.True(menu.IsEnabled);
            Assert.Contains(vm.Roots[1].FullPath, ToolTip.GetTip(button)!.ToString());
            window.Close();
        });
    }

    [Fact]
    public async Task Context_menu_has_the_one_new_script_line_and_runs_the_same_flow()
    {
        var root = Folder("work");
        var vm = NewVm(root);
        string? seenFolder = null;
        vm.NewScriptPrompt = request =>
        {
            seenFolder = request.Folder;
            return Task.FromResult(request.Create("fromMenu", false).Path);
        };

        await HeadlessHost.Run(async () =>
        {
            var view = new ScriptsView { DataContext = vm };
            var window = new Window { Content = view, Width = 420, Height = 360 };
            window.Show();
            Pump();
            var tree = view.FindControl<TreeView>("Tree")!;
            var items = tree.ContextMenu!.Items.OfType<MenuItem>().ToList();
            Assert.Single(items);
            Assert.Equal("새 스크립트…", items[0].Header);

            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessHost.WaitUntil(() => File.Exists(Path.Combine(root, "fromMenu.sql")));

            Assert.Equal(root, seenFolder);
            window.Close();
        });
    }

    // ---- 화면 점검(PNG) ----

    [Fact]
    public async Task Render_states_to_png_when_a_render_dir_is_set()
    {
        var root = Folder("sql-dev");
        Directory.CreateDirectory(Path.Combine(root, "월마감"));
        File.WriteAllText(Path.Combine(root, "조회.sql"), "select 1 from dual;");
        File.WriteAllText(Path.Combine(root, "새 스크립트.sql"), "");
        var other = Folder("sql-ops");
        var one = NewVm(root);
        one.Roots[0].IsExpanded = true;
        one.SelectedNode = one.Roots[0].Children.First(c => c.IsDirectory);
        var two = NewVm(root, other);
        two.SelectedNode = null;

        await HeadlessHost.Run(() =>
        {
            Shot("scripts-enabled", Host(one));
            Shot("scripts-disabled", Host(two));

            var folder = Path.Combine(root, "월마감");
            Shot("dialog-default", Dialog(folder, true, "새 스크립트.sql"));
            Shot("dialog-no-fill", Dialog(folder, false, "새 스크립트.sql"));
            Shot("dialog-exists", Dialog(root, true, "새 스크립트.sql"));
            Shot("dialog-badchar", Dialog(folder, true, "a?b"));
            Shot("dialog-hint", Dialog(folder, true, "월마감 보고"));
            Shot("dialog-empty", Dialog(folder, true, ""));
        });
    }

    private static Window Host(ScriptsViewModel vm)
    {
        var window = new Window { Content = new ScriptsView { DataContext = vm }, Width = 320, Height = 300 };
        window.Show();
        Pump();
        return window;
    }

    private static NewScriptDialog Dialog(string folder, bool canFill, string name)
    {
        var d = OpenDialog(folder, canFill, suggested: name);
        d.NameBox.Text = name;
        Pump();
        return d;
    }

    private static void Shot(string name, Window window)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        try
        {
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    Pump();
                    window.UpdateLayout();
                    var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                    using var bmp = new RenderTargetBitmap(size);
                    bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                    bmp.Save(Path.Combine(dir, $"newscript-{name}-{tone}.png"));
#pragma warning restore CS0618
                }
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
            window.Close();
        }
    }
}
