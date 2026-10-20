using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Features.SqlEditor;

namespace Oranda.App.Tests.Features.GridEdit;

public sealed partial class GridEditTests
{
    [Fact]
    public Task Edit_mode_shows_the_result_pane_toolbar_row_without_an_excel_button() => Ui.Run(async () =>
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        var pane = new SqlResultPaneViewModel();
        pane.SetResult(new MemoryResult(EmpCols, Data));
        pane.SetEditSession(s);
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Width = 500, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var row = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("panel-toolbar") && Grid.GetRow(b) == 3);
            Assert.True(pane.IsEditMode);
            Assert.True(row.IsVisible);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(), b => b.Tag is "oranda.grid.exportExcel");
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });
}
