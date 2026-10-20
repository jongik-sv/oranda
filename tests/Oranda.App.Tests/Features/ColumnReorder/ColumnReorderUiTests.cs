using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ColumnReorder;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ColumnReorder;

/// <summary>TSK-13-01 화면 시험(Headless): 단추로 순서 바꾸기, 끌기, 미리보기 글자, 명령·상세 단추 진입점.</summary>
public class ColumnReorderUiTests
{
    private static T Named<T>(Window w, string name) where T : Control
        => w.FindControl<T>(name) ?? throw new InvalidOperationException(name);

    private static async Task<(ColumnReorderWindow Window, ColumnReorderViewModel Vm, ReorderExec Exec)> ShowAsync(ReorderSession? session = null)
    {
        var exec = new ReorderExec();
        var vm = new ColumnReorderViewModel(new ReorderMeta(), new ReorderProvider(), exec);
        await vm.LoadAsync(session ?? new ReorderSession(), ReorderMeta.Emp);
        var window = new ColumnReorderWindow { DataContext = vm };
        window.RefreshHeader();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, exec);
    }

    [Fact]
    public Task Buttons_move_the_selected_column_and_preview_shows_the_script() => HeadlessHost.Run(async () =>
    {
        var (window, vm, _) = await ShowAsync();
        var list = Named<ListBox>(window, "ColumnList");
        Assert.Equal("SCOTT.EMP", Named<TextBlock>(window, "TableText").Text);
        Assert.Equal(4, list.ItemCount);
        Assert.False(Named<Button>(window, "PreviewButton").IsEnabled);

        list.SelectedItem = vm.Items[3];
        Named<Button>(window, "UpButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Named<Button>(window, "UpButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["ID", "SAL", "NAME", "HIRED"], vm.Items.Select(i => i.Name));
        Assert.True(Named<Button>(window, "PreviewButton").IsEnabled);
        Assert.False(Named<Button>(window, "RunButton").IsEnabled); // 미리보기 전에는 실행 불가

        Named<Button>(window, "PreviewButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => vm.PreviewText.Length > 0);
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"SAL\" INVISIBLE);", vm.PreviewText, StringComparison.Ordinal);
        Assert.True(Named<Button>(window, "RunButton").IsEnabled);
        window.Close();
    });

    [Fact]
    public Task Dragging_a_row_moves_it_to_the_drop_position() => HeadlessHost.Run(async () =>
    {
        var (window, vm, _) = await ShowAsync();
        var list = Named<ListBox>(window, "ColumnList");
        var first = list.ContainerFromIndex(0)!;
        var last = list.ContainerFromIndex(3)!;
        var from = first.TranslatePoint(new Avalonia.Point(20, first.Bounds.Height / 2), window)!.Value;
        var to = last.TranslatePoint(new Avalonia.Point(20, last.Bounds.Height / 2), window)!.Value;

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Avalonia.Point(from.X, from.Y + 12));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["NAME", "HIRED", "SAL", "ID"], vm.Items.Select(i => i.Name));
        window.Close();
    });

    [Fact]
    public Task Method_two_shows_open_script_button_instead_of_run_and_one_is_hidden_on_11g() => HeadlessHost.Run(async () =>
    {
        var (window, vm, _) = await ShowAsync(new ReorderSession("11.2.0.4.0"));

        Assert.False(Named<RadioButton>(window, "InvisibleRadio").IsVisible);
        Assert.False(Named<Button>(window, "RunButton").IsVisible);
        Assert.True(Named<Button>(window, "OpenScriptButton").IsVisible);
        vm.MoveTo(vm.Items[3], 0);
        Assert.True(Named<Button>(window, "OpenScriptButton").IsEnabled);
        window.Close();
    });

    [Fact]
    public Task Block_reasons_are_visible_in_the_window() => HeadlessHost.Run(async () =>
    {
        var vm = new ColumnReorderViewModel(new ReorderMeta(), new ReorderProvider { Traits = new ColumnReorderTraits(true, false, null) }, new ReorderExec());
        await vm.LoadAsync(new ReorderSession(), ReorderMeta.Emp);
        var window = new ColumnReorderWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasBlockReasons);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("외부 표", StringComparison.Ordinal) == true);
        window.Close();
    });

    [Fact]
    public async Task Command_is_registered_and_opens_the_dialog_only_for_tables()
    {
        var reg = ShellTestHelpers.CreateCommandRegistry();
        Assert.NotNull(reg.Find("oranda.schema.reorderColumns"));

        var opened = new List<ColumnReorderViewModel>();
        ColumnReorderLauncher.ShowOverride = vm =>
        {
            opened.Add(vm);
            return Task.CompletedTask;
        };
        try
        {
            var handler = new ColumnReorderCommandHandler(new Services(new ReorderMeta()))
            {
                SessionProvider = () => new ReorderSession(),
            };
            await handler.ExecuteAsync(new CommandContext("oranda.schema.reorderColumns", FakeContextKeys.AllTrue, ReorderMeta.Emp), default);
            await handler.ExecuteAsync(new CommandContext("oranda.schema.reorderColumns", FakeContextKeys.AllTrue, new DbObjectRef("SCOTT", "V", "VIEW")), default);

            Assert.Single(opened);
            Assert.Equal(4, opened[0].Items.Count);
        }
        finally
        {
            ColumnReorderLauncher.ShowOverride = null;
        }
    }

    [Fact]
    public async Task Object_detail_offers_the_reorder_button_on_the_columns_tab_of_a_table()
    {
        var vm = new ObjectDetailViewModel(new ReorderMeta());
        await vm.OpenAsync(new ReorderSession(), ReorderMeta.Emp, showDdl: false);
        Assert.Equal("열", vm.SelectedSection!.Title);
        Assert.True(vm.CanReorderColumns);

        await vm.OpenAsync(new ReorderSession(), new DbObjectRef("SCOTT", "V", "VIEW"), showDdl: false);
        Assert.False(vm.CanReorderColumns);
    }

    private sealed class Services(IMetadataProvider meta) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IMetadataProvider) ? meta : serviceType == typeof(IColumnReorderProvider) ? new ReorderProvider() : null;
    }
}
