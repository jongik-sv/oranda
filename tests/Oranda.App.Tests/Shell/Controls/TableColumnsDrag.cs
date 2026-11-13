using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Tests.Shell.Controls;

/// <summary>화면 시험이 머리글 경계를 끌어 열 폭을 바꿀 때 쓰는 도우미(TableColumns).</summary>
internal static class TableColumnsDrag
{
    /// <summary>머리글 Grid 의 경계 <paramref name="boundary"/>(그 열의 오른쪽) 손잡이를 <paramref name="dx"/> 만큼 끈다.</summary>
    public static void Drag(Window window, Grid header, int boundary, double dx)
    {
        var grip = header.Children.OfType<TableColumnGrip>().Single(g => g.Boundary == boundary);
        var from = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(dx / 2, 0));
        window.MouseMove(from + new Point(dx, 0));
        window.MouseUp(from + new Point(dx, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary><paramref name="root"/> 아래 줄 Grid(TableColumns.Row) 중 보이는 것.</summary>
    public static List<Grid> Rows(Visual root) =>
        root.GetVisualDescendants().OfType<Grid>().Where(g => TableColumns.GetRow(g) && g.IsEffectivelyVisible).ToList();

    /// <summary>
    /// <paramref name="root"/> 아래 머리글마다 오른쪽 클릭 「열 폭 처음으로」(저장된 폭도 지운다: 같은 시험 실행의 다른 시험이 기본 폭으로 시작하게).
    /// 단언이 실패해도 남지 않게 finally 에서 부른다.
    /// </summary>
    public static void ResetAll(Visual root)
    {
        foreach (var header in root.GetVisualDescendants().OfType<Grid>().Where(TableColumns.GetHeader).ToList())
        {
            if (header.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "열 폭 처음으로") is { } reset)
            {
                reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
        }

        Dispatcher.UIThread.RunJobs();
    }
}
