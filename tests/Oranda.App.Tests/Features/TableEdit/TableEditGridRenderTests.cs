using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 편집 표 모양(Headless): 열·제약·인덱스·파티션·저장 표가 앱 격자 표 양식(Styles/GridC, grid-c)을 따른다.
/// ORANDA_RENDER_DIR 가 있으면 밝은·어두운 테마 PNG 를 뽑는다(사람 눈 확인용).
/// </summary>
public class TableEditGridRenderTests
{
    /// <summary>바뀐 칸·새 줄·지울 줄·흐린 칸이 고루 든 열 편집 세션.</summary>
    private static TableEditSession SampleSession()
    {
        var s = CreateSession();
        s.Columns[2].LengthText = "100";
        s.Columns[1].Name = "CUST_ID";
        s.ToggleDrop(s.Columns[3].Key);
        var added = s.AddColumn()!;
        added.Name = "TOTAL_VAT";
        added.DataType = "NUMBER";
        added.LengthText = "12";
        added.ScaleText = "2";
        s.AddConstraint(ConstraintKind.ForeignKey);
        s.AddIndex();
        return s;
    }

    [Fact]
    public Task Edit_grids_render_in_light_and_dark() => HeadlessHost.Run(() =>
    {
        var s = SampleSession();
        var view = new TableEditView { DataContext = s };
        var w = new Window { Content = view, Width = 1400, Height = 300 };
        w.Show();
        try
        {
            foreach (var (page, name, list) in new[]
                     {
                         (TableEditPage.Columns, "columns", "ColumnRows"), (TableEditPage.Constraints, "constraints", "ConstraintRows"),
                         (TableEditPage.Indexes, "indexes", "IndexRows"),
                     })
            {
                s.CurrentPage = page;
                HeadlessHost.Settle(w);
                AssertGridC(view.FindControl<ListBox>(list)!);
                Render(w, name);
            }

            // 열 표: 숫자 칸(길이·소수·시작·증가)은 오른쪽 정렬, 못 고치는 칸은 바탕 없이 글만, 바뀐 체크 칸은 칸째 노랑.
            s.CurrentPage = TableEditPage.Columns;
            s.Columns[0].IsNullable = true;
            HeadlessHost.Settle(w);
            var rows = view.FindControl<ListBox>("ColumnRows")!;
            var boxes = rows.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("cell")).ToList();
            Assert.All(boxes.Where(t => t.Classes.Contains("num")), t => Assert.Equal(TextAlignment.Right, t.TextAlignment));
            Assert.Equal(4 * s.Columns.Count, boxes.Count(t => t.Classes.Contains("num")));
            var disabled = boxes.First(t => !t.IsEnabled && !t.Classes.Contains("changed"));
            var frame = disabled.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background).Color);
            Assert.Contains(rows.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("grid-c-cell") && b.Classes.Contains("changed") && b.Child is CheckBox { IsChecked: true });
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            w.Close();
        }
    });

    /// <summary>격자 표 양식: 목록 grid-c, 머리글 grid-c-header, 줄 높이 24(체크·고르기 칸이 있어도), 머리글 칸 폭 = 줄 칸 폭.</summary>
    private static void AssertGridC(ListBox list)
    {
        Assert.Contains("grid-c", list.Classes);
        var header = Assert.IsType<Border>(Assert.IsType<DockPanel>(list.Parent).Children[0]);
        Assert.Contains("grid-c-header", header.Classes);
        var headGrid = Assert.IsType<Grid>(header.Child);
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            Assert.InRange(item.Bounds.Height, 24, 25);
            var rowGrid = item.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == headGrid.ColumnDefinitions.Count);
            for (var i = 0; i < headGrid.ColumnDefinitions.Count; i++)
            {
                Assert.Equal(headGrid.ColumnDefinitions[i].ActualWidth, rowGrid.ColumnDefinitions[i].ActualWidth, 1);
            }
        }
    }

    [Fact]
    public Task Partition_and_storage_grids_render_in_light_and_dark() => HeadlessHost.Run(() =>
    {
        var p = TableEditPartitionSessionTests.Create(TableEditPartitionSessionTests.PartitionedOrders());
        p.PartitionRows[1].Name = "P_RENAMED";
        var pw = new Window { Content = new TableEditPartitionsView { DataContext = p }, Width = 1100, Height = 420 };
        var st = TableEditStorageSessionTests.CreateStorage();
        TableEditStorageSessionTests.Row(st, StorageProperty.Tablespace).Text = "DATA01";
        var sw = new Window { Content = new TableEditStorageView { DataContext = st }, Width = 1100, Height = 420 };
        pw.Show();
        sw.Show();
        try
        {
            HeadlessHost.Settle(pw);
            HeadlessHost.Settle(sw);
            AssertGridC(pw.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "PartitionRows"));
            Render(pw, "partitions");
            Render(sw, "storage");
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            pw.Close();
            sw.Close();
        }
    });

    private static void Render(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        var prefix = Environment.GetEnvironmentVariable("ORANDA_RENDER_PREFIX") ?? "te-grid";
        foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            HeadlessHost.Settle(w);
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            Directory.CreateDirectory(dir);
            using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w.Bounds.Width), (int)Math.Ceiling(w.Bounds.Height)));
            bmp.Render(w);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, $"{prefix}-{name}-{suffix}.png"));
#pragma warning restore CS0618
        }
    }
}
