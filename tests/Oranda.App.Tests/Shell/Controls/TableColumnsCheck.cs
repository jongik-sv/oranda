using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Tests.Shell.Controls;

/// <summary>화면별 표에 붙인 열 폭 끌기(TableColumns) 확인: 머리글 경계를 끌면 줄의 열도 같이 바뀌고, 「열 폭 처음으로」로 되돌린다(저장도 지움).</summary>
internal static class TableColumnsCheck
{
    private static List<Grid> Rows(ListBox list) =>
        list.GetVisualDescendants().OfType<Grid>().Where(TableColumns.GetRow).ToList();

    /// <summary>
    /// <paramref name="header"/> 의 <paramref name="column"/> 오른쪽 경계를 <paramref name="dx"/> 만큼 끌어 머리글·모든 줄의 그 열이 함께 바뀌는지 본다.
    /// 끝에 머리글 오른쪽 클릭 메뉴로 처음 폭으로 되돌려 저장 파일에 남기지 않는다. <paramref name="whileDragged"/> = 끈 폭에서 더 볼 것.
    /// </summary>
    public static void DragMovesHeaderAndRows(Window window, Grid header, ListBox list, int column, double dx = 40, Action? whileDragged = null)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(Rows(list));
        Assert.All(Rows(list), r => Assert.Equal(header.ColumnDefinitions.Count, r.ColumnDefinitions.Count));
        var before = header.ColumnDefinitions[column].ActualWidth;
        var grip = header.Children.OfType<TableColumnGrip>().Single(g => g.Boundary == column);
        var at = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Point(dx / 2, 0));
        window.MouseMove(at + new Point(dx, 0));
        window.MouseUp(at + new Point(dx, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + dx, header.ColumnDefinitions[column].ActualWidth, 1);
        SameAsHeader(header, list);
        whileDragged?.Invoke();

        var reset = Assert.IsType<MenuItem>(Assert.Single(header.ContextMenu!.Items));
        reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, header.ColumnDefinitions[column].ActualWidth, 1);
        SameAsHeader(header, list);
    }

    /// <summary>머리글의 픽셀 열마다 모든 줄의 열 폭이 같다(Auto 열은 줄마다 내용이 달라 뺀다).</summary>
    private static void SameAsHeader(Grid header, ListBox list) =>
        Assert.All(Rows(list), r =>
        {
            for (var i = 0; i < header.ColumnDefinitions.Count; i++)
            {
                if (header.ColumnDefinitions[i].Width.IsAbsolute)
                {
                    Assert.Equal(header.ColumnDefinitions[i].ActualWidth, r.ColumnDefinitions[i].ActualWidth, 1);
                }
            }
        });
}
