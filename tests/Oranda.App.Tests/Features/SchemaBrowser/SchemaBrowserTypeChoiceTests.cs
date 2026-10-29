using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.DbTools;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>TSK-14-25 객체 유형 체크 드롭다운: 전체/일부/없음 · 닫힌 글 · 트리 폴더 · 접속별 기억 · 화면 조작(D-056).</summary>
public sealed class SchemaBrowserTypeChoiceTests
{
    private static readonly string[] Five = ["TABLE", "VIEW", "INDEX", "SEQUENCE", "SYNONYM"];

    // ---------- 모델 ----------

    [Fact]
    public void Starts_with_every_type_checked_and_says_all_types()
    {
        var set = new TypeChoiceSet(Five);

        Assert.True(set.AllChecked);
        Assert.All(set.Items, i => Assert.True(i.IsChecked));
        Assert.Empty(set.SelectedTypes);
        Assert.True(set.IsAll);
        Assert.Equal("모든 유형", set.Summary);
    }

    [Fact]
    public void Some_checked_is_partial_and_the_summary_names_the_first_two()
    {
        var set = new TypeChoiceSet(Five);
        set.SetAll(false);
        set.Items[0].IsChecked = true;
        Assert.Null(set.AllChecked); // 하나 고르면 「일부」
        Assert.Equal("TABLE", set.Summary);

        set.Items[1].IsChecked = true;
        Assert.Equal("TABLE, VIEW", set.Summary);

        set.Items[2].IsChecked = true;
        set.Items[3].IsChecked = true;
        Assert.Equal(["TABLE", "VIEW", "INDEX", "SEQUENCE"], set.SelectedTypes);
        Assert.Equal("TABLE, VIEW 외 2", set.Summary);
        Assert.False(set.IsAll);
    }

    [Fact]
    public void Nothing_checked_shows_every_type_instead_of_an_empty_tree()
    {
        var set = new TypeChoiceSet(Five);
        set.AllChecked = false;

        Assert.False(set.AllChecked);
        Assert.All(set.Items, i => Assert.False(i.IsChecked));
        Assert.Empty(set.SelectedTypes); // 거르지 않는다
        Assert.True(set.IsAll);
        Assert.Equal("모든 유형", set.Summary);
    }

    [Fact]
    public void Select_all_from_partial_checks_everything_and_raises_one_change()
    {
        var set = new TypeChoiceSet(Five);
        set.SelectOnly(["VIEW"]);
        var changes = 0;
        set.Changed += (_, _) => changes++;

        set.AllChecked = false; // 체크 상자는 「일부」 에서 누르면 false 를 준다 → 전부 고른다
        Assert.True(set.AllChecked);
        Assert.Equal(1, changes);

        set.AllChecked = false;
        Assert.False(set.AllChecked);
        set.AllChecked = true;
        Assert.True(set.AllChecked);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Select_only_drops_unknown_names_and_restore_is_silent()
    {
        var set = new TypeChoiceSet(Five);
        var changes = 0;
        set.Changed += (_, _) => changes++;

        set.Restore(["VIEW", "NOPE"]);
        Assert.Equal(["VIEW"], set.SelectedTypes);
        Assert.Equal(0, changes);

        set.SelectOnly(["NOPE"]); // 아는 것이 없으면 모든 유형
        Assert.True(set.IsAll);
        Assert.Equal(1, changes);
    }

    // ---------- 스키마 브라우저 ----------

    [Fact]
    public async Task Several_types_show_only_their_folders_and_ask_the_server_for_them()
    {
        var h = new SchemaBrowserTests.Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
        Assert.Equal(["TABLE", "VIEW", "PROCEDURE"], scott.Children.Select(c => c.CategoryType));

        h.Vm.Types.SelectOnly(["TABLE", "VIEW"]);
        await h.Vm.ApplyFilterAsync();

        Assert.Equal(["TABLE", "VIEW"], h.Meta.LastQuery!.ObjectTypes);
        Assert.Equal(["TABLE", "VIEW"], h.Vm.Filter.Types);
        Assert.Equal(["TABLE", "VIEW"], scott.Children.Select(c => c.CategoryType));

        h.Vm.Types.AllChecked = false; // 아무것도 안 고르면 모든 유형
        await h.Vm.ApplyFilterAsync();
        Assert.Empty(h.Meta.LastQuery!.ObjectTypes);
        Assert.Equal(["TABLE", "VIEW", "PROCEDURE"], scott.Children.Select(c => c.CategoryType));
    }

    [Fact]
    public async Task Checking_a_type_filters_the_tree_right_away()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Vm.DebounceMilliseconds = 0;
        await h.Vm.SetSessionAsync(h.Session);
        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");

        h.Vm.Types.SelectOnly(["PROCEDURE"]);
        await HeadlessHost.WaitUntil(() => !h.Vm.IsLoading && scott.Children.All(c => c.CategoryType == "PROCEDURE"));

        Assert.Equal(["PROCEDURE"], scott.Children.Select(c => c.CategoryType));
    }

