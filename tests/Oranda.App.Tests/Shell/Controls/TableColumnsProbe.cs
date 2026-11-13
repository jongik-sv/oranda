using Avalonia;
using Avalonia.Controls;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Tests.Shell.Controls;

/// <summary>
/// 화면 표에 붙인 열 폭 끌기(TableColumns) 확인: 머리글 경계를 끌면 머리글과 보이는 모든 줄의 그 열이 같이 바뀐다.
/// 끝나면 「열 폭 처음으로」로 되돌려 저장 파일에 남기지 않는다(<see cref="TableColumnsDrag.ResetAll"/>).
/// 저장 폴더(DataDirectoryProvider)는 바꾸지 않는다: TableColumnsTests 가 같은 정적 값을 바꾸며 병렬로 돈다.
/// </summary>
internal static class TableColumnsProbe
{
    public static void AssertDragMovesHeaderAndRows(Window window, Grid header, Visual list, int column, double dx = 40)
    {
        Assert.True(TableColumns.GetHeader(header));
        Assert.NotEmpty(TableColumnsDrag.Rows(list));
        var before = header.ColumnDefinitions[column].ActualWidth;
        try
        {
            TableColumnsDrag.Drag(window, header, column, dx);
            window.UpdateLayout();
            Assert.Equal(before + dx, header.ColumnDefinitions[column].ActualWidth, 1);
            Assert.All(TableColumnsDrag.Rows(list), r => Assert.Equal(before + dx, r.ColumnDefinitions[column].ActualWidth, 1));
        }
        finally
        {
            TableColumnsDrag.ResetAll(window);
        }
    }
}
