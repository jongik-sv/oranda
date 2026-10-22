using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>TSK-13-17: 그리드 열을 끌어 옮겨도 편집 반영 SQL 은 원래 열(이름)로 만든다.</summary>
public sealed partial class GridEditTests
{
    [Fact]
    public async Task Edit_apply_sql_uses_original_columns_after_the_grid_columns_were_reordered()
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        await using var model = new ResultGridModel(new SaveTestResultSet());
        await model.LoadFirstPageAsync();
        GridEditBinding.Attach(model, s);
        Assert.True(model.MoveColumn(2, 0)); // SAL, EMPNO, ENAME 순으로 보인다

        var viewCol = model.VisibleColumns.ToList().IndexOf(1); // ENAME 의 화면 위치
        var ordinal = model.VisibleColumns[viewCol];
        Assert.True(GridEditBinding.TrySetCell(model, 0, ordinal, "ZED"));

        var preview = s.BuildPreview();
        Assert.Contains("UPDATE SCOTT.EMP SET ENAME = :v1 WHERE EMPNO = :k1", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("SAL", preview, StringComparison.Ordinal);
    }
}