    [Fact]
    public async Task Each_connection_remembers_its_types_in_settings()
    {
        var h = new SchemaBrowserTests.Harness();
        await h.Vm.SetSessionAsync(h.Session);
        h.Vm.Types.SelectOnly(["TABLE", "VIEW"]);

        var other = new SchemaBrowserTests.TestSession();
        await h.Vm.SetSessionAsync(other);
        Assert.True(h.Vm.Types.IsAll); // 다른 접속은 따로

        await h.Vm.SetSessionAsync(h.Session);
        Assert.Equal(["TABLE", "VIEW"], h.Vm.Types.SelectedTypes);
        await HeadlessHost.WaitUntil(() => h.Settings.Current.SchemaBrowser.TypeChoices.ContainsKey(h.Session.Profile.Id.ToString()));
        Assert.Equal("TABLE|VIEW", h.Settings.Current.SchemaBrowser.TypeChoices[h.Session.Profile.Id.ToString()]);

        // 새 창(다시 시작)도 설정에서 되살린다.
        var again = new SchemaBrowserViewModel(new SchemaBrowserTests.Services(h)) { PostToUi = a => a(), DebounceMilliseconds = 3_600_000 };
        await again.SetSessionAsync(h.Session);
        Assert.Equal(["TABLE", "VIEW"], again.Types.SelectedTypes);
        Assert.Equal(["TABLE", "VIEW"], h.Meta.LastQuery!.ObjectTypes);

        h.Vm.Types.Reset(); // 모든 유형으로 돌리면 설정에서 지운다
        await HeadlessHost.WaitUntil(() => !h.Settings.Current.SchemaBrowser.TypeChoices.ContainsKey(h.Session.Profile.Id.ToString()));
    }

    [Fact]
    public void Object_search_sends_the_checked_types()
    {
        var vm = new ObjectSearchViewModel(new Oranda.App.Tests.Features.DbTools.DbToolsTests.FakeAdmin(), new Oranda.App.Tests.Features.DbTools.DbToolsTests.FakeExec());
        Assert.Empty(vm.BuildQuery().ObjectTypes);

        vm.Types.SelectOnly(["TABLE", "JAVA SOURCE"]);
        Assert.Equal(["TABLE", "JAVA SOURCE"], vm.BuildQuery().ObjectTypes);
    }

    // ---------- 화면(Headless) ----------

    [Fact]
    public Task Dropdown_checks_types_select_all_and_shows_the_summary() => HeadlessHost.Run(async () =>
    {
        var h = new SchemaBrowserTests.Harness();
        h.Vm.DebounceMilliseconds = 0;
        await h.Vm.SetSessionAsync(h.Session);
        var view = new SchemaBrowserView { DataContext = h.Vm };
        var window = new Window { Width = 400, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var drop = view.GetVisualDescendants().OfType<TypeChoiceDropDown>().Single();
        var summary = drop.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SummaryText");
        Assert.Equal("모든 유형", summary.Text);
        Assert.Equal("객체 유형", Avalonia.Automation.AutomationProperties.GetName(drop.DropButton));

        drop.DropButton.Focus(); // 키보드로 연다(Enter = 단추 누르기)
        drop.DropButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = drop.DropButton });
        Dispatcher.UIThread.RunJobs();
        Assert.True(drop.IsDropDownOpen);

        var boxes = drop.CheckBoxes;
        Assert.Equal(SchemaBrowserViewModel.KnownTypes.Count + 1, boxes.Count);
        Assert.Equal("전체 선택", boxes[0].Content);
        Assert.True(boxes[0].IsChecked);
        Assert.All(boxes, b => Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))));

        // 「전체 선택」 을 풀고(없음 = 모든 유형) TABLE 과 VIEW 를 Space 로 고른다. 드롭다운은 열린 채로 남는다.
        Press(boxes[0], Key.Space);
        Assert.False(h.Vm.Types.AllChecked);
        Assert.Equal("모든 유형", summary.Text);

        Press(BoxFor(drop, "TABLE"), Key.Space);
        Press(BoxFor(drop, "VIEW"), Key.Space);
        Assert.True(drop.IsDropDownOpen);
        Assert.Null(boxes[0].IsChecked); // 일부
        Assert.Equal("TABLE, VIEW", summary.Text);

        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
        await HeadlessHost.WaitUntil(() => !h.Vm.IsLoading && scott.Children.All(c => c.CategoryType is "TABLE" or "VIEW"));
        Assert.Equal(["TABLE", "VIEW"], scott.Children.Select(c => c.CategoryType));

        // 일부일 때 「전체 선택」 을 누르면 전부.
        Press(boxes[0], Key.Space);
        Assert.True(h.Vm.Types.AllChecked);
        Assert.Equal("모든 유형", summary.Text);

        // Esc 로 닫고 포커스는 단추로.
        boxes[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = boxes[0] });
        Dispatcher.UIThread.RunJobs();
        Assert.False(drop.IsDropDownOpen);
        window.Close();
    });

    private static CheckBox BoxFor(TypeChoiceDropDown drop, string type) =>
        drop.CheckBoxes.Single(b => Equals(b.Content, type));

    private static void Press(CheckBox box, Key key)
    {
        box.Focus();
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = box });
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = box });
        Dispatcher.UIThread.RunJobs();
    }
}
