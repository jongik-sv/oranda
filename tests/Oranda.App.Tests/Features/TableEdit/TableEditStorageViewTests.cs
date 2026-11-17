using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using static Oranda.App.Tests.Features.TableEdit.TableEditStorageSessionTests;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>「저장」 페이지 화면(Headless, 시안 ⑦): 저장 속성 표와 LOB 표가 보이고, 바꾼 칸이 표시되고, MOVE 면 아래 ⚠ 줄이 보인다.</summary>
public class TableEditStorageViewTests
{
    [Fact]
    public Task Storage_page_renders_the_attribute_and_lob_tables_and_the_move_hint() => HeadlessHost.Run(() =>
    {
        var s = CreateStorage();
        var view = new TableEditStorageView { DataContext = s };
        var w = new Window { Content = view, Width = 1100, Height = 700 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            var attributes = view.FindControl<ItemsControl>("StorageRowList")!;
            Assert.Equal(6, attributes.ItemCount);
            var lobs = view.FindControl<ItemsControl>("LobRowList")!;
            Assert.Equal(1, lobs.ItemCount);
            Assert.True(view.FindControl<Control>("LobPanel")!.IsEffectivelyVisible);

            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("속성", texts);
            Assert.Contains("지금 값", texts);
            Assert.Contains("바꿀 값", texts);
            Assert.Contains("효과", texts);
            Assert.Contains("USERS", texts);
            Assert.Contains("PCTFREE", texts);
            Assert.Contains("LOB 열", texts);
            Assert.Contains("MEMO", texts);
            Assert.False(view.FindControl<Control>("StorageLockedBar")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<TextBlock>("StorageMoveText")!.IsEffectivelyVisible);
            Assert.Contains(attributes.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);

            Row(s, StorageProperty.Tablespace).Text = "DATA01";
            HeadlessHost.Settle(w);
            var hint = view.FindControl<TextBlock>("StorageMoveText")!;
            Assert.True(hint.IsEffectivelyVisible);
            Assert.Contains("테이블 전체를 다시 씁니다", hint.Text, StringComparison.Ordinal);
            Assert.Contains(attributes.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "DATA01" && t.Classes.Contains("changed"));
            Assert.Contains(attributes.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "MOVE 필요");
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Special_table_shows_the_lock_bar_and_disables_the_cells() => HeadlessHost.Run(() =>
    {
        var s = CreateStorage(StoredOrders(TableKind.Temporary));
        var view = new TableEditStorageView { DataContext = s };
        var w = new Window { Content = view, Width = 1100, Height = 700 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.True(view.FindControl<Control>("StorageLockedBar")!.IsEffectivelyVisible);
            Assert.All(view.FindControl<ItemsControl>("StorageRowList")!.GetVisualDescendants().OfType<TextBox>(), t => Assert.False(t.IsEffectivelyEnabled));
        }
        finally
        {
            w.Close();
        }
    });
}
