using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>「파티션」 페이지 화면(Headless, 시안 ⑥): 줄이 보이고, 도구줄 단추가 고른 줄·방식·잠금에 맞게 켜지고 꺼지고, ⚠ 줄이 아래에 보인다.</summary>
public class TableEditPartitionViewTests
{
    private static readonly string[] ToolbarButtons =
        ["AddPartitionButton", "DropPartitionButton", "SplitPartitionButton", "MergePartitionButton", "TruncatePartitionButton", "RevertPartitionButton", "FillBoundsButton"];

    [Fact]
    public Task Partition_page_renders_rows_and_enables_toolbar_buttons_by_selection() => HeadlessHost.Run(() =>
    {
        var s = Create(PartitionedOrders());
        var view = new TableEditPartitionsView { DataContext = s };
        var w = new Window { Content = view, Width = 1100, Height = 600 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            var rows = view.FindControl<ListBox>("PartitionRows")!;
            Assert.Equal(3, rows.ItemCount);
            Assert.Contains(rows.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "P_2026_09");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "상한값 (LESS THAN)");
            Assert.False(view.FindControl<TextBlock>("PartitionLockedText")!.IsEffectivelyVisible);

            Button B(string name) => view.FindControl<Button>(name)!;
            Assert.True(B("AddPartitionButton").IsEnabled);
            Assert.True(B("FillBoundsButton").IsEnabled);
            Assert.False(B("DropPartitionButton").IsEnabled);
            Assert.False(B("SplitPartitionButton").IsEnabled);
            Assert.False(B("MergePartitionButton").IsEnabled);
            Assert.False(B("TruncatePartitionButton").IsEnabled);

            rows.SelectedIndex = 0;
            HeadlessHost.Settle(w);
            Assert.Same(s.PartitionRows[0], s.SelectedPartition);
            Assert.True(B("DropPartitionButton").IsEnabled);
            Assert.True(B("SplitPartitionButton").IsEnabled);
            Assert.True(B("MergePartitionButton").IsEnabled);
            Assert.True(B("TruncatePartitionButton").IsEnabled);

            rows.SelectedIndex = 2;
            HeadlessHost.Settle(w);
            Assert.False(B("MergePartitionButton").IsEnabled); // 마지막 줄

            // 지우기: 빨강 줄, 아래 ⚠ 줄.
            rows.SelectedIndex = 0;
            B("DropPartitionButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            HeadlessHost.Settle(w);
            var borders = rows.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row")).ToList();
            Assert.Contains("dropped", borders[0].Classes);
            Assert.Contains(view.FindControl<ItemsControl>("PartitionWarningList")!.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text?.Contains("95,020", StringComparison.Ordinal) == true);

            // 경계 채우기 패널.
            B("FillBoundsButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            HeadlessHost.Settle(w);
            Assert.True(view.FindControl<Control>("BoundFillPanel")!.IsEffectivelyVisible);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Locked_partition_page_shows_the_reason_and_disables_everything() => HeadlessHost.Run(() =>
    {
        var s = Create(PartitionedOrders() with { Kind = TableKind.Temporary });
        var view = new TableEditPartitionsView { DataContext = s };
        var w = new Window { Content = view, Width = 1100, Height = 600 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            s.SelectedPartition = s.PartitionRows[0];
            HeadlessHost.Settle(w);
            Assert.True(view.FindControl<TextBlock>("PartitionLockedText")!.IsEffectivelyVisible);
            foreach (var name in ToolbarButtons)
            {
                Assert.False(view.FindControl<Button>(name)!.IsEnabled, name);
            }

            Assert.All(view.FindControl<ListBox>("PartitionRows")!.GetVisualDescendants().OfType<TextBox>(), t => Assert.False(t.IsEnabled));
        }
        finally
        {
            w.Close();
        }
    });
}
