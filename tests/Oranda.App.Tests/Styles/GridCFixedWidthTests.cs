using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Styles;

/// <summary>TSK-13-40: C안 표의 칸 너비는 처음부터 전체 행 기준이라 스크롤해도 바뀌지 않는다.</summary>
public class GridCFixedWidthTests
{
    private static readonly string[] Columns = ["이름", "값", "메모"];

    // 긴 글이 맨 아래쪽 행에만 있는 표.
    internal static List<IReadOnlyList<object?>> Rows(int count, int longAt)
    {
        var rows = new List<IReadOnlyList<object?>>();
        for (var i = 0; i < count; i++)
        {
            rows.Add(i == longAt
                ? new object?[] { "VERY_LONG_PARAMETER_NAME_FOR_THE_SGA_AREA", "1234567890123456", "끝" }
                : new object?[] { "SGA" + i, i, "메모" });
        }

        return rows;
    }

    // 머리글 줄의 칸 너비(첫 칸=행 머리 포함, 마지막 칸 제외).
    internal static double[] HeaderWidths(Control root)
    {
        var header = root.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("grid-c-header"));
        var grid = header.GetVisualDescendants().OfType<Grid>().First();
        return grid.ColumnDefinitions.Take(grid.ColumnDefinitions.Count - 1).Select(c => c.ActualWidth).ToArray();
    }

    internal static void ScrollToEnd(ListBox list)
    {
        var sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        sv.Offset = new Avalonia.Vector(0, sv.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        sv.Offset = new Avalonia.Vector(0, sv.Extent.Height);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task DetailTable_column_widths_do_not_change_when_scrolling() => HeadlessHost.Run(() =>
    {
        var table = new DetailTable { Columns = Columns, Rows = Rows(300, 280) };
        var w = new Window { Content = table, Width = 700, Height = 300 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var before = HeaderWidths(table);
            ScrollToEnd(table.List);
            var after = HeaderWidths(table);
            Assert.Equal(before, after);
            Assert.True(before[1] > 150, "긴 이름이 처음부터 반영돼야 한다: " + before[1]);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task DetailTable_widths_are_recomputed_when_rows_change() => HeadlessHost.Run(() =>
    {
        var table = new DetailTable { Columns = Columns, Rows = Rows(300, 280) };
        var w = new Window { Content = table, Width = 700, Height = 300 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var wide = HeaderWidths(table)[1];
            table.Rows = Rows(300, -1);
            Dispatcher.UIThread.RunJobs();
            Assert.True(HeaderWidths(table)[1] < wide - 100);
            table.Rows = Rows(300, 299);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(wide, HeaderWidths(table)[1]);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task DetailTable_empty_table_opens_without_error() => HeadlessHost.Run(() =>
    {
        var table = new DetailTable { Columns = Columns, Rows = [] };
        var w = new Window { Content = table, Width = 400, Height = 200 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.Close();
    });

    [Fact]
    public Task DetailTable_first_draw_of_1000_rows_time() => HeadlessHost.Run(() =>
    {
        var rows = Rows(1000, 900);
        var sw = Stopwatch.StartNew();
        var table = new DetailTable { Columns = Columns, Rows = rows };
        var w = new Window { Content = table, Width = 700, Height = 700 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        sw.Stop();
        w.Close();
        Console.WriteLine("PERF 1000 rows first draw ms = " + sw.ElapsedMilliseconds);
        Assert.True(sw.ElapsedMilliseconds < 5000);
    });
}
