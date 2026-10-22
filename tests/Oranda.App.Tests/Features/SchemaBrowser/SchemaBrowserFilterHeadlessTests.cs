using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>스키마 브라우저 이름 필터(Headless): 필터 칸에 친 글자가 트리에 걸린다.</summary>
public sealed class SchemaBrowserFilterHeadlessTests
{
    [Fact]
    public Task Typing_in_the_filter_box_filters_the_tree() => HeadlessHost.Run(async () =>
    {
        var h = new SchemaBrowserTests.Harness();
        h.Vm.DebounceMilliseconds = 0;
        await h.Vm.SetSessionAsync(h.Session);
        var view = new SchemaBrowserView { DataContext = h.Vm };
        var window = new Window { Width = 400, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
        Assert.Contains(scott.Children, c => c.CategoryType == "VIEW");

        var box = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FilterBox");
        box.Focus();
        window.KeyTextInput("DEPT");
        for (var i = 0; i < 20 && scott.Children.Any(c => c.CategoryType == "VIEW"); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("DEPT", h.Vm.FilterText);
        Assert.Equal(["TABLE"], scott.Children.Select(c => c.CategoryType));
        window.Close();
    });

    [Fact]
    public Task Typing_slowly_with_an_open_category_and_a_selected_object_filters_the_tree() => HeadlessHost.Run(async () =>
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Objects.AddRange(Enumerable.Range(0, 1200).Select(i => new Oranda.Core.Metadata.DbObjectRef("HR", $"TB_M30_T{i:D4}", "TABLE")));
        h.Meta.Objects.Add(new Oranda.Core.Metadata.DbObjectRef("HR", "TB_M30_SMTL_MASTER_DTL", "TABLE"));
        h.Vm.DebounceMilliseconds = 300;
        await h.Vm.SetSessionAsync(h.Session);
        var view = new SchemaBrowserView { DataContext = h.Vm };
        var window = new Window { Width = 400, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var hr = h.Vm.Roots.Single(r => r.Title == "HR");
        hr.IsExpanded = true;
        await Pump(() => hr.Loaded);
        var tables = hr.Children.Single(c => c.CategoryType == "TABLE");
        tables.IsExpanded = true;
        await Pump(() => tables.Loaded);
        var picked = tables.Children.First(c => c.Title == "TB_M30_T0003");
        picked.IsExpanded = true;
        h.Vm.SelectedNode = picked;
        await Pump(() => picked.Loaded);

        var box = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FilterBox");
        box.Focus();
        foreach (var ch in "SMTL_MAS")
        {
            window.KeyTextInput(ch.ToString());
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();
        }

        await Pump(() => hr.Children.SingleOrDefault(c => c.CategoryType == "TABLE")?.Children.Count == 1, 3000);
        Assert.Equal("SMTL_MAS", h.Vm.FilterText);
        var after = hr.Children.Single(c => c.CategoryType == "TABLE");
        Assert.Equal(["TB_M30_SMTL_MASTER_DTL"], after.Children.Select(c => c.Title));
        window.Close();
    });

    [Fact]
    public Task The_loading_bar_shows_while_a_schema_is_read_without_moving_the_tree() => HeadlessHost.Run(async () =>
    {
        var h = new SchemaBrowserTests.Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var view = new SchemaBrowserView { DataContext = h.Vm };
        var window = new Window { Width = 400, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var bar = view.GetVisualDescendants().OfType<ProgressBar>().Single(p => p.Name == "LoadingBar");
        var tree = view.GetVisualDescendants().OfType<TreeView>().Single();
        var treeTop = tree.TranslatePoint(default, view)!.Value.Y;
        Assert.False(bar.IsVisible);

        var gate = new TaskCompletionSource();
        h.Meta.ObjectsGate = gate;
        var expand = h.Vm.ExpandAsync(h.Vm.Roots.Single(r => r.Title == "HR"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsVisible);
        Assert.True(bar.IsIndeterminate);
        Assert.Equal(treeTop, tree.TranslatePoint(default, view)!.Value.Y);

        h.Meta.ObjectsGate = null;
        gate.SetResult();
        await expand;
        Dispatcher.UIThread.RunJobs();
        Assert.False(bar.IsVisible);
        window.Close();
    });

    private static async Task Pump(Func<bool> done, int ms = 1000)
    {
        for (var waited = 0; waited < ms && !done(); waited += 20)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
