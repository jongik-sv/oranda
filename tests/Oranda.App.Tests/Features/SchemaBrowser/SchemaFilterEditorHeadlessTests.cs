using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>저장 필터 편집 대화상자와 툴바 ▾ 메뉴(Headless, TSK-13-10).</summary>
public sealed class SchemaFilterEditorHeadlessTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Control root, string name)
    {
        Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Owner, SchemaFilterEditorWindow Dialog, Task<bool> Result) Open(SchemaFilterEditorViewModel vm)
    {
        var owner = new Window { Width = 800, Height = 700 };
        owner.Show();
        var dialog = new SchemaFilterEditorWindow { DataContext = vm };
        var result = dialog.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        return (owner, dialog, result);
    }

    [Fact]
    public Task New_filter_with_chosen_schemas_closes_with_ok() => HeadlessHost.Run(async () =>
    {
        var vm = new SchemaFilterEditorViewModel([], null, ["HR", "SCOTT"]);
        var (owner, dialog, result) = Open(vm);

        Click(dialog, "NewButton");
        Assert.Equal("새 필터", vm.Selected!.Name);
        Assert.Equal("새 필터", Named<TextBox>(dialog, "NameBox").Text);

        dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "SelectedScopeRadio").IsChecked = true;
        Assert.True(vm.Selected.IsSelectedScope);
        vm.SchemaToAdd = "HR";
        Click(dialog, "AddSchemaButton");
        Assert.Equal(["HR"], Named<ListBox>(dialog, "SchemaList").Items.Cast<string>());

        Click(dialog, "OkButton");
        Assert.True(await result);
        Assert.Equal(["HR"], Assert.Single(vm.BuildFilters()).Schemas);
        owner.Close();
    });

    [Fact]
    public Task Pattern_scope_previews_schemas_and_disables_ok_for_an_invalid_pattern() => HeadlessHost.Run(async () =>
    {
        var vm = new SchemaFilterEditorViewModel([], null, ["HR", "APP_USER", "SCOTT"]);
        var (owner, dialog, result) = Open(vm);
        Click(dialog, "NewButton");
        Named<RadioButton>(dialog, "PatternScopeRadio").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<Grid>(dialog, "PatternPanel").IsVisible);

        var box = Named<TextBox>(dialog, "SchemaPatternBox");
        box.Text = "_USER$";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["APP_USER"], Named<ListBox>(dialog, "PatternMatchList").Items.Cast<string>());
        Assert.Equal("맞는 스키마 1/3개", Named<TextBlock>(dialog, "PatternCountText").Text);
        Assert.True(Named<Button>(dialog, "OkButton").IsEnabled);
        Assert.False(Named<TextBlock>(dialog, "PatternErrorText").IsVisible);

        box.Text = "(_USER";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBlock>(dialog, "PatternErrorText").IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(Named<TextBlock>(dialog, "PatternErrorText").Text));
        Assert.Contains("pattern-invalid", box.Classes);
        Assert.False(Named<Button>(dialog, "OkButton").IsEnabled);
        Assert.False(Named<Button>(dialog, "SaveButton").IsEnabled);

        box.Text = "^SCOTT$";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<Button>(dialog, "OkButton").IsEnabled);
        Click(dialog, "OkButton");
        Assert.True(await result);
        Assert.Equal("^SCOTT$", Assert.Single(vm.BuildFilters()).SchemaPattern);
        owner.Close();
    });

    [Fact]
    public Task Ok_with_a_blank_name_stays_open_and_cancel_returns_false() => HeadlessHost.Run(async () =>
    {
        var vm = new SchemaFilterEditorViewModel([], null, ["HR"]);
        var (owner, dialog, result) = Open(vm);
        Click(dialog, "NewButton");
        vm.Selected!.Name = " ";

        Click(dialog, "OkButton");
        Assert.True(dialog.IsVisible);
        Assert.True(Named<TextBlock>(dialog, "ErrorText").IsVisible);

        Click(dialog, "CancelButton");
        Assert.False(await result);
        owner.Close();
    });

    [Fact]
    public Task Object_tab_has_a_row_per_category_bound_to_the_draft() => HeadlessHost.Run(async () =>
    {
        var vm = new SchemaFilterEditorViewModel([new SchemaBrowserFilter { Id = "f1", Name = "A", HiddenTypes = ["VIEW"] }], "f1", ["HR"]);
        var (owner, dialog, result) = Open(vm);
        Named<TabControl>(dialog, "Tabs").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        var checks = Named<ItemsControl>(dialog, "TypeRows").GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(SchemaBrowserViewModel.KnownTypes.Count, checks.Count);
        var view = checks.Single(c => ((FilterTypeRow)c.DataContext!).Type == "VIEW");
        Assert.False(view.IsChecked);
        checks.Single(c => ((FilterTypeRow)c.DataContext!).Type == "TABLE").IsChecked = false;
        Assert.False(vm.Selected!.Types.Single(t => t.Type == "TABLE").IsShown);

        vm.GlobalPattern = "TB*";
        Click(dialog, "AssignButton");
        Assert.All(vm.Selected.Types, t => Assert.Equal("TB*", t.Pattern));

        Click(dialog, "CancelButton");
        Assert.False(await result);
        owner.Close();
    });

    [Fact]
    public Task Toolbar_menu_lists_saved_filters_with_a_check_and_switches_between_them() => HeadlessHost.Run(async () =>
    {
        var h = new SchemaBrowserTests.Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var a = new SchemaBrowserFilter { Name = "HR만", Scope = SchemaFilterScope.Selected, Schemas = ["HR"] };
        var b = new SchemaBrowserFilter { Name = "SCOTT만", Scope = SchemaFilterScope.Selected, Schemas = ["SCOTT"] };
        await h.Vm.SaveFiltersAsync([a, b], a.Id);
        var view = new SchemaBrowserView { DataContext = h.Vm };
        var window = new Window { Width = 400, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("on", Named<Button>(view, "SavedFilterButton").Classes);
        var items = view.BuildSavedFilterMenu();
        Assert.Equal(["필터 없음", "HR만", "SCOTT만", "필터 편집…"], items.OfType<MenuItem>().Select(i => (string)i.Header!));
        Assert.Equal([false, true, false], items.OfType<MenuItem>().Take(3).Select(i => i.IsChecked));

        items.OfType<MenuItem>().Single(i => (string)i.Header! == "SCOTT만").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 50 && h.Vm.ActiveSavedFilter?.Id != b.Id; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("SCOTT만", h.Vm.ActiveSavedFilter?.Name);
        items = view.BuildSavedFilterMenu();
        items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 50 && h.Vm.ActiveSavedFilter is not null; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Null(h.Vm.ActiveSavedFilter);
        Assert.DoesNotContain("on", Named<Button>(view, "SavedFilterButton").Classes);
        window.Close();
    });
}
