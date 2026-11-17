using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>「✎ 새 테이블」 탭 화면(Headless, 시안 화면 ①): 위 소유자·이름·주석·종류 칸, 흐린 페이지·DDL 탭, 끌 수 없는 ✎ 토글, 이름을 쓰면 아래 DDL.</summary>
public class NewTableViewTests
{
    [Fact]
    public Task New_table_tab_shows_the_header_fields_and_a_fixed_edit_toggle() => HeadlessHost.Run(async () =>
    {
        var (vm, _, _) = CreateDetail();
        vm.TableEditEngine = CoreTableEditEngine.Instance;
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE", ["HR", "SAMPLE"]));
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1200, Height = 760 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            var header = view.FindControl<NewTableHeaderView>("NewTableHeader")!;
            Assert.True(header.IsEffectivelyVisible);
            var owner = header.FindControl<ComboBox>("NewTableOwnerBox")!;
            Assert.Equal("SAMPLE", owner.SelectedItem);
            var kind = header.FindControl<ComboBox>("NewTableKindBox")!;
            Assert.Equal(4, kind.ItemCount);

            var toggle = view.FindControl<ToggleButton>("TableEditToggle")!;
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.True(toggle.IsChecked);
            Assert.False(toggle.IsEnabled); // 끌 수 없다
            Assert.False(view.FindControl<Control>("RefreshDetailButton")!.IsVisible);
            Assert.False(view.FindControl<TabItem>("DdlTab")!.IsEnabled);
            Assert.True(view.FindControl<TableEditView>("TableEditView")!.IsEffectivelyVisible);

            // 흐린 페이지(데이터·통계·크기)는 고를 수 없다.
            var sections = view.FindControl<ListBox>("SectionList")!;
            var stats = (ListBoxItem)sections.ContainerFromItem(vm.Sections.Single(s => s.Title == "통계"))!;
            Assert.False(stats.IsEnabled);
            Assert.True(((ListBoxItem)sections.ContainerFromItem(vm.Sections.Single(s => s.Title == "제약"))!).IsEnabled);

            // 이름·열을 쓰면 아래 DDL 이 CREATE TABLE 로.
            var name = header.FindControl<TextBox>("NewTableNameBox")!;
            name.Text = "ORDER_ITEMS";
            vm.TableEdit!.AddColumn()!.Name = "ID";
            HeadlessHost.Settle(w);
            Assert.StartsWith("CREATE TABLE SAMPLE.ORDER_ITEMS", vm.TableEdit.PreviewText, StringComparison.Ordinal);
            Assert.Equal("변경 1건", view.FindControl<TextBlock>("TableEditSummaryText")!.Text);

            var comment = header.FindControl<TextBox>("NewTableCommentBox")!;
            comment.Text = "주문 상품";
            HeadlessHost.Settle(w);
            Assert.Equal("주문 상품", vm.TableEdit.Draft.Comment);

            kind.SelectedItem = vm.NewTableKinds.Single(k => k.Kind == TableKind.Iot);
            HeadlessHost.Settle(w);
            Assert.Equal(TableKind.Iot, vm.TableEdit.Draft.Kind);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Existing_table_detail_hides_the_new_table_header() => HeadlessHost.Run(async () =>
    {
        var (vm, _, _) = CreateDetail();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1200, Height = 760 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.False(view.FindControl<NewTableHeaderView>("NewTableHeader")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<ToggleButton>("TableEditToggle")!.IsEnabled);
            Assert.True(view.FindControl<Control>("RefreshDetailButton")!.IsVisible);
            Assert.True(view.FindControl<TabItem>("DdlTab")!.IsEnabled);
        }
        finally
        {
            w.Close();
        }
    });
}
